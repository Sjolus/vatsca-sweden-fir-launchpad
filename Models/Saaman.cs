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
