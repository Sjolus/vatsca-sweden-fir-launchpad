using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

// Native helpers operate only on the caller's named synthetic temporary files.
internal static class SyntheticNative
{
    internal static void Junction(string link, string target)
    {
        ValidateSynthetic(link); ValidateSynthetic(target);
        Directory.CreateDirectory(link);
        using var handle = CreateFile(link, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        var print = Encoding.Unicode.GetBytes(target);
        var bytes = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BitConverter.GetBytes(unchecked((uint)0xA0000003)).CopyTo(bytes, 0);
        BitConverter.GetBytes(checked((ushort)(bytes.Length - 8))).CopyTo(bytes, 4);
        BitConverter.GetBytes(checked((ushort)substitute.Length)).CopyTo(bytes, 10);
        BitConverter.GetBytes(checked((ushort)(substitute.Length + 2))).CopyTo(bytes, 12);
        BitConverter.GetBytes(checked((ushort)print.Length)).CopyTo(bytes, 14);
        substitute.CopyTo(bytes, 16); print.CopyTo(bytes, 16 + substitute.Length + 2);
        if (!DeviceIoControl(handle, 0x000900A4, bytes, bytes.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
    }
    internal static void MakeSparse(SafeFileHandle handle)
    {
        if (!DeviceIoControl(handle, 0x000900C4, null, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new IOException(new Win32Exception(Marshal.GetLastWin32Error()).Message);
    }
    private static void ValidateSynthetic(string path)
    {
        var relative = Path.GetRelativePath(Path.GetTempPath(), Path.GetFullPath(path));
        if (!relative.StartsWith("LaunchpadRemovalSynthetic-", StringComparison.Ordinal) || relative.StartsWith("..", StringComparison.Ordinal))
            throw new IOException("Native fixture path escaped the synthetic temporary root.");
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code, byte[]? input, int inputSize,
        IntPtr output, int outputSize, out int returned, IntPtr overlapped);
}
