using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VatscaUpdateChecker.Services;

/// <summary>Bounded configuration reads and atomic replacement without retaining a credential backup.</summary>
internal static class ClientConfigurationFiles
{
    internal static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.Length > 2048 || path.Any(char.IsControl) || path.IndexOfAny(['"', '<', '>', '|', '*', '?']) >= 0 ||
            path.IndexOf(':', 2) >= 0 || path.Replace('/', '\\').Split('\\').Any(part => part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')))
            throw new InvalidDataException("The client location is not a supported local path.");
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        SaamanFileSafety.NoLinks(full);
        return full;
    }

    internal static byte[]? Read(string path, int maximumBytes)
    {
        path = ValidatePath(path);
        string directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory)) return null;
        using var parents = new SaamanFileSafety.DirectoryGuard(directory, false);
        try { _ = File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        using var file = SaamanFileSafety.OpenRead(path);
        RequireSingleLink(file.SafeFileHandle);
        if (file.Length > maximumBytes) throw new InvalidDataException("The client configuration exceeds the supported size.");
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        return bytes;
    }

    internal static void Write(string path, byte[]? expected, byte[] content, Action beforeCommit)
    {
        path = ValidatePath(path);
        if (content.Length > ClientSignInService.MaximumConfigurationBytes) throw new InvalidDataException("The client configuration exceeds the supported size.");
        using var parents = new SaamanFileSafety.DirectoryGuard(Path.GetDirectoryName(path)!, true);
        using (LockUnchanged(path, expected)) { }
        beforeCommit();
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, ".launchpad-signin-" + Guid.NewGuid().ToString("N") + ".tmp");
        using var handle = CreateFile(@"\\?\" + temporary, 0xC0010000, 0, IntPtr.Zero, 1, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("The client configuration could not be prepared.");
        bool committed = false;
        FileStream? output = null;
        try
        {
            RequireSingleLink(handle);
            output = new FileStream(handle, FileAccess.ReadWrite);
            output.Write(content);
            output.Flush(true);
            // The final process and byte checks happen after preparing the replacement. A failed
            // guard leaves the original in place and deletes the temporary through its own handle.
            beforeCommit();
            using var previous = LockUnchanged(path, expected);
            Rename(handle, path, replace: expected is not null);
            committed = true;
        }
        finally
        {
            try { if (!committed) Delete(handle); }
            finally { output?.Dispose(); }
        }
    }

    private static FileStream? LockUnchanged(string path, byte[]? expected)
    {
        ValidatePath(path);
        // Permit the atomic rename while denying in-place writes until it commits. The caller's
        // maintenance lease and closed-client guards remain required: this is not a filesystem CAS
        // against another program independently replacing the same directory entry.
        var handle = CreateFile(@"\\?\" + path, 0x80000000, 5, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (expected is null && error is 2 or 3) return null;
            throw new IOException("The client configuration could not be held for replacement.");
        }
        FileStream? stream = null;
        try
        {
            RequireSingleLink(handle);
            stream = new FileStream(handle, FileAccess.Read);
            if (expected is null || stream.Length != expected.Length || stream.Length > ClientSignInService.MaximumConfigurationBytes ||
                !CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), SHA256.HashData(expected)))
                throw new IOException("The client configuration changed while preparing it.");
            return stream;
        }
        catch { stream?.Dispose(); handle.Dispose(); throw; }
    }

    private static void Rename(SafeFileHandle handle, string path, bool replace)
    {
        byte[] name = Encoding.Unicode.GetBytes(@"\\?\" + path);
        int rootOffset = IntPtr.Size == 8 ? 8 : 4, lengthOffset = rootOffset + IntPtr.Size, nameOffset = lengthOffset + 4;
        int size = nameOffset + name.Length + 2;
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, memory, size);
            // FileRenameInfoEx: replace + POSIX semantics retain the old read handle until
            // replacement completes. This requires a filesystem supporting modern rename flags.
            Marshal.WriteInt32(memory, replace ? 3 : 0);
            Marshal.WriteInt32(memory, lengthOffset, name.Length);
            Marshal.Copy(name, 0, IntPtr.Add(memory, nameOffset), name.Length);
            // Rename the already-open temporary file. This replaces the destination entry;
            // it never opens a newly substituted symlink/hardlink and writes through it.
            if (!SetFileInformationByHandle(handle, replace ? 22 : 3, memory, (uint)size))
                throw new IOException("The client configuration could not be replaced.");
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private static void Delete(SafeFileHandle handle)
    {
        var memory = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(memory, 1);
            if (!SetFileInformationByHandle(handle, 4, memory, 4))
                throw new IOException("The temporary client configuration could not be removed.");
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private static void RequireSingleLink(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information) || information.NumberOfLinks != 1 ||
            (information.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("The client configuration is not a regular unlinked file.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            VolumeSerial, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, IntPtr information, uint size);
}
