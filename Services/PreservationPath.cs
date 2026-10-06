using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VatscaUpdateChecker.Services;

/// <summary>Resolves configured paths for preservation checks without reading file contents.</summary>
internal static class PreservationPath
{
    internal static string Normalize(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.IndexOfAny(['"', '\r', '\n']) >= 0)
            throw new InvalidDataException("A configured or recovery path cannot be safely compared with a removal folder. Use a local absolute path or handle removal manually.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full[2..].Contains(':') || full[3..].Split(Path.DirectorySeparatorChar).Any(part => part.EndsWith('.') || part.EndsWith(' ')))
            throw new InvalidDataException("A configured or recovery path contains an ambiguous Windows path component. Review that path before removal.");
        return full;
    }

    // A missing executable still protects its location. Resolve the nearest existing ancestor,
    // including junctions and short-path aliases, then append the missing path components.
    internal static string Resolve(string path)
    {
        path = Normalize(path);
        var missing = new Stack<string>();
        for (string? candidate = path; candidate != null; candidate = Path.GetDirectoryName(candidate))
        {
            bool entryExists;
            try { _ = File.GetAttributes(candidate); entryExists = true; }
            catch (FileNotFoundException) { entryExists = false; }
            catch (DirectoryNotFoundException) { entryExists = false; }
            using var handle = CreateFileW(@"\\?\" + candidate, 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                if (!entryExists && error is 2 or 3) { missing.Push(Path.GetFileName(candidate)); continue; }
                throw new IOException("A configured or recovery path could not be resolved safely before removal.", new System.ComponentModel.Win32Exception(error));
            }
            var buffer = new StringBuilder(32768);
            uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity)
                throw new IOException("A configured or recovery path could not be resolved safely before removal.");
            string resolved = buffer.ToString();
            if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
            resolved = Normalize(resolved);
            foreach (var part in missing) resolved = Path.Combine(resolved, part);
            return resolved;
        }
        throw new IOException("A configured or recovery path has no accessible local ancestor. Review it before removal.");
    }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint characters, uint flags);
}
