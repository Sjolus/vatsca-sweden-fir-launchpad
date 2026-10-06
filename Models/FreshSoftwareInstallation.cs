namespace VatscaUpdateChecker.Models;

public enum FreshSoftwareInstallPhase { Checking, Downloading, Verifying, BackingUp, Installing, Completed }
public sealed record FreshSoftwareInstallProgress(FreshSoftwareInstallPhase Phase, string Message,
    double? Percent = null, bool CanCancel = true);
public sealed record FreshSoftwareInstallResult(string ExecutablePath, string? BackupFolder, bool RestartRequired);

/// <summary>A reviewed fresh-install plan, with immutable release metadata and existing data snapshot.</summary>
public sealed class FreshSoftwareInstallPlan
{
    internal FreshSoftwareInstallPlan(SoftwareRelease release, string executablePath, string installRoot,
        string scope, RemovalFilePlan backupFiles, string? backupDestination, string review,
        string configuredExePath, IEnumerable<string> installCandidates, IEnumerable<string> missingDataRoots, bool allowVatisBeta)
    {
        Release = release; ExecutablePath = executablePath; InstallRoot = installRoot; Scope = scope;
        BackupFiles = backupFiles; BackupDestination = backupDestination; Review = review;
        ConfiguredExePath = configuredExePath;
        InstallCandidates = Array.AsReadOnly(installCandidates.ToArray());
        MissingDataRoots = Array.AsReadOnly(missingDataRoots.ToArray());
        AllowVatisBeta = allowVatisBeta;
    }
    public SoftwareApp App => Release.App;
    public SoftwareRelease Release { get; }
    public string ExecutablePath { get; }
    public string InstallRoot { get; }
    public string Scope { get; }
    public RemovalFilePlan BackupFiles { get; }
    public string? BackupDestination { get; }
    public string Review { get; }
    public bool AllowVatisBeta { get; }
    internal string ConfiguredExePath { get; }
    internal IReadOnlyList<string> InstallCandidates { get; }
    internal IReadOnlyList<string> MissingDataRoots { get; }
}
