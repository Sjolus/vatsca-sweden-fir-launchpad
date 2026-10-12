using System.Text.Json.Serialization;

namespace VatscaUpdateChecker.Models;

public enum ActivityApplication { EuroScope, Gng, TrackAudio, Vacs, Vatis, VatEfs, Launchpad, Maintenance }
public enum ActivityOperation { Install, Update, Repair, Replace, Remove, Cleanup, Restore, RestoreCleanup, Prerequisite }
public enum ActivityOutcome { Completed, Stopped, Cancelled, NeedsAttention }

/// <summary>Only public package metadata and local recovery locations belong in activity history.</summary>
public sealed record ActivityEntry(Guid Id, DateTimeOffset Time, ActivityApplication Application,
    ActivityOperation Operation, ActivityOutcome Outcome, string? Version = null,
    string? RecoveryFolder = null, string? DataFolder = null, bool RestartRequired = false, string? PreviousVersion = null)
{
    [JsonIgnore] public string ApplicationName => Application switch
    {
        ActivityApplication.Gng => "Swedish GNG", ActivityApplication.Vacs => "VACS",
        ActivityApplication.Vatis => "vATIS", ActivityApplication.VatEfs => "VatEFS",
        ActivityApplication.Launchpad => "Launchpad", ActivityApplication.Maintenance => "Selected applications and data",
        _ => Application.ToString()
    };
    [JsonIgnore] public string OperationName => Operation switch
    {
        ActivityOperation.Install => "Installation", ActivityOperation.Update => "Update",
        ActivityOperation.Repair => "Repair", ActivityOperation.Replace => "Replacement",
        ActivityOperation.Remove => "Removal", ActivityOperation.Cleanup => "Old-file cleanup",
        ActivityOperation.Restore or ActivityOperation.RestoreCleanup => "Backup restore",
        ActivityOperation.Prerequisite => "Runtime installation", _ => "Change"
    };
    [JsonIgnore] public string OutcomeName => Outcome switch
    {
        ActivityOutcome.Completed => "Completed", ActivityOutcome.Stopped => "Stopped",
        ActivityOutcome.Cancelled => "Cancelled", _ => "Needs attention"
    };
    [JsonIgnore] public string Summary => $"{ApplicationName} · {OperationName.ToLowerInvariant()} {OutcomeName.ToLowerInvariant()}" +
        (Version is null ? "" : $" · {VersionChange}") + (RestartRequired ? " · Restart was requested" : "");
    [JsonIgnore] public string VersionChange => PreviousVersion is null || PreviousVersion == Version
        ? Version ?? "" : $"{PreviousVersion} → {Version}";
    [JsonIgnore] public string DisplayTime => Time.ToLocalTime().ToString("g");
    [JsonIgnore] public string Guidance => Outcome switch
    {
        ActivityOutcome.Stopped or ActivityOutcome.NeedsAttention =>
            "The operation did not finish cleanly. Check the application's current state and keep any recovery copy before trying again.",
        ActivityOutcome.Cancelled => "Preparation was cancelled. Review the application again before retrying.",
        _ => "The operation finished. This is a record of that result; it does not check the application's current state."
    };
}
