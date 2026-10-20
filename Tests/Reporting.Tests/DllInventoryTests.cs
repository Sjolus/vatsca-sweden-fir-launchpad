using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

internal static class DllInventoryTests
{
    internal static async Task<int> Run()
    {
        int passed = 0;
        async Task Test(string name, Func<Task> body) { await body(); passed++; Console.WriteLine("PASS " + name); }
        await Test("legacy version consent does not enable DLL fingerprints, and explicit detail choice persists", () =>
        {
            using var f = new Fixture();
            Assert(f.Client.TrySavePreferences(false, true, false));
            var legacy = JsonNode.Parse(File.ReadAllText(f.StatePath))!.AsObject();
            legacy["preferences"]!.AsObject().Remove("detailed_plugins");
            File.WriteAllText(f.StatePath, legacy.ToJsonString());
            using var other = f.NewClient();
            Assert(other.State.StorageAvailable && other.State.Preferences.Inventory && !other.State.Preferences.DetailedPlugins);
            var capture = other.BeginInventoryCapture()!;
            Assert(!capture.IncludeDllInventory && !other.RecordInventory(capture, InventoryTests.Items(), Details()));
            Assert(other.RecordInventory(capture, InventoryTests.Items()));
            Assert(other.TrySavePreferences(false, true, false, true));
            Assert(other.BeginInventoryCapture()!.IncludeDllInventory);
            Assert(other.TrySavePreferences(true, true, false) && other.State.Preferences.DetailedPlugins);
            Assert(other.TrySavePreferences(true, false, false) && !other.State.Preferences.DetailedPlugins);
            Assert(other.TrySavePreferences(true, true, false) && !other.State.Preferences.DetailedPlugins);
            Assert(other.TrySavePreferences(true, false, false, true) && !other.State.Preferences.DetailedPlugins);
            return Task.CompletedTask;
        });
        await Test("detailed DLL wire includes unknown binaries without filenames and preserves exact acknowledged history", async () =>
        {
            using var f = new Fixture(); Assert(f.Client.TrySavePreferences(false, true, false, true));
            var entries = new List<ReportingDllEntry>
            {
                Entry(1), Entry(2) with { Product = ReportingDllProduct.Topsky, Version = "2.6-beta.4", FileVersion = "2.5.99.4", VersionSource = ReportingDllVersionSource.Embedded },
                Entry(2) with { Role = ReportingDllRole.Staged, Copies = 3 }
            };
            Assert(f.Client.RecordInventory(f.Client.BeginInventoryCapture()!, InventoryTests.Items(), new(ReportingDllInventoryStatus.Partial, entries, 1)));
            entries.Clear();
            await f.Client.FlushOnceAsync();
            var request = f.Transport.Requests.Single();
            using var json = JsonDocument.Parse(request.Json);
            var value = json.RootElement.GetProperty("events")[0].GetProperty("dll_inventory");
            Assert(value.GetProperty("entries").GetArrayLength() == 3 && value.GetProperty("unreadable_files").GetInt32() == 1);
            var unknown = value.GetProperty("entries")[0];
            Assert(unknown.GetProperty("product").GetString() == "unknown" && unknown.GetProperty("sha256").GetString() == Entry(1).Sha256);
            Assert(!request.Json.Contains("filename") && !request.Json.Contains("file_name") && !request.Json.Contains("path"));
            Assert(unknown.EnumerateObject().Select(property => property.Name).SequenceEqual(new[] { "sha256", "product", "role", "copies", "version_source" }));
            var receipt = f.Client.History.LastSent.Single();
            Assert(receipt.Json == request.Json && ReportingHistoryValidation.Valid(receipt, f.Client.State.SupportId));
            string readable = ReportingHistoryPresentation.Describe(receipt);
            Assert(readable.Contains("Unrecognised DLL") && readable.Contains("2.6-beta.4") && readable.Contains("2.5.99.4") && readable.Contains("Staged copy"));
            using var reloaded = f.NewClient();
            Assert(reloaded.State.Preferences.DetailedPlugins && reloaded.History.LastSent.Single().Json == request.Json);
        });
        await Test("registration versions retain their public suffix and source through delivery and history", async () =>
        {
            using var f = new Fixture(); Assert(f.Client.TrySavePreferences(false, true, false, true));
            var registration = Entry(3) with { Product = ReportingDllProduct.VatcanBookings, Version = "1.1.6-2022wb",
                FileVersion = "1.0.0.1", VersionSource = ReportingDllVersionSource.Registration };
            var details = Details() with { Entries = new[] { registration } };
            Assert(ReportingWire.ValidDllInventory(details) && !ReportingWire.Version(registration.Version));
            Assert(ReportingWire.DllVersion("0.2.11T") && !ReportingWire.Version("0.2.11T"));
            foreach (string invalid in new[] { "1.1.6-person", "1.1.6-2022private", "1.1.6-2022wb+private", "1.1.6-2022wb\n", "1.1.6-2022wb ", "1.1.6-2022WB", "0.2.11T-private", "0.2.11t", "0.2.11T\n" })
                Assert(!ReportingWire.DllVersion(invalid));
            Assert(!ReportingWire.ValidDllInventory(details with { Entries = new[] { registration with { FileVersion = registration.Version } } }));
            Assert(f.Client.RecordInventory(f.Client.BeginInventoryCapture()!, InventoryTests.Items(), details));
            await f.Client.FlushOnceAsync();
            var sent = f.Client.History.LastSent.Single();
            Assert(sent.Json.Contains("1.1.6-2022wb") && sent.Json.Contains("\"version_source\":\"registration\""));
            Assert(ReportingHistoryValidation.Valid(sent, f.Client.State.SupportId));
            Assert(ReportingHistoryPresentation.Describe(sent).Contains("EuroScope plugin registration"));
            using var reloaded = f.NewClient();
            Assert(reloaded.History.LastSent.Single().Json == sent.Json);
        });
        await Test("SAAMAN keeps the existing consent and legacy schema through delivery and history", async () =>
        {
            using var f = new Fixture();
            var details = Details() with { Entries = new[] { Entry(4) with { Product = ReportingDllProduct.Saaman,
                Version = "1.3.37", VersionSource = ReportingDllVersionSource.Registration } } };
            Assert(f.Client.TrySavePreferences(false, true, false));
            Assert(!f.Client.RecordInventory(f.Client.BeginInventoryCapture()!, InventoryTests.Items(), details));
            Assert(f.Client.TrySavePreferences(false, true, false, true));
            Assert(f.Client.RecordInventory(f.Client.BeginInventoryCapture()!, InventoryTests.Items(), details));
            await f.Client.FlushOnceAsync();
            var sent = f.Client.History.LastSent.Single();
            Assert(ReportingHistoryValidation.Valid(sent, f.Client.State.SupportId));
            Assert(sent.Json.Contains("\"product\":\"saaman\"") && sent.Json.Contains("\"version\":\"1.3.37\""));
            Assert(ReportingHistoryPresentation.Describe(sent).Contains("SAAMAN"));
            using var reloaded = f.NewClient();
            Assert(reloaded.History.LastSent.Single().Json == sent.Json);
            Assert(InventoryTests.Items().Count == 10 && (int)ReportingApplication.Maintenance == 8 &&
                (int)ReportingDllProduct.VatcanBookings == 11);
            Assert(f.Client.TrySavePreferences(true, true, false, true));
            Assert(!f.Client.RecordUsage(ReportingEventName.ApplicationOpened, new(Application: ReportingApplication.Saaman)));
            var report = f.Client.PrepareReport(ReportingApplication.Saaman, ReportingCategory.Installation, false);
            Assert(report.Json.Contains("\"application\":\"saaman\""));
            Assert((await f.Client.SendReportAsync(report, default)).Succeeded);
            var manual = f.Client.History.LastSent.Single(item => item.Stream == ReportingStream.ManualReport);
            Assert(ReportingHistoryValidation.Valid(manual, f.Client.State.SupportId) &&
                ReportingHistoryPresentation.Describe(manual).Contains("Application: SAAMAN"));
        });
        await Test("DLL schema rejects malformed identities, arbitrary text, inconsistent status and oversized payloads", () =>
        {
            var valid = Details(); Assert(ReportingWire.ValidDllInventory(valid));
            var invalidEntries = new[]
            {
                Entry(1) with { Sha256 = new string('A', 64) }, Entry(1) with { Sha256 = "private-path" },
                Entry(1) with { Copies = 0 }, Entry(1) with { Copies = 4097 }, Entry(1) with { Product = (ReportingDllProduct)999 },
                Entry(1) with { Role = (ReportingDllRole)999 }, Entry(1) with { VersionSource = (ReportingDllVersionSource)999 },
                Entry(1) with { Version = "2.1" }, Entry(1) with { VersionSource = ReportingDllVersionSource.Resource },
                Entry(1) with { Version = "C:\\private\\user", VersionSource = ReportingDllVersionSource.Embedded },
                Entry(1) with { FileVersion = "somebody@example.invalid" }
            };
            foreach (var entry in invalidEntries) Assert(!ReportingWire.ValidDllInventory(valid with { Entries = new[] { entry } }));
            Assert(!ReportingWire.ValidDllInventory(valid with { Entries = new[] { Entry(1), Entry(1) } }));
            Assert(ReportingWire.ValidDllInventory(valid with { Entries = new[] { Entry(1), Entry(1) with { Role = ReportingDllRole.Staged } } }));
            Assert(!ReportingWire.ValidDllInventory(valid with { Status = (ReportingDllInventoryStatus)999 }));
            Assert(!ReportingWire.ValidDllInventory(valid with { UnreadableFiles = 1 }));
            Assert(!ReportingWire.ValidDllInventory(valid with { Status = ReportingDllInventoryStatus.Unavailable }));
            Assert(!ReportingWire.ValidDllInventory(valid with { Entries = null! }));
            Assert(!ReportingWire.ValidDllInventory(valid with { Entries = new ReportingDllEntry[] { null! } }));
            Assert(!ReportingWire.ValidDllInventory(valid with { Status = ReportingDllInventoryStatus.Partial, UnreadableFiles = 4097 }));
            Assert(ReportingWire.ValidDllInventory(new(ReportingDllInventoryStatus.Complete, [])));
            Assert(ReportingWire.ValidDllInventory(new(ReportingDllInventoryStatus.Partial, [])));
            Assert(ReportingWire.ValidDllInventory(new(ReportingDllInventoryStatus.Unavailable, [], 1)));
            var oversized = valid with { Entries = Enumerable.Range(1, 64).Select(Entry).ToArray() };
            Assert(ReportingWire.Bytes(oversized).Length > ReportingWire.MaximumDllInventoryBytes && !ReportingWire.ValidDllInventory(oversized));
            Assert(!ReportingWire.ValidDllInventory(valid with { Entries = Enumerable.Range(1, 65).Select(Entry).ToArray() }));
            return Task.CompletedTask;
        });
        await Test("DLL details are inventory-only and receipt validation rejects extra or duplicated fields", () =>
        {
            using var f = new Fixture();
            var entry = new ReportingEvent(Guid.NewGuid(), f.Client.State.SupportId, Guid.NewGuid(), f.Now, "2.3.1", ReportingEventName.InventorySnapshot,
                Items: InventoryTests.Items(), DllInventory: Details());
            Assert(ReportingWire.ValidEvent(entry, f.Now));
            Assert(!ReportingWire.ValidEvent(entry with { EventName = ReportingEventName.SessionStarted, Items = null }, f.Now));
            string json = Encoding.UTF8.GetString(ReportingWire.Bytes(new ReportingBatch(1, new[] { entry })));
            var receipt = new ReportingDelivery(ReportingStream.Inventory, f.Now, json);
            Assert(ReportingHistoryValidation.Valid(receipt, entry.SupportId));
            foreach (var altered in new[]
            {
                json.Replace("\"sha256\":", "\"filename\":\"private.dll\",\"sha256\":"),
                json.Replace("\"copies\":1", "\"copies\":1,\"copies\":1"),
                json.Replace("\"unreadable_files\":0", "\"unreadable_files\":0,\"unreadable_files\":0"),
                json.Replace(",\"role\":\"current\"", "")
            }) Assert(!ReportingHistoryValidation.Valid(receipt with { Json = altered }, entry.SupportId));
            return Task.CompletedTask;
        });
        await Test("revoking DLL details removes whole pending snapshots and keeps plain inventory, errors and receipts", async () =>
        {
            using var f = new Fixture(); Assert(f.Client.TrySavePreferences(true, true, true, true));
            Assert(f.Client.RecordInventory(f.Client.BeginInventoryCapture()!, InventoryTests.Items(), Details()));
            await f.Client.FlushOnceAsync(); var receipt = f.Client.History.LastSent.Single();
            Assert(f.Client.RecordInventory(f.Client.BeginInventoryCapture()!, InventoryTests.Items(), Details()));
            Assert(f.Client.RecordInventory(f.Client.BeginInventoryCapture()!, InventoryTests.Items()));
            Assert(f.Client.RecordUsage(ReportingEventName.SessionStarted));
            Assert(f.Client.RecordError(ReportingErrorCode.NetworkUnavailable));
            var before = Read(f).Pending.Where(item => item.DllInventory is null).ToArray();
            Assert(f.Client.TrySavePreferences(true, true, true, false));
            var after = Read(f).Pending;
            Assert(after.Count == 3 && after.Select(item => item.EventId).SequenceEqual(before.Select(item => item.EventId)));
            Assert(after.All(item => item.DllInventory is null));
            Assert(f.Client.History.LastSent.Single().Json == receipt.Json);
            Assert(f.Client.ClearPending() && f.Client.History.LastSent.Single().Json == receipt.Json);
            Assert(f.Client.TryRegenerateId() && f.Client.History.LastSent.Count == 0);
        });
        await Test("capture generation prevents details replay after consent changes, ID reset and stale cross-client captures", () =>
        {
            using var f = new Fixture(); Assert(f.Client.TrySavePreferences(false, true, false, true));
            var old = f.Client.BeginInventoryCapture()!;
            using var other = f.NewClient();
            Assert(other.TrySavePreferences(false, true, false, false));
            Assert(!f.Client.RecordInventory(old, InventoryTests.Items(), Details()));
            Assert(other.TrySavePreferences(false, true, false, true));
            Assert(!f.Client.RecordInventory(old, InventoryTests.Items(), Details()));
            var current = f.Client.BeginInventoryCapture()!;
            Assert(current.IncludeDllInventory && f.Client.RecordInventory(current, InventoryTests.Items(), Details()));
            Assert(other.TryRegenerateId() && !f.Client.RecordInventory(current, InventoryTests.Items(), Details()));
            return Task.CompletedTask;
        });
        await Test("late accepted DLL batch cannot resurrect consent-revoked events or overwrite last sent copy", async () =>
        {
            using var f = new Fixture(); Assert(f.Client.TrySavePreferences(false, true, false, true));
            Assert(f.Client.RecordInventory(f.Client.BeginInventoryCapture()!, InventoryTests.Items(), Details()));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Transport.Responder = async (_, _) =>
            {
                entered.SetResult(); await release.Task;
                return new(HttpStatusCode.Accepted) { Content = new StringContent("{\"accepted\":1,\"duplicates\":0}") };
            };
            var sending = f.Client.FlushOnceAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var other = f.NewClient(); Assert(other.TrySavePreferences(false, true, false, false));
            release.SetResult(); await sending;
            Assert(f.Client.State.PendingCount == 0 && f.Client.History.LastSent.Count == 0 && !f.Client.State.Preferences.DetailedPlugins);
        });
        await Test("DLL event and batch sizes remain within existing ceilings", async () =>
        {
            using var f = new Fixture(); Assert(f.Client.TrySavePreferences(false, true, false, true));
            var entries = new List<ReportingDllEntry>();
            for (int index = 1; index <= 64; index++)
            {
                var next = entries.Append(Entry(index)).ToArray();
                if (!ReportingWire.ValidDllInventory(new(ReportingDllInventoryStatus.Complete, next))) break;
                entries.Add(Entry(index));
            }
            Assert(entries.Count > 1);
            var detail = new ReportingDllInventory(ReportingDllInventoryStatus.Partial, entries);
            for (int index = 0; index < 25; index++)
                Assert(f.Client.RecordInventory(f.Client.BeginInventoryCapture()!, InventoryTests.Items(), detail));
            Assert(Read(f).Pending.All(item => ReportingWire.Bytes(item).Length <= 8192));
            await f.Client.FlushOnceAsync();
            Assert(Encoding.UTF8.GetByteCount(f.Transport.Requests.Single().Json) <= ReportingWire.MaximumRequestBytes);
            Assert(f.Client.State.PendingCount > 0);
        });
        return passed;
    }

    private static ReportingDllEntry Entry(int value) => new(value.ToString("x64"), ReportingDllProduct.Unknown,
        ReportingDllRole.Current, 1, null, null, ReportingDllVersionSource.Unknown);
    private static ReportingDllInventory Details() => new(ReportingDllInventoryStatus.Complete, new[] { Entry(1) });
    private static ReportingDocument Read(Fixture fixture) => JsonSerializer.Deserialize<ReportingDocument>(File.ReadAllText(fixture.StatePath), ReportingWire.Json)!;
    private static void Assert(bool condition) { if (!condition) throw new Exception("Detailed DLL inventory assertion failed."); }
}
