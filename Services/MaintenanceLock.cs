using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace VatscaUpdateChecker.Services;

/// <summary>Serializes destructive maintenance across this user's Launchpad processes and sessions.</summary>
public static class MaintenanceLock
{
    private static int _held;
    public static bool IsHeldByCurrentProcess => Volatile.Read(ref _held) != 0;

    public static bool TryAcquire(out IDisposable? lease)
    {
        lease = null;
        try { return TryAcquire(GetName(), out lease); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        { return false; }
    }

    internal static bool TryAcquire(string name, out IDisposable? lease)
    {
        lease = null;
        try
        {
            if (HandoffActive(name)) return false;
            if (!TryAcquireMutex(name, out lease)) return false;
            // Close the check/acquire race with an uninstall helper taking over an existing lease.
            if (!HandoffActive(name)) return true;
            lease!.Dispose(); lease = null;
            return false;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            lease?.Dispose(); lease = null;
            return false;
        }
    }

    internal static EventWaitHandle BeginUninstallHandoff() => BeginUninstallHandoff(GetName());
    internal static EventWaitHandle BeginUninstallHandoff(string name) => new(true, EventResetMode.ManualReset, name + ".Uninstall");
    internal static bool HandoffActive(string name)
    {
        try { using var handoff = EventWaitHandle.OpenExisting(name + ".Uninstall"); return handoff.WaitOne(0); }
        catch (WaitHandleCannotBeOpenedException) { return false; }
    }

    private static bool TryAcquireMutex(string name, out IDisposable? lease)
    {
        lease = null;
        // Mutex ownership is thread-affine. A dedicated owner permits callers to release after await.
        var acquired = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        bool ownsMutex = false;
        var owner = new Thread(() =>
        {
            Mutex? mutex = null;
            try
            {
                mutex = new Mutex(false, name);
                try { ownsMutex = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { ownsMutex = true; }
                if (ownsMutex) Interlocked.Increment(ref _held);
            }
            catch { ownsMutex = false; }
            finally { acquired.Set(); }
            if (!ownsMutex) { mutex?.Dispose(); return; }
            try { release.Wait(); }
            finally
            {
                try { mutex!.ReleaseMutex(); }
                finally { Interlocked.Decrement(ref _held); mutex!.Dispose(); }
            }
        }) { IsBackground = true, Name = "Launchpad maintenance lock" };
        owner.Start();
        acquired.Wait();
        if (!ownsMutex)
        {
            owner.Join();
            acquired.Dispose();
            release.Dispose();
            return false;
        }
        lease = new Lease(owner, release, acquired);
        return true;
    }

    private static string GetName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User?.Value ?? throw new InvalidOperationException("Windows user identity is unavailable.");
        return @"Global\SwedenFirLaunchpad.Maintenance." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user)));
    }

    private sealed class Lease(Thread owner, ManualResetEventSlim release, ManualResetEventSlim acquired) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            release.Set();
            owner.Join();
            acquired.Dispose();
            release.Dispose();
        }
    }
}
