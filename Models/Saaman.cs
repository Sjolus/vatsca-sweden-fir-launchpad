namespace VatscaUpdateChecker.Models;

public sealed record SaamanAsset(string Name, Uri DownloadUri, long Size, string Sha256);
public sealed record SaamanRelease(string Version, string Tag, IReadOnlyList<SaamanAsset> Assets)
{
    public SaamanAsset Dll => Assets.Single(asset => asset.Name == "SAAMAN.dll");
    public string ReleaseUrl => $"https://github.com/Kaskobiii/SAAMAN/releases/tag/{Uri.EscapeDataString(Tag)}";
}
public sealed record SaamanDllInfo(string Path, string Version, long Size, string Sha256);
public sealed record SaamanInstallPlan(string TargetPath, SaamanRelease Release, SaamanDllInfo? Previous,
    IReadOnlyList<string> ProtectedPaths);
public sealed record SaamanProgress(string Message, double? Percent = null);
public sealed record SaamanInstallResult(string DllPath, string Version, string ReceiptPath);
public sealed record SaamanRecovery(string ReceiptPath, string TargetPath, string Version, string? PreviousVersion,
    string InstalledHash, string? PreviousHash, long PreviousSize, string BackupPath);

/// <summary>Shared by the management dialog and inert visual review fixtures.</summary>
public static class SaamanReviewPresentation
{
    public static string Format(SaamanInstallPlan plan, bool runtimeAvailable)
    {
        string heading = plan.Previous is null
            ? $"Install SAAMAN {plan.Release.Version}"
            : $"Update SAAMAN {plan.Previous.Version} → {plan.Release.Version}";
        string changes = plan.Previous is null
            ? "Install the verified plugin DLL. Keep the download, license notices and installation receipt. A fresh installation has no previous DLL to restore."
            : "Replace the selected DLL after saving a verified recovery copy. Restore is available while the installed copy remains unchanged.";
        return $"{heading}\nLatest stable release from Kaskobiii/SAAMAN\n\n" +
            $"Destination\n{plan.TargetPath}\n\n" +
            $"What changes\n{changes}\n\n" +
            "What stays\nSAAMAN preferences, browser data and all EuroScope profiles.\n\n" +
            "Next step (optional)\nEnable SAAMAN for your selected profile in Controller profile.\n\n" +
            (runtimeAvailable
                ? "WebView2 Runtime: available."
                : "WebView2 Runtime: missing. Use Check WebView2 requirement before loading SAAMAN.");
    }
}
