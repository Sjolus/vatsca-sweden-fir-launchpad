using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

// Presentation of an already acknowledged local copy. Reading never queues or sends data.
public static class ReportingHistoryPresentation
{
    public static string StreamName(ReportingStream stream) => stream switch
    {
        ReportingStream.Usage => "Usage statistics",
        ReportingStream.Inventory => "Software and plugin versions",
        ReportingStream.Errors => "Automatic error reports",
        ReportingStream.ManualReport => "Problem reports you submitted",
        _ => "Reporting"
    };

    public static string Describe(ReportingDelivery delivery)
    {
        try
        {
            var text = new StringBuilder();
            if (delivery.Stream == ReportingStream.ManualReport)
            {
                var report = JsonSerializer.Deserialize<ReportingReport>(delivery.Json, ReportingWire.Json)
                    ?? throw new JsonException();
                Field(text, "Support ID", report.SupportId.ToString("D"));
                Field(text, "Launchpad version", report.LaunchpadVersion);
                Field(text, "Application", Label(report.Application));
                Field(text, "Problem category", Label(report.Category));
                Field(text, "Receipt reference", delivery.Reference);
                var context = report.Context ?? [];
                text.AppendLine().AppendLine(context.Count == 0 ? "No recent technical steps were included." : $"Recent technical steps included: {context.Count}");
                AppendEvents(text, context);
            }
            else
            {
                var batch = JsonSerializer.Deserialize<ReportingBatch>(delivery.Json, ReportingWire.Json)
                    ?? throw new JsonException();
                if (batch.Events.Count == 0) throw new JsonException();
                Field(text, "Support ID", batch.Events[0].SupportId.ToString("D"));
                text.AppendLine($"Records in this send: {batch.Events.Count}");
                AppendEvents(text, batch.Events);
            }
            text.AppendLine().Append("The exact JSON also shows technical identifiers used to group steps and prevent duplicate reports.");
            return text.ToString();
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        { return "This saved copy could not be read. Refresh to check local reporting storage again."; }
    }

    public static string FormatJson(ReportingDelivery delivery)
    {
        try
        {
            using var json = JsonDocument.Parse(delivery.Json);
            return JsonSerializer.Serialize(json.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException) { return "This saved copy could not be read."; }
    }

    private static void AppendEvents(StringBuilder text, IReadOnlyList<ReportingEvent> events)
    {
        for (int index = 0; index < events.Count; index++)
        {
            var entry = events[index];
            text.AppendLine().AppendLine($"{index + 1}. {Label(entry.EventName)}");
            Field(text, "Recorded", entry.OccurredAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));
            Field(text, "Launchpad version", entry.LaunchpadVersion);
            Field(text, "Application", entry.Application);
            Field(text, "Action", entry.Operation);
            Field(text, "Result", entry.Outcome);
            Field(text, "Step", entry.Stage);
            Field(text, "Error category", entry.ErrorCode);
            Field(text, "Time taken", entry.DurationBucket);
            Field(text, "Reported version", entry.PackageVersion);
            Field(text, "Previous version", entry.PreviousVersion);
            Field(text, "Source or choice", entry.Source);
            Field(text, "Restart required", entry.RestartRequired);
            Field(text, "Recovery available", entry.RecoveryAvailable);
            Field(text, "Feature", entry.Feature);
            Field(text, "Compact layout", entry.CompactLayout);
            Field(text, "Text size", entry.TextScale);
            Field(text, "Always on top", entry.AlwaysOnTop);
            Field(text, "Theme", entry.Theme);
            if (entry.Items is null) continue;
            foreach (var item in entry.Items)
            {
                string observation = item.Status switch
                {
                    ReportingInventoryStatus.Known => $"Version {item.Version}",
                    ReportingInventoryStatus.Unknown => "Detected, but the version could not be read",
                    ReportingInventoryStatus.Absent => "Not detected",
                    _ => "Could not be determined"
                };
                text.AppendLine($"  {Label(item.Component)} — {observation}");
                text.AppendLine($"    Source: {Label(item.Source)}");
            }
            if (entry.DllInventory is not { } dlls) continue;
            text.AppendLine().AppendLine("Additional DLL details");
            Field(text, "Scan completeness", Label(dlls.Status));
            text.AppendLine($"Unreadable files: {dlls.UnreadableFiles}");
            text.AppendLine("Current and staged describe folder placement; they do not show which DLLs EuroScope loaded.");
            if (dlls.Entries.Count == 0) text.AppendLine(dlls.Status == ReportingDllInventoryStatus.Complete
                ? "No DLLs were found in the scanned folders." : "No DLL details were available in this snapshot.");
            foreach (var dll in dlls.Entries)
            {
                text.AppendLine().AppendLine($"  {Label(dll.Product)} — {Label(dll.Role)}; {dll.Copies} copy/copies");
                Field(text, "    SHA-256 fingerprint", dll.Sha256);
                Field(text, "    Plugin version", dll.Version ?? "Not readable");
                Field(text, "    Windows file version", dll.FileVersion);
                Field(text, "    Version source", Label(dll.VersionSource));
            }
        }
    }

    private static void Field(StringBuilder text, string label, string? value)
    { if (value is not null) text.AppendLine($"{label}: {value}"); }
    private static void Field<T>(StringBuilder text, string label, T? value) where T : struct, Enum
    { if (value is not null) Field(text, label, Label(value.Value)); }
    private static void Field(StringBuilder text, string label, bool? value)
    { if (value is not null) Field(text, label, value.Value ? "Yes" : "No"); }

    private static string Label(Enum value) => value switch
    {
        ReportingApplication.Euroscope or ReportingInventoryComponent.Euroscope => "EuroScope",
        ReportingApplication.Gng or ReportingInventoryComponent.Gng or ReportingCategory.Gng or ReportingStage.Gng => "Swedish GNG package",
        ReportingApplication.Trackaudio or ReportingInventoryComponent.Trackaudio => "TrackAudio",
        ReportingApplication.Vacs or ReportingInventoryComponent.Vacs => "VACS",
        ReportingApplication.Vatis or ReportingInventoryComponent.Vatis => "vATIS",
        ReportingApplication.Vatefs or ReportingInventoryComponent.Vatefs => "VatEFS",
        ReportingApplication.Vatiris => "VATIRIS",
        ReportingInventoryComponent.Topsky or ReportingDllProduct.Topsky => "TopSky",
        ReportingInventoryComponent.Rdf or ReportingDllProduct.Rdf => "RDF",
        ReportingInventoryComponent.VatefsPlugin => "VatEFS plugin",
        ReportingInventoryComponent.AfvEuroscopeBridge or ReportingDllProduct.AfvEuroscopeBridge => "AFV EuroScope Bridge",
        ReportingDllProduct.Unknown => "Unrecognised DLL",
        ReportingDllProduct.Vatefs => "VatEFS plugin",
        ReportingDllProduct.Vatiris => "VatIRIS plugin",
        ReportingDllProduct.ArrivalLoadBalancer => "Arrival Load Balancer",
        ReportingDllProduct.Cdm => "CDM",
        ReportingDllProduct.GroundRadar => "Ground Radar",
        ReportingDllProduct.Maestro => "MAESTRO",
        ReportingDllProduct.Vch => "VCH",
        ReportingDllProduct.VatcanBookings => "VATCAN Bookings",
        ReportingDllRole.Current => "Current folder",
        ReportingDllRole.Staged => "Staged copy",
        ReportingDllVersionSource.Resource => "Windows version metadata",
        ReportingDllVersionSource.Embedded => "Embedded plugin version",
        ReportingDllVersionSource.Registration => "EuroScope plugin registration",
        ReportingDllVersionSource.Unknown => "Not readable",
        ReportingInventorySource.ConfiguredApp => "Configured application",
        ReportingInventorySource.RegisteredMsi => "Registered Windows installer",
        ReportingInventorySource.PackageMetadata => "GNG package metadata",
        ReportingInventorySource.PluginFile => "Version read from DLL",
        ReportingEventName.SessionStarted => "Launchpad session started",
        ReportingEventName.ApplicationOpened => "Application launch",
        ReportingEventName.InventorySnapshot => "Software and plugin observations",
        ReportingEventName.PreferencesSnapshot => "Display preferences",
        ReportingEventName.GngPackageObtained => "GNG package obtained",
        ReportingEventName.UnhandledError => "Unexpected application error",
        ReportingFeature.GngPackageReview => "Review GNG package changes",
        ReportingFeature.GngCleanupReview => "Review GNG cleanup",
        ReportingFeature.GngRestoreReview => "Review GNG recovery",
        ReportingDurationBucket.Under1s => "Under 1 second",
        ReportingDurationBucket.Under10s => "1–10 seconds",
        ReportingDurationBucket.Under1m => "10 seconds–1 minute",
        ReportingDurationBucket.Under5m => "1–5 minutes",
        ReportingDurationBucket.Over5m => "5 minutes or more",
        ReportingSource.Cache => "Previously downloaded package",
        ReportingSource.Import => "Imported package",
        ReportingSource.Existing => "Existing installation",
        ReportingSource.Fresh => "Fresh installation",
        _ => Regex.Replace(value.ToString(), "([a-z])([A-Z])", "$1 $2")
    };
}
