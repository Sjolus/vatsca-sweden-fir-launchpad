namespace VatscaUpdateChecker.Services;

// Defaults must never inspect the machine, trust store or a real maintenance session in this harness.
// Every service test injects both probes; filesystem tests use only the temporary fixture root.
internal static class SoftwareInstallerNative
{
    internal static void VerifyPublisher(string path, string publisher) => throw new InvalidOperationException("Publisher probe was not injected.");
}

internal static class MaintenanceLock
{
    public static bool IsHeldByCurrentProcess => throw new InvalidOperationException("Maintenance lease was not injected.");
}
