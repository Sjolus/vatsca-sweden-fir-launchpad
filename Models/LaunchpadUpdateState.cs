namespace VatscaUpdateChecker.Models;

public enum LaunchpadUpdateStatus
{
    Unsupported,
    Idle,
    Checking,
    NoFeed,
    UpToDate,
    Available,
    Downloading,
    Ready,
    Error
}

public sealed record LaunchpadUpdateState(
    LaunchpadUpdateStatus Status,
    string Message,
    string? CurrentVersion = null,
    string? AvailableVersion = null,
    int? ProgressPercent = null);
