using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Security;

namespace VatscaUpdateChecker.Services;

/// <summary>Blocks shared ATC file changes while any EuroScope instance is running.</summary>
internal static class EuroScopeProcessGuard
{
    private const string RunningReason = "EuroScope is running. Close all EuroScope instances before changing GNG or VatEFS files.";
    private const string UnknownReason = "Launchpad could not check whether EuroScope is running. Close all EuroScope instances and try again before changing GNG or VatEFS files.";

    public static string? GetBlockingReason() => GetBlockingReason(IsProcessRunning);

    internal static string? GetBlockingReason(Func<string, bool> isProcessRunning)
    {
        ArgumentNullException.ThrowIfNull(isProcessRunning);
        try { return isProcessRunning("EuroScope") ? RunningReason : null; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException or SecurityException)
        {
            return UnknownReason;
        }
    }

    public static void RequireClosed() => RequireClosed(IsProcessRunning);

    internal static void RequireClosed(Func<string, bool> isProcessRunning)
    {
        if (GetBlockingReason(isProcessRunning) is { } reason)
            throw new InvalidOperationException(reason);
    }

    private static bool IsProcessRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
