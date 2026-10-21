using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

internal static class ReportingWire
{
    internal const int MaximumRequestBytes = 64 * 1024;
    internal const int MaximumReportBytes = 16 * 1024;
    internal const int MaximumDllInventoryBytes = 6 * 1024;
    internal const int MaximumDllEntries = 64;
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) }
    };

    internal static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
    internal static bool IsUuid(Guid value)
    {
        var text = value.ToString("D");
        return text[14] == '4' && "89ab".Contains(text[19]);
    }
    internal static bool Version(string? value) => value is not null && value.Length <= 40 &&
        Regex.IsMatch(value, @"\A[0-9]{1,6}(?:\.[0-9]{1,6}){1,3}(?:-(?:alpha|beta|rc|preview|dev)(?:\.[0-9]{1,6}){0,2})?\z", RegexOptions.CultureInvariant);

    // Public registration formats: VATCAN's 1.1.6-2022wb and ALB's 0.2.11T.
    // Keep this DLL-only: application versions and arbitrary build labels remain restricted.
    internal static bool DllVersion(string? value) => Version(value) || value is not null && value.Length <= 40 &&
        Regex.IsMatch(value, @"\A[0-9]{1,6}(?:\.[0-9]{1,6}){1,3}(?:-(?:19|20)[0-9]{2}wb|T)\z", RegexOptions.CultureInvariant);

    internal static string NormalizeAppVersion(string value)
    {
        value = value.Split('+')[0];
        if (System.Version.TryParse(value, out var version))
            value = $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        if (!Version(value)) throw new ArgumentException("A numeric Launchpad version is required.", nameof(value));
        return value;
    }

    internal static bool ValidDetails(ReportingDetails details) =>
        (details.OperationId is null || IsUuid(details.OperationId.Value)) &&
        ValidEnum(details.Application) && ValidEnum(details.Operation) && ValidEnum(details.Outcome) &&
        ValidEnum(details.Stage) && ValidEnum(details.ErrorCode) && ValidEnum(details.DurationBucket) && ValidEnum(details.Source) &&
        ValidEnum(details.Feature) && ValidEnum(details.TextScale) && ValidEnum(details.Theme) &&
        ValidEnum(details.Reason) && ValidEnum(details.View) && ValidEnum(details.Service) &&
        (details.HttpStatus is null || details.HttpStatus is >= 100 and <= 599) &&
        (details.PackageVersion is null || Version(details.PackageVersion)) &&
        (details.PreviousVersion is null || Version(details.PreviousVersion));
    private static bool ValidEnum<T>(T? value) where T : struct, Enum => value is null || Enum.IsDefined(value.Value);
    internal static bool IsError(ReportingEventName name) => name is ReportingEventName.OperationFailed or ReportingEventName.UnhandledError;
    internal static bool IsInventory(ReportingEventName name) => name == ReportingEventName.InventorySnapshot;
    internal static string Stream(ReportingEventName name) => IsInventory(name) ? "inventory" : IsError(name) ? "errors" : "usage";
    internal static bool Allowed(ReportingPreferences preferences, ReportingEventName name) => preferences.Confirmed &&
        (IsInventory(name) ? preferences.Inventory : IsError(name) ? preferences.Errors : preferences.Usage);
    internal static bool HasVersions(ReportingEvent entry) => entry.PackageVersion is not null || entry.PreviousVersion is not null;
    internal static ReportingEvent WithoutVersions(ReportingEvent entry) => entry with { PackageVersion = null, PreviousVersion = null };
    internal static ReportingInventorySource InventorySource(ReportingInventoryComponent component) => component switch
    {
        ReportingInventoryComponent.Gng => ReportingInventorySource.PackageMetadata,
        ReportingInventoryComponent.Vatefs => ReportingInventorySource.RegisteredMsi,
        >= ReportingInventoryComponent.Topsky => ReportingInventorySource.PluginFile,
        _ => ReportingInventorySource.ConfiguredApp
    };
    internal static bool ValidInventory(IReadOnlyList<ReportingInventoryEntry>? items) => items is { Count: 10 } &&
        items.Select(i => i?.Component).Distinct().Count() == 10 && items.All(i => i is not null && Enum.IsDefined(i.Component) &&
            Enum.IsDefined(i.Status) && i.Source == InventorySource(i.Component) &&
            (i.Status == ReportingInventoryStatus.Known ? Version(i.Version) : i.Version is null));
    internal static bool ValidDllInventory(ReportingDllInventory? inventory) => inventory is not null &&
        Enum.IsDefined(inventory.Status) && inventory.UnreadableFiles is >= 0 and <= 4096 &&
        inventory.Entries is { Count: <= MaximumDllEntries } &&
        (inventory.Status != ReportingDllInventoryStatus.Complete || inventory.UnreadableFiles == 0) &&
        (inventory.Status != ReportingDllInventoryStatus.Unavailable || inventory.Entries.Count == 0) &&
        inventory.Entries.All(entry => entry is not null && entry.Sha256 is { Length: 64 } &&
            Regex.IsMatch(entry.Sha256, @"\A[a-f0-9]{64}\z", RegexOptions.CultureInvariant) &&
            Enum.IsDefined(entry.Product) && Enum.IsDefined(entry.Role) && Enum.IsDefined(entry.VersionSource) &&
            entry.Copies is >= 1 and <= 4096 &&
            (entry.VersionSource == ReportingDllVersionSource.Unknown ? entry.Version is null : DllVersion(entry.Version)) &&
            (entry.FileVersion is null || Version(entry.FileVersion))) &&
        inventory.Entries.Select(entry => (entry.Sha256, entry.Role)).Distinct().Count() == inventory.Entries.Count &&
        Bytes(inventory).Length <= MaximumDllInventoryBytes;
    internal static bool ValidEvent(ReportingEvent entry, DateTime utcNow, bool checkAge = true) =>
        IsUuid(entry.EventId) && IsUuid(entry.SupportId) && IsUuid(entry.SessionId) && Version(entry.LaunchpadVersion) &&
        Enum.IsDefined(entry.EventName) && entry.OccurredAt.Kind == DateTimeKind.Utc &&
        (!checkAge || (entry.OccurredAt >= utcNow.AddDays(-7) && entry.OccurredAt <= utcNow.AddMinutes(5))) &&
        ValidDetails(new(entry.OperationId, entry.Application, entry.Operation, entry.Outcome, entry.Stage,
            entry.ErrorCode, entry.DurationBucket, entry.PackageVersion, entry.PreviousVersion, entry.Source, entry.RestartRequired, entry.RecoveryAvailable,
            entry.Feature, entry.CompactLayout, entry.TextScale, entry.AlwaysOnTop, entry.Theme,
            entry.Reason, entry.View, entry.Service, entry.HttpStatus)) &&
        (IsError(entry.EventName) || entry.ErrorCode is null) &&
        (IsError(entry.EventName) || entry.Service is null && entry.HttpStatus is null) &&
        (entry.Reason is null || IsError(entry.EventName) || entry.EventName == ReportingEventName.OperationFinished) &&
        (entry.EventName == ReportingEventName.ViewOpened ? entry.View is not null : entry.View is null) &&
        (entry.EventName != ReportingEventName.ApplicationOpened || entry.Application is ReportingApplication.Euroscope or ReportingApplication.Trackaudio or
            ReportingApplication.Vacs or ReportingApplication.Vatis or ReportingApplication.Vatefs or ReportingApplication.Vatiris) &&
        (entry.EventName == ReportingEventName.FeatureUsed ? entry.Feature is not null : entry.Feature is null) &&
        (entry.EventName == ReportingEventName.PreferencesSnapshot ? (entry.CompactLayout is not null && entry.TextScale is not null && entry.AlwaysOnTop is not null)
            : entry.CompactLayout is null && entry.TextScale is null && entry.AlwaysOnTop is null && entry.Theme is null) &&
        (IsInventory(entry.EventName) ? ValidInventory(entry.Items) && (entry.DllInventory is null || ValidDllInventory(entry.DllInventory)) &&
            entry.OperationId is null && entry.Application is null && entry.Operation is null &&
            entry.Outcome is null && entry.Stage is null && entry.ErrorCode is null && entry.DurationBucket is null && entry.PackageVersion is null &&
            entry.PreviousVersion is null && entry.Source is null && entry.RestartRequired is null && entry.RecoveryAvailable is null && entry.Feature is null &&
            entry.CompactLayout is null && entry.TextScale is null && entry.AlwaysOnTop is null && entry.Theme is null &&
            entry.Reason is null && entry.View is null && entry.Service is null && entry.HttpStatus is null : entry.Items is null && entry.DllInventory is null) &&
        Bytes(entry).Length <= (IsError(entry.EventName) || IsInventory(entry.EventName) ? 8 * 1024 : 2 * 1024);
}
