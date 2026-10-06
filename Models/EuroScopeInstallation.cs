using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker.Models;

public enum EuroScopeInstallAction { Install, Repair, Replace }
public enum EuroScopeInstallPhase { Checking, Downloading, Verifying, BackingUp, Installing, Completed }
public sealed record EuroScopeInstallProgress(EuroScopeInstallPhase Phase, string Message, double? Percent = null, bool CanCancel = true);
public sealed record EuroScopeInstallResult(string ExecutablePath, string? BackupFolder, bool RestartRequired,
    EuroScopeInstallAction Action, bool InstallationCompleted = true);

/// <summary>Created only by Preview. Holds the reviewed bytes and paths, not a live settings reference.</summary>
public sealed class EuroScopeInstallPlan
{
    internal EuroScopeInstallPlan(EuroScopeInstallAction action, string? installedVersion, bool isDowngrade,
        string executablePath, string installRoot, string scope, RemovalFilePlan backupFiles,
        string? backupDestination, string review, AppSettings settings, AtcRemovalTarget? target,
        AtcMsiRegistration? registration, string msiexec, string msiexecHash, RemovalFilePlan installFiles,
        RemovalFilePlan externalDataFiles, IEnumerable<string> missingPaths, string dataRoot)
    {
        Action = action; InstalledVersion = installedVersion; IsDowngrade = isDowngrade;
        ExecutablePath = executablePath; InstallRoot = installRoot; Scope = scope;
        BackupFiles = backupFiles; BackupDestination = backupDestination; Review = review;
        Settings = new() { EuroscopeExePath = settings.EuroscopeExePath, EuroscopeDataPath = settings.EuroscopeDataPath };
        Target = target; Registration = registration; MsiexecPath = msiexec; MsiexecHash = msiexecHash;
        InstallFiles = installFiles; ExternalDataFiles = externalDataFiles;
        MissingPaths = Array.AsReadOnly(missingPaths.ToArray());
        DataRoot = dataRoot;
    }
    public EuroScopeInstallAction Action { get; }
    public string? InstalledVersion { get; }
    public string TargetVersion => EuroScopePolicy.SupportedVersion;
    public bool IsDowngrade { get; }
    public string ExecutablePath { get; }
    public string InstallRoot { get; }
    public string Scope { get; }
    public RemovalFilePlan BackupFiles { get; }
    public string? BackupDestination { get; }
    public string Review { get; }
    internal AppSettings Settings { get; }
    internal AtcRemovalTarget? Target { get; }
    internal AtcMsiRegistration? Registration { get; }
    internal string MsiexecPath { get; }
    internal string MsiexecHash { get; }
    internal RemovalFilePlan InstallFiles { get; }
    internal RemovalFilePlan ExternalDataFiles { get; }
    internal IReadOnlyList<string> MissingPaths { get; }
    internal string DataRoot { get; }
}
