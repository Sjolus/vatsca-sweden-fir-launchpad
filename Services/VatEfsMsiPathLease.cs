using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VatscaUpdateChecker.Services;

/// <summary>Keeps the verified package and system-installer directories from being renamed while MSI opens them.</summary>
internal sealed class VatEfsMsiPathLease : IDisposable
{
    private readonly List<SafeFileHandle> _handles = [];
    internal VatEfsMsiPathLease(params string[] files) : this(files, null) { }

    internal VatEfsMsiPathLease(string[] files, Action<string>? directoryLocked)
    {
        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                VatEfsInstaller.RejectReparse(file);
                var ancestors = new Stack<string>();
                for (string? directory = Path.GetDirectoryName(VatEfsInstaller.FullPath(file)); directory != null; directory = Path.GetDirectoryName(directory))
                    ancestors.Push(directory);
                // Pin every parent before opening its child. Leaf-first acquisition could follow
                // a temporarily redirected ancestor and retain a handle to an unrelated directory.
                foreach (string directory in ancestors)
                {
                    if (!seen.Add(directory)) continue;
                    // FILE_LIST_DIRECTORY participates in sharing checks. A metadata-only
                    // FILE_READ_ATTRIBUTES handle does not reliably prevent directory renames.
                    var handle = CreateFileW(directory, 0x81, 3, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
                    if (handle.IsInvalid)
                    {
                        int error = Marshal.GetLastWin32Error();
                        handle.Dispose();
                        throw new IOException("The verified VatEFS installer path could not be protected.", new Win32Exception(error));
                    }
                    _handles.Add(handle);
                    if (!GetFileInformationByHandleEx(handle, 9, out var attributes, (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
                        throw new IOException("The protected VatEFS installer directory could not be inspected.", new Win32Exception(Marshal.GetLastWin32Error()));
                    RequireDirectoryAttributes(attributes.Attributes);
                    directoryLocked?.Invoke(directory);
                }
                VatEfsInstaller.RejectReparse(file);
            }
        }
        catch { Dispose(); throw; }
    }
    internal static void RequireDirectoryAttributes(uint attributes)
    {
        if ((attributes & 0x10) == 0 || (attributes & 0x400) != 0)
            throw new IOException("The protected VatEFS installer path is not an ordinary directory.");
    }
    public void Dispose() { foreach (var handle in _handles) handle.Dispose(); _handles.Clear(); }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo { public uint Attributes, ReparseTag; }
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileAttributeTagInfo information, uint size);
}
