using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

internal static class SyntheticJunction
{
    internal static void Create(string link, string target)
    {
        string allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Launchpad-uninstall-tests")) + Path.DirectorySeparatorChar;
        foreach (string path in new[] { link, target })
            if (!Path.GetFullPath(path).StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Junction fixture escaped its temporary root.");
        Directory.CreateDirectory(link);
        using var handle = CreateFileW(link, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
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
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int size, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
}
