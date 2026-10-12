using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace VatscaUpdateChecker.Services;

public sealed record ApplicationProcessIdentity(int Id, DateTime StartedUtc, string ExecutablePath);
public enum RunningApplicationAction { Show, CloseNormally, ForceStop }

/// <summary>Actions target a verified executable and process lifetime, never a name-only match.</summary>
public static class ApplicationProcessControl
{
    private const uint QueryLimitedInformation = 0x1000;
    private const uint Terminate = 0x0001;
    private const uint StillActive = 259;

    public static bool SameIdentity(ApplicationProcessIdentity expected, ApplicationProcessIdentity actual) =>
        expected.Id == actual.Id && expected.StartedUtc == actual.StartedUtc &&
        !string.IsNullOrWhiteSpace(expected.ExecutablePath) &&
        string.Equals(expected.ExecutablePath, actual.ExecutablePath, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<ApplicationProcessIdentity> Find(string executablePath)
    {
        var matches = new List<ApplicationProcessIdentity>();
        if (string.IsNullOrWhiteSpace(executablePath)) return matches;
        string fullPath;
        try { fullPath = Path.GetFullPath(executablePath); }
        catch { return matches; }
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(fullPath)))
        {
            using (process)
            {
                try
                {
                    var identity = Identity(process);
                    if (string.Equals(identity.ExecutablePath, fullPath, StringComparison.OrdinalIgnoreCase))
                        matches.Add(identity);
                }
                catch (Exception ex) when (IsProcessError(ex)) { }
            }
        }
        return matches;
    }

    public static ApplicationProcessIdentity Identity(Process process)
    {
        var id = process.Id;
        using var pin = OpenPinnedProcess(id);
        return ReadIdentity(pin, id);
    }

    public static bool HasWindow(ApplicationProcessIdentity expected)
    {
        try
        {
            using var pin = OpenPinnedProcess(expected.Id);
            if (!SameIdentity(expected, ReadIdentity(pin, expected.Id))) return false;
            using var process = Process.GetProcessById(expected.Id);
            return process.MainWindowHandle != IntPtr.Zero;
        }
        catch (Exception ex) when (IsProcessError(ex)) { return false; }
    }

    public static bool IsAlive(ApplicationProcessIdentity expected)
    {
        try
        {
            using var pin = OpenPinnedProcess(expected.Id);
            return SameIdentity(expected, ReadIdentity(pin, expected.Id));
        }
        catch (Exception ex) when (IsProcessError(ex)) { return false; }
    }

    public static bool TryAct(ApplicationProcessIdentity expected, RunningApplicationAction action)
    {
        try
        {
            // Keep this handle through the action so a terminated process's PID cannot
            // be reused. Only ForceStop requests termination rights; detection and Show
            // must not need the broad access requested by Process.Handle/MainModule.
            using var pin = OpenPinnedProcess(expected.Id,
                action == RunningApplicationAction.ForceStop ? Terminate : 0);
            if (!SameIdentity(expected, ReadIdentity(pin, expected.Id))) return false;
            using var process = Process.GetProcessById(expected.Id);
            switch (action)
            {
                case RunningApplicationAction.Show:
                    var handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero) return false;
                    if (IsIconic(handle)) ShowWindowAsync(handle, 9 /* SW_RESTORE */);
                    return SetForegroundWindow(handle);
                case RunningApplicationAction.CloseNormally:
                    return process.CloseMainWindow();
                case RunningApplicationAction.ForceStop:
                    // Only this reviewed process. Do not sweep other installations or process trees.
                    return TerminateProcess(pin, unchecked((uint)-1));
                default: return false;
            }
        }
        catch (Exception ex) when (IsProcessError(ex)) { return false; }
    }

    private static SafeProcessHandle OpenPinnedProcess(int id, uint additionalAccess = 0)
    {
        var pin = OpenProcess(QueryLimitedInformation | additionalAccess, false, id);
        if (!pin.IsInvalid) return pin;
        var error = Marshal.GetLastWin32Error();
        pin.Dispose();
        throw new Win32Exception(error);
    }

    private static ApplicationProcessIdentity ReadIdentity(SafeProcessHandle pin, int id)
    {
        if (!GetProcessTimes(pin, out var created, out _, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var path = new StringBuilder(32768);
        var capacity = path.Capacity;
        if (!QueryFullProcessImageName(pin, 0, path, ref capacity))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!GetExitCodeProcess(pin, out var exitCode))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (exitCode != StillActive)
            throw new InvalidOperationException("The application has already exited.");
        var started = ((long)created.dwHighDateTime << 32) | (uint)created.dwLowDateTime;
        return new(id, DateTime.FromFileTimeUtc(started), path.ToString());
    }

    private static bool IsProcessError(Exception ex) => ex is InvalidOperationException or
        ArgumentException or System.ComponentModel.Win32Exception or NotSupportedException or
        System.Security.SecurityException or UnauthorizedAccessException;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out FILETIME creation,
        out FILETIME exit, out FILETIME kernel, out FILETIME user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags,
        StringBuilder imageName, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
}
