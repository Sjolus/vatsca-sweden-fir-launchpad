using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

internal static class InventoryTests
{
    internal static IReadOnlyList<ReportingInventoryEntry> Items() => Enum.GetValues<ReportingInventoryComponent>()
        .Select(component => new ReportingInventoryEntry(component, ReportingInventoryStatus.Known,
            ReportingWire.InventorySource(component), component == ReportingInventoryComponent.Gng ? "2610.1.3" : "1.2.3")).ToArray();

    internal static async Task<int> Run()
    {
        int passed = 0;
        await Test("all eight permissions independently persist and route usage, inventory and errors", async () =>
        {
            foreach (bool usage in new[] { false, true })
            foreach (bool inventory in new[] { false, true })
            foreach (bool errors in new[] { false, true })
            {
                using var f = new Fixture(); Assert(f.Client.BeginInventoryCapture() is null);
                Assert(f.Client.TrySavePreferences(usage, inventory, errors));
                using var next = f.NewClient();
                Assert(next.State.Preferences == new ReportingPreferences(true, usage, errors, inventory));
                Assert(next.RecordUsage(ReportingEventName.ApplicationOpened, new(Application: ReportingApplication.Trackaudio)) == usage);
                Assert(next.RecordError(ReportingErrorCode.DownloadTimeout) == errors);
                var capture = next.BeginInventoryCapture(); Assert((capture is not null) == inventory);
                if (capture is not null) Assert(next.RecordInventory(capture, Items()));
                for (int i = 0; i < 3; i++) await next.FlushOnceAsync();
                Assert(next.State.PendingCount == 0);
                Assert(f.Transport.Requests.Count == (usage ? 1 : 0) + (inventory ? 1 : 0) + (errors ? 1 : 0));
                Assert(f.Transport.Requests.Count(r => r.Path.EndsWith("inventory/batches")) == (inventory ? 1 : 0));
                if (errors) Assert(f.Transport.Requests.First().Path.EndsWith("errors/batches"));
            }
        });
        await Test("inventory-only normal launch emits no usage sessions and two-argument save preserves inventory", () =>
        {
            using var f = new Fixture(); f.Client.StartNormalSession();
            Assert(f.Client.TrySavePreferences(false, true, false));
            Assert(f.Client.State.PendingCount == 0);
            Assert(f.Client.RecordInventory(f.Client.BeginInventoryCapture()!, Items()));
            Assert(f.Client.TrySavePreferences(false, true));
            Assert(f.Client.State.Preferences.Inventory && f.Client.State.Preferences.Errors && !f.Client.State.Preferences.Usage);
            Assert(Read(f).Pending.All(e => e.EventName == ReportingEventName.InventorySnapshot));
            return Task.CompletedTask;
        });
        await Test("missing legacy inventory preference stays off and version-bearing queued events never replay", async () =>
        {
            using var f = new Fixture(); Assert(f.Client.TrySavePreferences(true, true, true));
            Assert(f.Client.RecordUsage(ReportingEventName.OperationFinished, new(PackageVersion: "1.2.3", PreviousVersion: "1.2.2")));
            Assert(f.Client.RecordUsage(ReportingEventName.SetupStep, new(Stage: ReportingStage.Applications)));
            string legacy = File.ReadAllText(f.StatePath).Replace(",\"inventory\":true", "", StringComparison.Ordinal);
            File.WriteAllText(f.StatePath, legacy);
            using var next = f.NewClient();
            Assert(next.State.StorageAvailable && !next.State.Preferences.Inventory && next.State.PendingCount == 1);
            await next.FlushOnceAsync();
            Assert(f.Transport.Requests.Single().Json.Contains("setup_step") && !f.Transport.Requests.Single().Json.Contains("operation_finished") &&
                !f.Transport.Requests.Single().Json.Contains("package_version") && !f.Transport.Requests.Single().Json.Contains("previous_version"));
        });
        await Test("inventory revocation drops version-bearing queued events and strips only manual memory context", async () =>
        {
            using var f = new Fixture(); Assert(f.Client.TrySavePreferences(true, true, true));
            Assert(f.Client.RecordUsage(ReportingEventName.OperationFinished, new(PackageVersion: "1.2.3", PreviousVersion: "1.2.2")));
            Assert(f.Client.RecordError(ReportingErrorCode.PackageInvalid, new(PackageVersion: "1.2.3")));
            Assert(f.Client.RecordUsage(ReportingEventName.SetupStep, new(Stage: ReportingStage.Applications)));
            Assert(f.Client.RecordError(ReportingErrorCode.NetworkUnavailable));
            Assert(f.Client.RecordInventory(f.Client.BeginInventoryCapture()!, Items()));
            var old = f.Client.PrepareReport(ReportingApplication.Gng, ReportingCategory.Gng, true);
            Assert(old.Json.Contains("package_version") && !old.Json.Contains("inventory_snapshot"));
            Assert(f.Client.TrySavePreferences(true, false, true) && f.Client.State.PendingCount == 2);
            Assert(!(await f.Client.SendReportAsync(old, default)).Succeeded && f.Transport.Requests.IsEmpty);
            var current = f.Client.PrepareReport(ReportingApplication.Gng, ReportingCategory.Gng, true);
            Assert(!current.Json.Contains("package_version") && !current.Json.Contains("previous_version") && !current.Json.Contains("items"));
            await f.Client.FlushOnceAsync(); await f.Client.FlushOnceAsync();
            Assert(f.Transport.Requests.Count == 2 && f.Transport.Requests.All(r => !r.Json.Contains("package_version") && !r.Json.Contains("previous_version")));
        });
        await Test("lost acknowledgement then inventory withdrawal preserves unaffected event IDs without changed-payload retries", async () =>
        {
            using var f = new Fixture(); Assert(f.Client.TrySavePreferences(true, true, false));
            Assert(f.Client.RecordUsage(ReportingEventName.OperationFinished, new(PackageVersion: "1.2.3")));
            Assert(f.Client.RecordUsage(ReportingEventName.SetupStep, new(Stage: ReportingStage.Applications)));
            var original = Read(f).Pending.ToArray();
            var server = new Dictionary<Guid, string>(); bool loseFirstAcknowledgement = true;
            int conflicts = 0, duplicateEvents = 0;
            f.Transport.Responder = (request, _) =>
            {
                using var body = JsonDocument.Parse(request.Json); int accepted = 0, duplicates = 0;
                foreach (var entry in body.RootElement.GetProperty("events").EnumerateArray())
                {
                    var id = entry.GetProperty("event_id").GetGuid(); string exact = entry.GetRawText();
                    if (server.TryGetValue(id, out var existing))
                    {
                        if (existing != exact) { conflicts++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)); }
                        duplicates++; duplicateEvents++;
                    }
                    else { server.Add(id, exact); accepted++; }
                }
                if (loseFirstAcknowledgement) { loseFirstAcknowledgement = false; throw new HttpRequestException("Synthetic acknowledgement loss after durable acceptance"); }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted)
                    { Content = new StringContent($"{{\"accepted\":{accepted},\"duplicates\":{duplicates}}}") });
            };
            await f.Client.FlushOnceAsync(); Assert(server.Count == 2 && f.Client.State.PendingCount == 2);
            Assert(f.Client.TrySavePreferences(true, false, false));
            var remaining = Read(f).Pending.Single();
            Assert(remaining.EventId == original[1].EventId && remaining == original[1]);
            Assert(f.Client.RecordUsage(ReportingEventName.FeatureUsed, new(Feature: ReportingFeature.CheckUpdates)));
            f.Now = f.Now.AddMinutes(2); await f.Client.FlushOnceAsync();
            Assert(conflicts == 0 && duplicateEvents == 1 && server.Count == 3 && f.Client.State.PendingCount == 0);
            Assert(!f.Transport.Requests.Last().Json.Contains(original[0].EventId.ToString()));
        });
        await Test("usage and error versions are never saved without separate inventory permission", () =>
        {
            using var f = new Fixture(); Assert(f.Client.TrySavePreferences(true, false, true));
            Assert(f.Client.RecordUsage(ReportingEventName.GngPackageObtained, new(PackageVersion: "2610.1.3")));
            Assert(f.Client.RecordError(ReportingErrorCode.PackageInvalid, new(PackageVersion: "1.2.3", PreviousVersion: "1.0.0")));
            Assert(Read(f).Pending.All(e => e.PackageVersion is null && e.PreviousVersion is null));
            Assert(!f.Client.PrepareReport(ReportingApplication.Gng, ReportingCategory.Gng, true).Json.Contains("package_version"));
            return Task.CompletedTask;
        });
        await Test("independent category withdrawal preserves the other two queues", () =>
        {
            for (int revoke = 0; revoke < 3; revoke++)
            {
                using var f = new Fixture(); Assert(f.Client.TrySavePreferences(true, true, true));
                Assert(f.Client.RecordUsage(ReportingEventName.SessionStarted));
                Assert(f.Client.RecordError(ReportingErrorCode.PackageInvalid));
                Assert(f.Client.RecordInventory(f.Client.BeginInventoryCapture()!, Items()));
                Assert(f.Client.TrySavePreferences(revoke != 0, revoke != 1, revoke != 2));
                var entries = Read(f).Pending; Assert(entries.Count == 2);
                Assert(entries.All(e => ReportingWire.Stream(e.EventName) != new[] { "usage", "inventory", "errors" }[revoke]));
            }
            return Task.CompletedTask;
        });
        await Test("captured scans cannot cross revoke-reenable, rotation, clearing or age limits", () =>
        {
            using var f = new Fixture(); Assert(f.Client.TrySavePreferences(false, true, false));
            var beforeRevoke = f.Client.BeginInventoryCapture()!;
            Assert(f.Client.TrySavePreferences(false, false, false)); Assert(f.Client.TrySavePreferences(false, true, false));
            Assert(!f.Client.RecordInventory(beforeRevoke, Items()));
            var beforeRotation = f.Client.BeginInventoryCapture()!; Assert(f.Client.TryRegenerateId());
            Assert(!f.Client.RecordInventory(beforeRotation, Items()));
            var beforeClear = f.Client.BeginInventoryCapture()!; Assert(f.Client.ClearPending());
            Assert(!f.Client.RecordInventory(beforeClear, Items()));
            var old = f.Client.BeginInventoryCapture()!; f.Now = f.Now.AddMinutes(6);
            Assert(!f.Client.RecordInventory(old, Items()) && f.Client.State.PendingCount == 0);
            var fresh = f.Client.BeginInventoryCapture()!; Assert(f.Client.RecordInventory(fresh, Items()));
            Assert(Read(f).Pending.Single().OccurredAt == f.Now);
            return Task.CompletedTask;
        });
        await Test("revoking inventory cancels in-flight snapshot and never requeues its old generation", async () =>
        {
            using var f = new Fixture(); Assert(f.Client.TrySavePreferences(false, true, false));
            Assert(f.Client.RecordInventory(f.Client.BeginInventoryCapture()!, Items()));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Transport.Responder = async (_, token) => { entered.SetResult(); try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { cancelled.SetResult(); throw; } return new(HttpStatusCode.OK); };
            var sending = f.Client.FlushOnceAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert(f.Client.TrySavePreferences(false, false, false)); await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)); await sending;
            Assert(f.Client.State.PendingCount == 0); Assert(f.Client.TrySavePreferences(false, true, false));
            await f.Client.FlushOnceAsync(); Assert(f.Transport.Requests.Count == 1);
        });
        await Test("inventory backoff is durable and shared quota pauses all three streams", async () =>
        {
            using var f = new Fixture(); Assert(f.Client.TrySavePreferences(true, true, true));
            Assert(f.Client.RecordInventory(f.Client.BeginInventoryCapture()!, Items()));
            f.Transport.Responder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(10)) }, Content = new StringContent("{}") });
            await f.Client.FlushOnceAsync();
            Assert(f.Client.RecordUsage(ReportingEventName.SessionStarted)); Assert(f.Client.RecordError(ReportingErrorCode.DownloadTimeout));
            using var next = f.NewClient();
            for (int i = 0; i < 3; i++) await next.FlushOnceAsync();
            Assert(f.Transport.Requests.Count == 1 && next.State.PendingCount == 3);
            var doc = Read(f); Assert(doc.InventoryAttempts == 1 && doc.InventoryNotBefore >= f.Now.AddMinutes(10));
            f.Now = f.Now.AddMinutes(11); f.Transport.Responder = null;
            for (int i = 0; i < 3; i++) await next.FlushOnceAsync();
            Assert(next.State.PendingCount == 0 && f.Transport.Requests.Count == 4);
            var inventory = f.Transport.Requests.Where(r => r.Path.EndsWith("inventory/batches")).ToArray();
            Assert(inventory[0].Json == inventory[1].Json);
        });
        await Test("inventory full catalogue, source, status and public-version rules reject malformed observations", () =>
        {
            using var f = new Fixture(); Assert(f.Client.TrySavePreferences(true, true, true)); var token = f.Client.BeginInventoryCapture()!;
            var good = Items();
            Assert(!f.Client.RecordInventory(token, good.Take(9).ToArray()));
            Assert(!f.Client.RecordInventory(token, good.Skip(1).Append(good[1]).ToArray()));
            Assert(!f.Client.RecordInventory(token, good.Select(e => e.Component == ReportingInventoryComponent.Vatefs ? e with { Source = ReportingInventorySource.PluginFile } : e).ToArray()));
            foreach (var item in new[] { good[0] with { Version = "synthetic@example.invalid" }, good[0] with { Status = ReportingInventoryStatus.Unknown },
                good[0] with { Status = ReportingInventoryStatus.Known, Version = null }, good[0] with { Component = (ReportingInventoryComponent)99 } })
                Assert(!f.Client.RecordInventory(token, good.Skip(1).Prepend(item).ToArray()));
            Assert(!f.Client.RecordUsage(ReportingEventName.InventorySnapshot));
            Assert(f.Client.State.PendingCount == 0);
            return Task.CompletedTask;
        });
        await Test("new usage fields are bounded and restricted to their intended event", () =>
        {
            using var f = new Fixture(); Assert(f.Client.TrySavePreferences(true, false, false));
            Assert(f.Client.RecordUsage(ReportingEventName.PreferencesSnapshot, new(CompactLayout: true, TextScale: ReportingTextScale.Largest, AlwaysOnTop: false, Theme: ReportingTheme.Light)));
            foreach (var feature in Enum.GetValues<ReportingFeature>()) Assert(f.Client.RecordUsage(ReportingEventName.FeatureUsed, new(Feature: feature)));
            Assert(!f.Client.RecordUsage(ReportingEventName.FeatureUsed));
            Assert(!f.Client.RecordUsage(ReportingEventName.PreferencesSnapshot, new(TextScale: ReportingTextScale.Standard)));
            Assert(!f.Client.RecordUsage(ReportingEventName.PreferenceChanged, new(CompactLayout: true)));
            Assert(!f.Client.RecordUsage(ReportingEventName.SessionStarted, new(Feature: ReportingFeature.Adopt)));
            Assert(!f.Client.RecordUsage(ReportingEventName.ApplicationOpened, new(Application: ReportingApplication.Gng)));
            Assert(!f.Client.RecordUsage(ReportingEventName.PreferencesSnapshot, new(CompactLayout: true, TextScale: (ReportingTextScale)99, AlwaysOnTop: false)));
            return Task.CompletedTask;
        });
        await Test("safe collector produces fixed IDs and rejects arbitrary private versions without filesystem discovery", () =>
        {
            Assert(ReportingInventoryCollector.PublicVersion("2610/01 rev.3") == "2610.1.3");
            Assert(ReportingInventoryCollector.PublicVersion("2610/01  rev.3") == "2610.1.3");
            Assert(ReportingInventoryCollector.PublicVersion("4.1.0-beta.19") == "4.1.0-beta.19");
            Assert(ReportingInventoryCollector.PublicVersion("2.3.0-rc.4") == "2.3.0-rc.4");
            var observed = new[] {
                new ReportingAppObservation(ReportingInventoryComponent.Euroscope, ReportingInventoryStatus.Known, "v3.2.3.2"),
                new ReportingAppObservation(ReportingInventoryComponent.Gng, ReportingInventoryStatus.Known, "2610/01 rev.3"),
                new ReportingAppObservation(ReportingInventoryComponent.Vatefs, ReportingInventoryStatus.Known, "synthetic secret"),
                new ReportingAppObservation(ReportingInventoryComponent.Vatis, ReportingInventoryStatus.Absent, "discard this"),
                new ReportingAppObservation((ReportingInventoryComponent)99, ReportingInventoryStatus.Known, "private") };
            var items = ReportingInventoryCollector.Collect(observed, null, null);
            Assert(ReportingWire.ValidInventory(items) && items.Count == 10);
            Assert(items.Single(i => i.Component == ReportingInventoryComponent.Euroscope).Version == "3.2.3.2");
            Assert(items.Single(i => i.Component == ReportingInventoryComponent.Gng).Version == "2610.1.3");
            var vatefs = items.Single(i => i.Component == ReportingInventoryComponent.Vatefs);
            Assert(vatefs.Status == ReportingInventoryStatus.Unknown && vatefs.Source == ReportingInventorySource.RegisteredMsi);
            Assert(items.Single(i => i.Component == ReportingInventoryComponent.VatefsPlugin).Status == ReportingInventoryStatus.Unavailable);
            var json = Encoding.UTF8.GetString(ReportingWire.Bytes(items)); Assert(!json.Contains("secret") && !json.Contains("private") && !json.Contains("discard"));
            foreach (string value in new[] { "SYNTHETIC PRIVATE NAME", @"C:\Users\synthetic\profile", "1.2.3+secret", "https://private.invalid/token", "٢٦١٠/٠١ rev.٣", "1.2.3\nsecret" })
                Assert(ReportingInventoryCollector.PublicVersion(value) is null);
            return Task.CompletedTask;
        });
        await Test("collector reads only allowlisted DLL metadata in configured plugin folders", () =>
        {
            using var f = new Fixture(); string data = Path.Combine(f.Root, "synthetic-private-controller");
            string plugins = Path.Combine(data, "ESAA", "Plugins"); Directory.CreateDirectory(Path.Combine(plugins, "TopSky"));
            File.WriteAllBytes(Path.Combine(plugins, "TopSky", "TopSky.dll"), InventoryPeFixture.Create("1.2.3.4", "1.2.3.4"));
            File.WriteAllText(Path.Combine(plugins, "RDF.dll"), "synthetic non-PE plugin");
            Directory.CreateDirectory(Path.Combine(data, "Private"));
            File.WriteAllBytes(Path.Combine(data, "Private", "VatEFS.dll"), InventoryPeFixture.Create("1.2.3.4", "1.2.3.4"));
            File.WriteAllText(Path.Combine(plugins, "unrelated.dll"), "not part of the catalogue");
            var items = ReportingInventoryCollector.Collect([], data, null);
            Assert(items.Single(i => i.Component == ReportingInventoryComponent.Topsky).Status == ReportingInventoryStatus.Known);
            Assert(items.Single(i => i.Component == ReportingInventoryComponent.Rdf).Status == ReportingInventoryStatus.Unknown);
            Assert(items.Single(i => i.Component == ReportingInventoryComponent.VatefsPlugin).Status == ReportingInventoryStatus.Absent);
            Assert(items.Where(i => i.Component <= ReportingInventoryComponent.Vatefs).All(i => i.Status == ReportingInventoryStatus.Unavailable));
            string json = Encoding.UTF8.GetString(ReportingWire.Bytes(items));
            Assert(!json.Contains(data) && !json.Contains("private") && !json.Contains("non-PE"));
            return Task.CompletedTask;
        });
        await Test("native plugin metadata preserves numeric resources and explicit prerelease versions", () =>
        {
            using var f = new Fixture(); string data = Path.Combine(f.Root, "data");
            string plugins = Path.Combine(data, "ESAA", "Plugins"); Directory.CreateDirectory(plugins);
            var cases = new[]
            {
                (Bytes: InventoryPeFixture.Create("  V1.2.3.0 \t", "1, 2, 3, 0"), Version: "1.2.3.0"),
                (Bytes: InventoryPeFixture.Create(file: "0,\t0, 5, 0"), Version: "0.0.5.0"),
                (Bytes: InventoryPeFixture.Create("v1.2.3-beta.4", "1.2.3.0", flags: 2), Version: "1.2.3-beta.4"),
                (Bytes: InventoryPeFixture.Create("2.5 beta 15", "2.5.99.4", flags: 2), Version: "2.5-beta.15"),
                (Bytes: InventoryPeFixture.Create("", "", productHigh: 0x10002, productLow: 0x30004), Version: "1.2.3.4"),
                (Bytes: InventoryPeFixture.Create(fileHigh: 0, fileLow: 0x50000), Version: "0.0.5.0"),
                (Bytes: InventoryPeFixture.Create("0.0.0.0", "1.4.3.0"), Version: "1.4.3.0"),
                (Bytes: InventoryPeFixture.Create("1.4.3.0", "", productHigh: 0x10004, productLow: 0x30000), Version: "1.4.3.0"),
                (Bytes: InventoryPeFixture.Create(file: "1.2.3", flags: 2), Version: "1.2.3"),
                (Bytes: InventoryPeFixture.Create("1.2.3", flags: 8), Version: "1.2.3")
            };
            foreach (var item in cases)
            {
                File.WriteAllBytes(Path.Combine(plugins, "TopSky.dll"), item.Bytes);
                var observed = ReportingInventoryCollector.Collect([], data, null).Single(i => i.Component == ReportingInventoryComponent.Topsky);
                var metadata = System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(plugins, "TopSky.dll"));
                Assert(observed.Status == ReportingInventoryStatus.Known && observed.Version == item.Version,
                    $"Synthetic native version expected {item.Version}, got {observed.Status}/{observed.Version}; product '{metadata.ProductVersion}', file '{metadata.FileVersion}'.");
            }
            // Broader DLL-resource syntax must not loosen the generic app/package wire normalization.
            Assert(ReportingInventoryCollector.PublicVersion("  V1.2.3.0 ") is null);
            Assert(ReportingInventoryCollector.PublicVersion("1, 2, 3, 0") is null);
            return Task.CompletedTask;
        });
        await Test("native plugin metadata does not invent versions from text, zero placeholders or build flags", () =>
        {
            using var f = new Fixture(); string data = Path.Combine(f.Root, "data");
            string plugins = Path.Combine(data, "ESAA", "Plugins"); Directory.CreateDirectory(plugins);
            var images = new[]
            {
                InventoryPeFixture.Create("1.2.3 b 4", "1.2.3.0", productHigh: 0x10002, productLow: 0x30000),
                InventoryPeFixture.Create("Version 1.2.3", "1.2.3.0"),
                InventoryPeFixture.Create("1.2.3+synthetic-private", productHigh: 0x10002, productLow: 0x30000),
                InventoryPeFixture.Create("1.2.3", "1.2.3-rc.4"),
                InventoryPeFixture.Create(productHigh: 0x10002, productLow: 0x30000, flags: 2),
                InventoryPeFixture.Create("0.0.0.0", "0, 0, 0, 0"),
                InventoryPeFixture.Create("0.0.0.0", productHigh: 0x10002, productLow: 0x30000),
                InventoryPeFixture.Create(),
                InventoryPeFixture.Create("1.2.3", "synthetic-private-controller"),
                InventoryPeFixture.Create("1.2.3\nsynthetic-private"),
                InventoryPeFixture.Create("١.٢.٣"),
                InventoryPeFixture.Create(new string(' ', 65) + "1.2.3")
            };
            foreach (var bytes in images)
            {
                File.WriteAllBytes(Path.Combine(plugins, "TopSky.dll"), bytes);
                var items = ReportingInventoryCollector.Collect([], data, null);
                var observed = items.Single(i => i.Component == ReportingInventoryComponent.Topsky);
                Assert(observed.Status == ReportingInventoryStatus.Unknown && observed.Version is null);
                Assert(!Encoding.UTF8.GetString(ReportingWire.Bytes(items)).Contains("synthetic-private"));
            }
            return Task.CompletedTask;
        });
        await Test("configured VatEFS DLL is authoritative and independent of EuroScope scan availability", () =>
        {
            using var f = new Fixture(); string data = Path.Combine(f.Root, "data"), configured = Path.Combine(f.Root, "configured-vatefs");
            string plugins = Path.Combine(data, "ESAA", "Plugins"); Directory.CreateDirectory(plugins); Directory.CreateDirectory(configured);
            File.WriteAllBytes(Path.Combine(plugins, "TopSky.dll"), InventoryPeFixture.Create("1.2.3.4"));
            File.WriteAllBytes(Path.Combine(plugins, "VatEFS.dll"), InventoryPeFixture.Create("9.8.7.6"));
            string dll = Path.Combine(configured, "VatEFS.dll");
            File.WriteAllBytes(dll, InventoryPeFixture.Create("0.0.5.0"));
            File.WriteAllBytes(Path.Combine(configured, "TopSky.dll"), InventoryPeFixture.Create("9.9.9.9"));
            var items = ReportingInventoryCollector.Collect([], data, null, configured);
            Assert(items.Single(i => i.Component == ReportingInventoryComponent.VatefsPlugin).Version == "0.0.5.0");
            Assert(items.Single(i => i.Component == ReportingInventoryComponent.Topsky).Version == "1.2.3.4");
            Assert(ReportingInventoryCollector.Collect([], data, null).Single(i => i.Component == ReportingInventoryComponent.VatefsPlugin).Version == "9.8.7.6");
            var noEuroScope = ReportingInventoryCollector.Collect([], Path.Combine(f.Root, "missing-data"), null, configured);
            Assert(noEuroScope.Single(i => i.Component == ReportingInventoryComponent.VatefsPlugin).Version == "0.0.5.0");
            Assert(noEuroScope.Single(i => i.Component == ReportingInventoryComponent.Topsky).Status == ReportingInventoryStatus.Unavailable);
            File.Delete(dll);
            Assert(ReportingInventoryCollector.Collect([], data, null, configured).Single(i => i.Component == ReportingInventoryComponent.VatefsPlugin).Status == ReportingInventoryStatus.Absent);
            var missingRoot = ReportingInventoryCollector.Collect([], data, null, Path.Combine(f.Root, "missing-vatefs"));
            Assert(missingRoot.Single(i => i.Component == ReportingInventoryComponent.VatefsPlugin).Status == ReportingInventoryStatus.Unavailable);
            Assert(missingRoot.Single(i => i.Component == ReportingInventoryComponent.Topsky).Version == "1.2.3.4");
            string json = Encoding.UTF8.GetString(ReportingWire.Bytes(items)); Assert(!json.Contains(configured) && !json.Contains("configured-vatefs"));
            return Task.CompletedTask;
        });
        await Test("configured VatEFS failures stay isolated and linked folders are never followed", () =>
        {
            using var f = new Fixture(); string data = Path.Combine(f.Root, "data");
            string plugins = Path.Combine(data, "ESAA", "Plugins"); Directory.CreateDirectory(plugins);
            File.WriteAllBytes(Path.Combine(plugins, "TopSky.dll"), InventoryPeFixture.Create("1.2.3.4"));
            string dll = Path.Combine(plugins, "VatEFS.dll"); File.WriteAllBytes(dll, InventoryPeFixture.Create("0.0.5.0"));
            using (var locked = new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var items = ReportingInventoryCollector.Collect([], data, null, plugins);
                Assert(items.Single(i => i.Component == ReportingInventoryComponent.VatefsPlugin).Status == ReportingInventoryStatus.Unavailable);
                Assert(items.Single(i => i.Component == ReportingInventoryComponent.Topsky).Version == "1.2.3.4");
            }
            string link = Path.Combine(f.Root, "linked-vatefs"); CreateJunction(link, plugins);
            try
            {
                var items = ReportingInventoryCollector.Collect([], data, null, link);
                Assert(items.Single(i => i.Component == ReportingInventoryComponent.VatefsPlugin).Status == ReportingInventoryStatus.Unavailable);
                Assert(items.Single(i => i.Component == ReportingInventoryComponent.Topsky).Version == "1.2.3.4");
            }
            finally { Directory.Delete(link); }
            File.WriteAllText(dll, "synthetic plugin without resource metadata");
            Assert(ReportingInventoryCollector.Collect([], data, null, plugins).Single(i => i.Component == ReportingInventoryComponent.VatefsPlugin).Status == ReportingInventoryStatus.Unknown);
            return Task.CompletedTask;
        });
        await Test("GNG staging copies do not obscure installed versions but other active conflicts remain unavailable", () =>
        {
            using var f = new Fixture(); string data = Path.Combine(f.Root, "data");
            string plugins = Path.Combine(data, "ESAA", "Plugins"); Directory.CreateDirectory(plugins);
            File.WriteAllBytes(Path.Combine(plugins, "TopSky.dll"), InventoryPeFixture.Create("2.5.99.4"));
            foreach (var name in new[] { "Updated Plugin", "uPdAtEd PlUgInS" })
            {
                string staging = Path.Combine(plugins, name); Directory.CreateDirectory(staging);
                File.WriteAllBytes(Path.Combine(staging, "TopSky.dll"), InventoryPeFixture.Create("2.5.99.1"));
                File.WriteAllBytes(Path.Combine(staging, "RDFPlugin.dll"), InventoryPeFixture.Create("1.4.3.0"));
                for (int i = 0; i < 257; i++) File.WriteAllText(Path.Combine(staging, $"staged-{i}.txt"), "");
            }
            var items = ReportingInventoryCollector.Collect([], data, null);
            Assert(items.Single(i => i.Component == ReportingInventoryComponent.Topsky).Version == "2.5.99.4");
            Assert(items.Single(i => i.Component == ReportingInventoryComponent.Rdf).Status == ReportingInventoryStatus.Absent);
            string other = Path.Combine(plugins, "Updated Plugin Backup"); Directory.CreateDirectory(other);
            File.WriteAllBytes(Path.Combine(other, "TopSky.dll"), InventoryPeFixture.Create("2.5.99.1"));
            Assert(ReportingInventoryCollector.Collect([], data, null).Single(i => i.Component == ReportingInventoryComponent.Topsky).Status == ReportingInventoryStatus.Unavailable);
            return Task.CompletedTask;
        });
        await Test("collector rejects arbitrary roots and reports uncertain bounded scans as unavailable", () =>
        {
            using var f = new Fixture(); string arbitrary = Path.Combine(f.Root, "arbitrary"); Directory.CreateDirectory(arbitrary);
            File.WriteAllBytes(Path.Combine(arbitrary, "TopSky.dll"), InventoryPeFixture.Create("1.2.3.4", "1.2.3.4"));
            Assert(ReportingInventoryCollector.Collect([], arbitrary, null).Where(i => i.Component >= ReportingInventoryComponent.Topsky)
                .All(i => i.Status == ReportingInventoryStatus.Unavailable));
            string plugins = Path.Combine(f.Root, "data", "ESAA", "Plugins"); Directory.CreateDirectory(plugins);
            for (int i = 0; i < 257; i++) File.WriteAllText(Path.Combine(plugins, $"file-{i}.txt"), "");
            Assert(ReportingInventoryCollector.Collect([], Path.Combine(f.Root, "data"), null).Where(i => i.Component >= ReportingInventoryComponent.Topsky)
                .All(i => i.Status == ReportingInventoryStatus.Unavailable));
            return Task.CompletedTask;
        });
        await Test("collector rejects configured ancestor and nested plugin reparse traversal", () =>
        {
            using var f = new Fixture(); string target = Path.Combine(f.Root, "target");
            string plugins = Path.Combine(target, "ESAA", "Plugins"); Directory.CreateDirectory(plugins);
            File.WriteAllBytes(Path.Combine(plugins, "TopSky.dll"), InventoryPeFixture.Create("1.2.3.4", "1.2.3.4"));
            string link = Path.Combine(f.Root, "linked");
            CreateJunction(link, target);
            try
            {
                Assert(ReportingInventoryCollector.Collect([], link, null).Where(i => i.Component >= ReportingInventoryComponent.Topsky)
                    .All(i => i.Status == ReportingInventoryStatus.Unavailable));
            }
            finally { Directory.Delete(link); }
            string nested = Path.Combine(plugins, "linked"); CreateJunction(nested, target);
            try
            {
                Assert(ReportingInventoryCollector.Collect([], target, null).Where(i => i.Component >= ReportingInventoryComponent.Topsky)
                    .All(i => i.Status == ReportingInventoryStatus.Unavailable));
            }
            finally { Directory.Delete(nested); }
            return Task.CompletedTask;
        });
        passed += DllScannerTests.Run();
        return passed;

        async Task Test(string name, Func<Task> body) { await body(); passed++; Console.WriteLine("PASS " + name); }
    }
    private static ReportingDocument Read(Fixture f)
    { var store = new ReportingStore(f.Root); Assert(store.Read(out var document)); return document!; }
    private static void Assert(bool condition, string? message = null) { if (!condition) throw new Exception(message ?? "Inventory reporting assertion failed."); }

    // Directory junctions need no administrator/symlink privilege. Both paths are
    // synthetic fixture descendants and the junction itself is removed explicitly.
    internal static void CreateJunction(string link, string target)
    {
        byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        byte[] display = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        byte[] buffer = new byte[16 + substitute.Length + 2 + display.Length + 2];
        BitConverter.GetBytes(0xA0000003u).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)(buffer.Length - 8)).CopyTo(buffer, 4);
        BitConverter.GetBytes((ushort)substitute.Length).CopyTo(buffer, 10);
        BitConverter.GetBytes((ushort)(substitute.Length + 2)).CopyTo(buffer, 12);
        BitConverter.GetBytes((ushort)display.Length).CopyTo(buffer, 14);
        substitute.CopyTo(buffer, 16); display.CopyTo(buffer, 18 + substitute.Length);
        Directory.CreateDirectory(link);
        using var handle = CreateFile(link, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize, IntPtr output, int outputSize, out int bytes, IntPtr overlap);
}
