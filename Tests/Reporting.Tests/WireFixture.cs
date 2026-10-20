using System.Text;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

internal static class WireFixture
{
    internal static void Write()
    {
        var support = Guid.NewGuid(); var session = Guid.NewGuid();
        var now = DateTime.UtcNow.AddSeconds(-2);
        now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond + 1234567, DateTimeKind.Utc);
        ReportingEvent Event() => new(Guid.NewGuid(), support, session, now, "2.2.0", ReportingEventName.OperationFinished,
            OperationId: Guid.NewGuid(), PackageVersion: "3.2.3.2", PreviousVersion: "4.1.0-beta.19", RestartRequired: false, RecoveryAvailable: true);
        var usage = new List<ReportingEvent>(); var errors = new List<ReportingEvent>();
        foreach (var value in Enum.GetValues<ReportingEventName>().Where(value => value < ReportingEventName.ApplicationOpened))
            (ReportingWire.IsError(value) ? errors : usage).Add(Event() with { EventName = value });
        foreach (var application in new[] { ReportingApplication.Euroscope, ReportingApplication.Trackaudio, ReportingApplication.Vacs,
            ReportingApplication.Vatis, ReportingApplication.Vatefs, ReportingApplication.Vatiris })
            usage.Add(Event() with { EventName = ReportingEventName.ApplicationOpened, Application = application });
        foreach (var feature in Enum.GetValues<ReportingFeature>()) usage.Add(Event() with { EventName = ReportingEventName.FeatureUsed, Feature = feature });
        foreach (var scale in Enum.GetValues<ReportingTextScale>())
        foreach (var theme in Enum.GetValues<ReportingTheme>())
        foreach (bool compact in new[] { false, true })
            usage.Add(Event() with { EventName = ReportingEventName.PreferencesSnapshot, CompactLayout = compact, TextScale = scale, AlwaysOnTop = !compact, Theme = theme });
        foreach (var value in Enum.GetValues<ReportingApplication>()) usage.Add(Event() with { Application = value });
        foreach (var value in Enum.GetValues<ReportingOperation>()) usage.Add(Event() with { Operation = value });
        foreach (var value in Enum.GetValues<ReportingOutcome>()) usage.Add(Event() with { Outcome = value });
        foreach (var value in Enum.GetValues<ReportingStage>()) usage.Add(Event() with { Stage = value });
        foreach (var value in Enum.GetValues<ReportingDurationBucket>()) usage.Add(Event() with { DurationBucket = value });
        foreach (var value in Enum.GetValues<ReportingSource>()) usage.Add(Event() with { Source = value });
        foreach (var value in Enum.GetValues<ReportingErrorCode>()) errors.Add(Event() with { EventName = ReportingEventName.OperationFailed, ErrorCode = value });
        var inventory = Enum.GetValues<ReportingInventoryStatus>().Select(status => new ReportingEvent(Guid.NewGuid(), support, session,
            now, "2.2.0", ReportingEventName.InventorySnapshot, Items: InventoryTests.Items().Select(item => item with
            { Status = status, Version = status == ReportingInventoryStatus.Known ? item.Version : null }).ToArray())).ToList();
        var dlls = Enum.GetValues<ReportingDllProduct>().Select((product, index) => new ReportingDllEntry((index + 1).ToString("x64"),
            product, ReportingDllRole.Current, 1, product == ReportingDllProduct.Unknown ? null : "1.2.3", null,
            product == ReportingDllProduct.Unknown ? ReportingDllVersionSource.Unknown : ReportingDllVersionSource.Resource)).ToArray();
        foreach (var status in Enum.GetValues<ReportingDllInventoryStatus>())
            inventory.Add(new(Guid.NewGuid(), support, session, now, "2.2.0", ReportingEventName.InventorySnapshot,
                Items: InventoryTests.Items(), DllInventory: new(status, status == ReportingDllInventoryStatus.Unavailable ? [] : dlls,
                    status == ReportingDllInventoryStatus.Partial ? 1 : 0)));
        inventory.Add(new(Guid.NewGuid(), support, session, now, "2.2.0", ReportingEventName.InventorySnapshot, Items: InventoryTests.Items(),
            DllInventory: new(ReportingDllInventoryStatus.Complete, new[] { dlls[1] with
            { Role = ReportingDllRole.Staged, Copies = 2, Version = "2.6-beta.4", FileVersion = "2.5.99.4", VersionSource = ReportingDllVersionSource.Embedded } })));
        inventory.Add(new(Guid.NewGuid(), support, session, now, "2.2.0", ReportingEventName.InventorySnapshot, Items: InventoryTests.Items(),
            DllInventory: new(ReportingDllInventoryStatus.Complete, new[] { dlls[1] with
            { Product = ReportingDllProduct.VatcanBookings, Version = "1.1.6-2022wb", FileVersion = "1.0.0.1", VersionSource = ReportingDllVersionSource.Registration } })));
        inventory.Add(new(Guid.NewGuid(), support, session, now, "2.2.0", ReportingEventName.InventorySnapshot, Items: InventoryTests.Items(),
            DllInventory: new(ReportingDllInventoryStatus.Complete, new[] { dlls[1] with
            { Product = ReportingDllProduct.ArrivalLoadBalancer, Version = "0.2.11T", FileVersion = "1.0.0.2", VersionSource = ReportingDllVersionSource.Registration } })));
        inventory.Add(new(Guid.NewGuid(), support, session, now, "2.5.0", ReportingEventName.InventorySnapshot, Items: InventoryTests.Items(),
            DllInventory: new(ReportingDllInventoryStatus.Complete, new[] { dlls[1] with
            { Product = ReportingDllProduct.Saaman, Version = "1.3.37", FileVersion = null, VersionSource = ReportingDllVersionSource.Registration } })));
        foreach (var entry in usage.Concat(errors).Concat(inventory))
            if (!ReportingWire.ValidEvent(entry, DateTime.UtcNow)) throw new InvalidDataException("Invalid synthetic wire fixture.");
        var reports = Enum.GetValues<ReportingCategory>().Select(category => new ReportingReport(1, Guid.NewGuid(), support,
            "2.2.0", category == ReportingCategory.Installation ? ReportingApplication.Saaman : ReportingApplication.Gng,
            category, usage.Take(20).ToArray())).ToArray();
        var fixture = new
        {
            usage_batches = usage.Chunk(25).Select(events => new ReportingBatch(1, events)).ToArray(),
            error_batches = errors.Chunk(25).Select(events => new ReportingBatch(1, events)).ToArray(),
            inventory_batches = inventory.Chunk(25).Select(events => new ReportingBatch(1, events)).ToArray(),
            reports
        };
        Console.WriteLine(Encoding.UTF8.GetString(ReportingWire.Bytes(fixture)));
    }
}
