using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VatscaUpdateChecker.Services;

/// <summary>Holds ancestor identities and hashes the same file handle used for replacement/deletion.</summary>
internal static class SaamanFileSafety
{
    internal const long MaximumDllSize = 64L * 1024 * 1024;
    internal static string Canonical(string path)
    {
        path = PreservationPath.Normalize(path);
        NoLinks(path);
        path = PreservationPath.Resolve(path);
        NoLinks(path);
        return path;
    }
    internal static void NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("SAAMAN management does not follow symbolic links or junctions.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
    }
    internal static bool Overlaps(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
        a.StartsWith(b.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ||
        b.StartsWith(a.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    internal static (long Size, string Hash) Snapshot(string path)
    {
        using var parents = new DirectoryGuard(Path.GetDirectoryName(path)!, false);
        using var file = Open(path, false);
        if (file.Length > MaximumDllSize) throw new IOException("The SAAMAN file exceeds the safe inspection limit.");
        return (file.Length, Hash(file));
    }
    internal static string Hash(Stream stream) => Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    internal static void Copy(string source, string destination, long length, string hash)
    {
        using var sourceParents = new DirectoryGuard(Path.GetDirectoryName(source)!, false);
        using var destinationParents = new DirectoryGuard(Path.GetDirectoryName(destination)!, true);
        using var input = Open(source, false);
        Verify(input, length, hash);
        input.Position = 0;
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Flush(true);
    }
    internal static void Delete(string path, long length, string hash)
    {
        using var parents = new DirectoryGuard(Path.GetDirectoryName(path)!, false);
        using var input = Open(path, true);
        Verify(input, length, hash);
        var memory = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(memory, 1);
            if (!SetFileInformationByHandle(input.SafeFileHandle, 4, memory, 4))
                throw NativeError("Could not remove the verified SAAMAN file");
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    internal static void Move(string source, string destination, long length, string hash)
    {
        using var sourceParents = new DirectoryGuard(Path.GetDirectoryName(source)!, false);
        using var destinationParents = new DirectoryGuard(Path.GetDirectoryName(destination)!, true);
        using var input = Open(source, true);
        Verify(input, length, hash);
        byte[] name = Encoding.Unicode.GetBytes(Extended(destination));
        int rootOffset = IntPtr.Size == 8 ? 8 : 4, lengthOffset = rootOffset + IntPtr.Size, nameOffset = lengthOffset + 4;
        int size = nameOffset + name.Length + 2;
        var memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, memory, size);
            Marshal.WriteInt32(memory, lengthOffset, name.Length);
            Marshal.Copy(name, 0, IntPtr.Add(memory, nameOffset), name.Length);
            if (!SetFileInformationByHandle(input.SafeFileHandle, 3, memory, (uint)size))
                throw NativeError("Could not move the verified SAAMAN file; existing files are never overwritten");
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    private static void Verify(FileStream stream, long length, string hash)
    {
        if (stream.Length != length || !Hash(stream).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("A SAAMAN file changed after review. The changed file was retained.");
    }
    internal static FileStream OpenRead(string path) => Open(path, false);
    internal static FileStream OpenReadWrite(string path)
    {
        var handle = CreateFile(Extended(path), 0xC0000000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw NativeError("Could not lock the SAAMAN profile"); }
        try { Check(handle, false); return new FileStream(handle, FileAccess.ReadWrite); }
        catch { handle.Dispose(); throw; }
    }
    private static FileStream Open(string path, bool mutation)
    {
        var handle = CreateFile(Extended(path), 0x80000000u | (mutation ? 0x10000u : 0), 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw NativeError("Could not lock the SAAMAN file"); }
        try { Check(handle, false); return new FileStream(handle, FileAccess.Read); }
        catch { handle.Dispose(); throw; }
    }
    internal sealed class DirectoryGuard : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = [];
        internal DirectoryGuard(string directory, bool create)
        {
            var chain = new Stack<string>();
            for (string? path = Path.GetFullPath(directory); path is not null; path = Path.GetDirectoryName(path)) chain.Push(path);
            try
            {
                foreach (string path in chain)
                {
                    if (create && !Directory.Exists(path)) Directory.CreateDirectory(path);
                    var handle = CreateFile(Extended(path), 0x80, 3, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
                    if (handle.IsInvalid) { handle.Dispose(); throw NativeError("Could not hold a SAAMAN directory"); }
                    _handles.Add(handle);
                    Check(handle, true);
                }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            for (int i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose();
            _handles.Clear();
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeInfo { public uint Attributes; public uint Tag; }
    private static void Check(SafeFileHandle handle, bool directory)
    {
        if (!GetFileInformationByHandleEx(handle, 9, out var info, 8)) throw NativeError("Could not verify a SAAMAN file handle");
        if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 ||
            ((info.Attributes & (uint)FileAttributes.Directory) != 0) != directory)
            throw new IOException("A SAAMAN path changed its type or became a link.");
    }
    private static IOException NativeError(string message) => new(message + ": " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
    private static string Extended(string path) => @"\\?\" + Path.GetFullPath(path);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, IntPtr info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out AttributeInfo info, uint size);
}
