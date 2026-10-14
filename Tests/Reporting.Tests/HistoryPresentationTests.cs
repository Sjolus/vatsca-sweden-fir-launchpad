using System.Text;
using System.Text.Json;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

internal static class HistoryPresentationTests
{
    internal static int Run()
    {
        var support = Guid.NewGuid();
        var now = new DateTime(2026, 10, 14, 12, 30, 0, DateTimeKind.Utc);
        ReportingEvent Event(ReportingEventName name) => new(Guid.NewGuid(), support, Guid.NewGuid(), now, "2.3.1", name);
        ReportingDelivery Batch(ReportingStream stream, params ReportingEvent[] entries) =>
            new(stream, now, Encoding.UTF8.GetString(ReportingWire.Bytes(new ReportingBatch(1, entries))));

        var usage = Batch(ReportingStream.Usage, Event(ReportingEventName.ApplicationOpened) with { Application = ReportingApplication.Trackaudio },
            Event(ReportingEventName.PreferencesSnapshot) with { CompactLayout = true, AlwaysOnTop = false, TextScale = ReportingTextScale.Larger, Theme = ReportingTheme.Dark });
        var readable = ReportingHistoryPresentation.Describe(usage);
        Require(readable.Contains("Records in this send: 2") && readable.Contains("Application: TrackAudio") &&
            readable.Contains("Recorded: 2026-10-14 12:30:00 UTC") && readable.Contains("Compact layout: Yes") &&
            readable.Contains("Always on top: No") && readable.Contains("Text size: Larger") && readable.Contains("Theme: Dark"), "Usage readability");

        var items = InventoryTests.Items().Select(item => item.Component switch
        {
            ReportingInventoryComponent.Topsky => item with { Status = ReportingInventoryStatus.Unknown, Version = null },
            ReportingInventoryComponent.Rdf => item with { Status = ReportingInventoryStatus.Absent, Version = null },
            ReportingInventoryComponent.VatefsPlugin => item with { Status = ReportingInventoryStatus.Unavailable, Version = null },
            _ => item
        }).ToArray();
        var inventory = Batch(ReportingStream.Inventory, Event(ReportingEventName.InventorySnapshot) with { Items = items });
        readable = ReportingHistoryPresentation.Describe(inventory);
        Require(readable.Contains("TopSky — Detected, but the version could not be read") && readable.Contains("RDF — Not detected") &&
            readable.Contains("VatEFS plugin — Could not be determined") && readable.Contains("EuroScope — Version 1.2.3") &&
            readable.Contains("Source: Registered Windows installer") && readable.Contains("AFV EuroScope Bridge"), "Inventory states and sources");

        var error = Event(ReportingEventName.OperationFailed) with
        {
            Application = ReportingApplication.Gng, Operation = ReportingOperation.Update, Stage = ReportingStage.Download,
            ErrorCode = ReportingErrorCode.DownloadTimeout, Outcome = ReportingOutcome.Unknown,
            PreviousVersion = "2609.1.1", PackageVersion = "2610.1.3", DurationBucket = ReportingDurationBucket.Over5m,
            RecoveryAvailable = true, RestartRequired = false
        };
        readable = ReportingHistoryPresentation.Describe(Batch(ReportingStream.Errors, error));
        Require(readable.Contains("Application: Swedish GNG package") && readable.Contains("Error category: Download Timeout") &&
            readable.Contains("Result: Unknown") && readable.Contains("Time taken: 5 minutes or more") &&
            readable.Contains("Previous version: 2609.1.1") && readable.Contains("Recovery available: Yes"), "Error outcome and technical details");

        var report = new ReportingReport(1, Guid.NewGuid(), support, "2.3.1", ReportingApplication.Vatefs, ReportingCategory.Installation, [error]);
        var manual = new ReportingDelivery(ReportingStream.ManualReport, now, Encoding.UTF8.GetString(ReportingWire.Bytes(report)), "LP_SYNTHETIC_0001");
        readable = ReportingHistoryPresentation.Describe(manual);
        Require(readable.Contains("Problem category: Installation") && readable.Contains("Receipt reference: LP_SYNTHETIC_0001") &&
            readable.Contains("Application: VatEFS") && readable.Contains("Recent technical steps included: 1"), "Manual receipt and context");
        foreach (var copy in new[] { usage, inventory, manual })
        {
            using var original = JsonDocument.Parse(copy.Json);
            using var formatted = JsonDocument.Parse(ReportingHistoryPresentation.FormatJson(copy));
            Require(JsonElement.DeepEquals(original.RootElement, formatted.RootElement), "Pretty JSON preserves every transmitted field and value");
        }
        Require(Enum.GetValues<ReportingStream>().Select(ReportingHistoryPresentation.StreamName).Distinct().Count() == 4, "Distinct type labels");
        Console.WriteLine("PASS readable last-sent summaries preserve states, outcomes, references and exact JSON values");
        return 1;
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new Exception("History presentation: " + message); }
}
