using System.Text.Json.Serialization;

namespace VatscaUpdateChecker.Models;

public enum ReportingApplication { Launchpad, Euroscope, Gng, Trackaudio, Vacs, Vatis, Vatefs, Vatiris, Maintenance }
public enum ReportingCategory { Setup, Installation, Update, Gng, Launch, Appearance, Other }
public enum ReportingEventName { SessionStarted, SetupStep, OperationStarted, OperationFinished, GngPackageObtained, PreferenceChanged, OperationFailed, UnhandledError, ApplicationOpened, FeatureUsed, PreferencesSnapshot, InventorySnapshot }
public enum ReportingFeature { CheckUpdates, ControllerProfileReview, GngPackageReview, GngCleanupReview, GngRestoreReview, SetupGuide, InstallOrUpdate, Adopt, FontCheck }
public enum ReportingTheme { Dark, Light }
public enum ReportingTextScale { Standard, Larger, Largest }
public enum ReportingInventoryComponent { Euroscope, Gng, Trackaudio, Vacs, Vatis, Vatefs, Topsky, Rdf, VatefsPlugin, AfvEuroscopeBridge }
public enum ReportingInventoryStatus { Known, Unknown, Absent, Unavailable }
public enum ReportingInventorySource { ConfiguredApp, RegisteredMsi, PackageMetadata, PluginFile }
public sealed record ReportingInventoryEntry(ReportingInventoryComponent Component, ReportingInventoryStatus Status, ReportingInventorySource Source, string? Version = null);
public sealed record ReportingAppObservation(ReportingInventoryComponent Component, ReportingInventoryStatus Status, string? Version = null);
public sealed class ReportingInventoryCapture
{
    internal Guid SupportId { get; }
    internal Guid Generation { get; }
    internal DateTime StartedAt { get; }
    internal ReportingInventoryCapture(Guid supportId, Guid generation, DateTime startedAt)
    { SupportId = supportId; Generation = generation; StartedAt = startedAt; }
}
public enum ReportingOperation { Install, Update, Repair, Replace, Remove, Cleanup, Restore, RestoreCleanup, Prerequisite, Setup, Adopt, Check, Save, Download, Launch }
public enum ReportingOutcome { Completed, Cancelled, Blocked, Failed, Unknown }
public enum ReportingStage { Start, Check, Prepare, Download, Verify, Apply, Finish, Welcome, Applications, Gng, Profile, Fonts, Preferences }
public enum ReportingErrorCode { Unknown, DownloadTimeout, NetworkUnavailable, PackageInvalid, InstallationFailed, InstallationIdentityMismatch, SettingsSaveFailed, BrowserProcessFailed, OperationStopped, UnexpectedError }
public enum ReportingSource { Cache, Download, Import, Discovery, Browse, Compact, Expanded, Standard, Larger, Largest, Existing, Fresh, Preferences }
public enum ReportingDurationBucket
{
    [JsonStringEnumMemberName("under_1s")] Under1s,
    [JsonStringEnumMemberName("under_10s")] Under10s,
    [JsonStringEnumMemberName("under_1m")] Under1m,
    [JsonStringEnumMemberName("under_5m")] Under5m,
    [JsonStringEnumMemberName("over_5m")] Over5m
}

public sealed record ReportingPreferences(bool Confirmed = false, bool Usage = false, bool Errors = false, bool Inventory = false);
public sealed record ReportingState(Guid SupportId, ReportingPreferences Preferences, bool StorageAvailable, int PendingCount, long SuppressedCount = 0);

// No free-form message, exception, path, account or arbitrary property bag is accepted.
public sealed record ReportingDetails(
    Guid? OperationId = null,
    ReportingApplication? Application = null,
    ReportingOperation? Operation = null,
    ReportingOutcome? Outcome = null,
    ReportingStage? Stage = null,
    ReportingErrorCode? ErrorCode = null,
    ReportingDurationBucket? DurationBucket = null,
    string? PackageVersion = null,
    string? PreviousVersion = null,
    ReportingSource? Source = null,
    bool? RestartRequired = null,
    bool? RecoveryAvailable = null,
    ReportingFeature? Feature = null,
    bool? CompactLayout = null,
    ReportingTextScale? TextScale = null,
    bool? AlwaysOnTop = null,
    ReportingTheme? Theme = null);

public sealed class PreparedReport
{
    public Guid SupportId { get; }
    public Guid ReportId { get; }
    public string Json { get; }
    internal PreparedReport(Guid supportId, Guid reportId, string json)
    { SupportId = supportId; ReportId = reportId; Json = json; }
}

public sealed record ReportSendResult(bool Succeeded, string? Reference, string Message);

internal sealed record ReportingEvent(
    Guid EventId, Guid SupportId, Guid SessionId, DateTime OccurredAt, string LaunchpadVersion,
    ReportingEventName EventName,
    Guid? OperationId = null, ReportingApplication? Application = null, ReportingOperation? Operation = null,
    ReportingOutcome? Outcome = null, ReportingStage? Stage = null, ReportingErrorCode? ErrorCode = null,
    ReportingDurationBucket? DurationBucket = null, string? PackageVersion = null, string? PreviousVersion = null,
    ReportingSource? Source = null, bool? RestartRequired = null, bool? RecoveryAvailable = null,
    ReportingFeature? Feature = null, bool? CompactLayout = null, ReportingTextScale? TextScale = null,
    bool? AlwaysOnTop = null, ReportingTheme? Theme = null, IReadOnlyList<ReportingInventoryEntry>? Items = null);

internal sealed record ReportingBatch(int SchemaVersion, IReadOnlyList<ReportingEvent> Events);
internal sealed record ReportingReport(int SchemaVersion, Guid ReportId, Guid SupportId, string LaunchpadVersion,
    ReportingApplication Application, ReportingCategory Category, IReadOnlyList<ReportingEvent>? Context);
