namespace VatscaUpdateChecker.Services;

internal sealed record SoftwareInstallCommand(string Executable, IReadOnlyList<string> Arguments, bool Elevate)
{
    public bool IsMsi { get; init; }
}

// Every adapter fixture supplies a non-executing runner and synthetic trust result.
// The real signature reader also needs the installer's shared path check.
internal static class SoftwareInstaller
{
    internal static void RejectReparse(string path) => VatEfsInstaller.RejectReparse(path);
}
