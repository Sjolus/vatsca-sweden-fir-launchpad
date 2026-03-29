using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VatscaUpdateChecker.Services;

/// <summary>
/// Native process-enumeration helpers used to reliably track browser processes that
/// may relaunch themselves (e.g. Edge first-run profile setup).
/// </summary>
internal static class ProcessHelper
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public uint   dwSize;
        public uint   cntUsage;
        public uint   th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint   th32ModuleID;
        public uint   cntThreads;
        public uint   th32ParentProcessID;
        public int    pcPriClassBase;
        public uint   dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Returns a PID → parent-PID map for every process currently on the system.
    /// </summary>
    private static Dictionary<int, int> GetParentPidMap()
    {
        var map      = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(0x00000002 /* TH32CS_SNAPPROCESS */, 0);
        if (snapshot == (IntPtr)(-1)) return map;

        var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
        try
        {
            if (Process32First(snapshot, ref entry))
                do { map[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID; }
                while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }

        return map;
    }

    /// <summary>
    /// Finds the Edge browser process (msedge.exe) that was spawned at or after
    /// <paramref name="notBefore"/> and is the root of its process tree — i.e. its
    /// parent is not another msedge process (renderers/GPU/network processes are children
    /// of the browser, not the other way around).
    /// Returns -1 if not found.
    /// </summary>
    public static int FindEdgeBrowserProcess(DateTime notBefore)
    {
        var msedgeProcs = Process.GetProcessesByName("msedge");
        if (msedgeProcs.Length == 0) return -1;

        var msedgePids = new HashSet<int>();
        foreach (var p in msedgeProcs) msedgePids.Add(p.Id);

        var parentMap = GetParentPidMap();

        foreach (var proc in msedgeProcs)
        {
            try
            {
                if (proc.StartTime < notBefore) continue;

                // Renderers / GPU / network processes are children of the browser —
                // their parent PID is also msedge. Skip those.
                if (parentMap.TryGetValue(proc.Id, out var parentPid) && msedgePids.Contains(parentPid))
                    continue;

                return proc.Id;
            }
            catch { /* process may have exited between enumeration and StartTime query */ }
        }

        return -1;
    }
}
