namespace VatscaUpdateChecker.Models;

public enum SoftwareApp { Vacs, Vatis, TrackAudio, VatEfs }

public sealed record SoftwareRelease(SoftwareApp App, string Version, Uri DownloadUri,
    string FileName, string Sha256, long Size)
{
    public bool IsPrerelease { get; init; }
}

public sealed record SoftwareInstallation(SoftwareApp App, string ExePath, string Version,
    string RootPath, string Scope, string? UpdaterPath, bool CanUpdate, string? Reason);

public sealed record SoftwareInstallResult(string ExecutablePath);

public enum SoftwareUpdatePhase
{
    Idle, Checking, Available, Current, Unavailable, Downloading, Verifying,
    BackingUp, Installing, Completed, Cancelled, Error
}

public sealed record SoftwareUpdateState(SoftwareApp App, SoftwareUpdatePhase Phase,
    string Message, SoftwareInstallation? Installation = null, SoftwareRelease? Release = null,
    double? ProgressPercent = null, string? BackupFolder = null)
{
    public bool IsBusy => Phase is SoftwareUpdatePhase.Checking or SoftwareUpdatePhase.Downloading
        or SoftwareUpdatePhase.Verifying or SoftwareUpdatePhase.BackingUp or SoftwareUpdatePhase.Installing;
    public bool CanCancel => IsBusy && Phase != SoftwareUpdatePhase.Installing;
    public bool CanUpdate => Phase == SoftwareUpdatePhase.Available;
    public string? InstalledVersion => Installation?.Version;
    public string? LatestVersion => Release?.Version;
}
