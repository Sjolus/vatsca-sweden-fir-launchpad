using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

// Local synthetic files and injected transport only. Reading history never starts delivery.
internal static class HistoryTests
{
    internal static async Task<int> Run()
    {
        int passed = 0;
        await Test("history starts empty and local refresh separates all pending streams without HTTP", () =>
        {
            using var f = new Fixture();
            Assert(f.Client.History.StorageAvailable && f.Client.History.LastSent.Count == 0);
            Assert(f.Client.TrySavePreferences(true, true, true));
            Assert(f.Client.RecordUsage(ReportingEventName.SessionStarted));
            Assert(f.Client.RecordUsage(ReportingEventName.SetupStep, new(Stage: ReportingStage.Applications)));
            Assert(f.Client.RecordError(ReportingErrorCode.NetworkUnavailable));
            Assert(f.Client.RecordInventory(f.Client.BeginInventoryCapture()!, InventoryTests.Items()));
            var history = f.Client.History;
            Assert(history.SupportId == f.Client.State.SupportId && history.Preferences == f.Client.State.Preferences);
            Assert(history.UsagePending == 2 && history.InventoryPending == 1 && history.ErrorPending == 1);
            Assert(history.LastSent.Count == 0 && f.Transport.Requests.IsEmpty);
            using (f.LockStore()) Assert(!f.Client.History.StorageAvailable);
            Assert(f.Client.History.StorageAvailable && f.Transport.Requests.IsEmpty);
            return Task.CompletedTask;
        });

        await Test("acknowledged copies preserve outgoing JSON and only the newest delivery per stream", async () =>
        {
            using var f = new Fixture(); Assert(f.Client.TrySavePreferences(true, true, true));
            foreach (var stream in Enum.GetValues<ReportingStream>()) await Send(f, stream);
            var before = f.Client.History;
            Assert(before.LastSent.Count == 4 && before.LastSent.Select(copy => copy.Stream).Distinct().Count() == 4);
            foreach (var copy in before.LastSent)
            {
                Assert(copy.ConfirmedAt == f.Now && copy.ConfirmedAt.Kind == DateTimeKind.Utc);
                Assert(copy.Json == f.Transport.Requests.Single(request => Stream(request) == copy.Stream).Json);
                Assert(copy.Stream == ReportingStream.ManualReport ? copy.Reference == "LP_SYNTHETIC_0001" : copy.Reference is null);
            }
            var oldUsage = before.LastSent.Single(copy => copy.Stream == ReportingStream.Usage);
            f.Now = f.Now.AddMinutes(1); await Send(f, ReportingStream.Usage);
            var after = f.Client.History;
            Assert(after.LastSent.Count == 4 && after.LastSent.Single(copy => copy.Stream == ReportingStream.Usage).Json != oldUsage.Json);
            Assert(after.LastSent.Single(copy => copy.Stream == ReportingStream.Usage).ConfirmedAt == f.Now);
            Assert(before.LastSent.Single(copy => copy.Stream == ReportingStream.Usage) == oldUsage, "Earlier snapshots must not mutate.");
            Assert(after.LastSent is not IList<ReportingDelivery> list || list.IsReadOnly, "History should expose a read-only snapshot.");
            using var upgraded = f.NewClient("2.9.0");
            Assert(upgraded.History.LastSent.SequenceEqual(after.LastSent), "Local copies survive a normal application update.");
            f.Now = f.Now.AddDays(40);
            Assert(upgraded.History.LastSent.SequenceEqual(after.LastSent), "Sent copies are not the seven-day unsent queue or a mirror of server retention.");
        });

        await Test("valid mixed and all-duplicate acknowledgements save the immutable batch", async () =>
        {
            foreach (int accepted in new[] { 0, 1, 2 })
            {
                using var f = new Fixture(); Assert(f.Client.TrySavePreferences(true, false));
                Assert(f.Client.RecordUsage(ReportingEventName.SessionStarted));
                Assert(f.Client.RecordUsage(ReportingEventName.SetupStep, new(Stage: ReportingStage.Welcome)));
                f.Transport.Responder = (_, _) => Task.FromResult(Response(HttpStatusCode.Accepted, $"{{\"accepted\":{accepted},\"duplicates\":{2 - accepted}}}"));
                await f.Client.FlushOnceAsync();
                Assert(f.Client.History.UsagePending == 0);
                Assert(f.Client.History.LastSent.Single().Json == f.Transport.Requests.Single().Json);
            }
        });

        await Test("invalid acknowledgements, transport failure and permanent rejection never replace sent history", async () =>
        {
            foreach (var response in new (HttpStatusCode Status, string Body)[]
            {
                (HttpStatusCode.Accepted, "{\"accepted\":0,\"duplicates\":0}"),
                (HttpStatusCode.Accepted, "{\"accepted\":-1,\"duplicates\":2}"),
                (HttpStatusCode.Accepted, "{\"accepted\":2147483647,\"duplicates\":2147483647}"),
                (HttpStatusCode.Accepted, "{\"accepted\":1}"),
                (HttpStatusCode.Accepted, "{\"accepted\":1,\"duplicates\":0,\"unexpected\":true}"),
                (HttpStatusCode.Accepted, "not JSON"),
                (HttpStatusCode.OK, "{\"accepted\":1,\"duplicates\":0}"),
                (HttpStatusCode.BadRequest, "{}"), (HttpStatusCode.Conflict, "{}"), (HttpStatusCode.Gone, "{}"),
                (HttpStatusCode.TooManyRequests, "{}"), (HttpStatusCode.ServiceUnavailable, "{}")
            })
            {
                using var f = new Fixture(); Assert(f.Client.TrySavePreferences(true, false)); await Send(f, ReportingStream.Usage);
                var initial = f.Client.History;
                if (!initial.StorageAvailable || initial.LastSent.Count != 1)
                    throw new Exception($"Initial acknowledged copy missing before {response.Status}: " + Diagnostics(f, initial));
                var prior = initial.LastSent.Single();
                Assert(f.Client.RecordUsage(ReportingEventName.SetupStep, new(Stage: ReportingStage.Gng)));
                f.Transport.Responder = (_, _) => Task.FromResult(Response(response.Status, response.Body));
                await f.Client.FlushOnceAsync();
                Assert(f.Client.History.LastSent.Single() == prior, $"{response.Status} must not replace a valid sent copy.");
                if (response.Status is HttpStatusCode.BadRequest or HttpStatusCode.Conflict or HttpStatusCode.Gone)
                    Assert(f.Client.History.UsagePending == 0, "A permanent rejection may remove pending data without creating a receipt.");
            }
            using var failed = new Fixture(); Assert(failed.Client.TrySavePreferences(true, false));
            Assert(failed.Client.RecordUsage(ReportingEventName.SessionStarted));
            failed.Transport.Responder = (_, _) => throw new HttpRequestException("Synthetic transport failure");
            await failed.Client.FlushOnceAsync();
            Assert(failed.Client.History.LastSent.Count == 0 && failed.Client.History.UsagePending == 1);
        });

        await Test("manual history stores exact reviewed formatting only after a valid reference", async () =>
        {
            using var f = new Fixture();
            var draft = f.Client.PrepareReport(ReportingApplication.Gng, ReportingCategory.Gng, false);
            string formatted = JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(draft.Json), new JsonSerializerOptions { WriteIndented = true }) + "\n";
            var reviewed = new PreparedReport(draft.SupportId, draft.ReportId, formatted);
            var result = await f.Client.SendReportAsync(reviewed, default);
            Assert(result.Succeeded && result.Reference == "LP_SYNTHETIC_0001");
            var prior = f.Client.History.LastSent.Single();
            Assert(prior.Stream == ReportingStream.ManualReport && prior.Json == formatted && f.Transport.Requests.Single().Json == formatted);
            foreach (var body in new[] { "{}", "{\"reference\":\"short\"}", "{\"reference\":\"LP_SYNTHETIC_0001\",\"extra\":true}", "not JSON" })
            {
                f.Transport.Responder = (_, _) => Task.FromResult(Response(HttpStatusCode.Created, body));
                Assert(!(await f.Client.SendReportAsync(reviewed, default)).Succeeded);
                Assert(f.Client.History.LastSent.Single() == prior);
            }
        });

        await Test("withdrawal and ClearPending retain historical copies while ID regeneration removes them", async () =>
        {
            using var f = new Fixture(); Assert(f.Client.TrySavePreferences(true, true, true));
            foreach (var stream in Enum.GetValues<ReportingStream>()) await Send(f, stream);
            var saved = f.Client.History.LastSent.ToArray(); var oldId = f.Client.State.SupportId;
            Assert(saved.Single(copy => copy.Stream == ReportingStream.Inventory).Json.Contains("items", StringComparison.Ordinal));
            Assert(f.Client.TrySavePreferences(false, false, false));
            Assert(f.Client.History.LastSent.SequenceEqual(saved), "Turning off collection must not rewrite historical outgoing data.");
            Assert(f.Client.ClearPending() && f.Client.History.LastSent.SequenceEqual(saved));
            using (var reopened = f.NewClient()) Assert(reopened.History.LastSent.SequenceEqual(saved));
            Assert(f.Client.TryRegenerateId() && f.Client.History.LastSent.Count == 0 && f.Client.History.SupportId != oldId);
            Assert(!File.ReadAllText(f.StatePath).Contains(oldId.ToString(), StringComparison.Ordinal));
        });

        await Test("late automatic and manual acknowledgements cannot overwrite a newer identity or generation", async () =>
        {
            foreach (bool manual in new[] { false, true })
            foreach (string change in new[] { "rotate", "clear", "withdraw" })
            {
                using var f = new Fixture(); Assert(f.Client.TrySavePreferences(true, true, true));
                await Send(f, manual ? ReportingStream.ManualReport : ReportingStream.Usage);
                var prior = f.Client.History.LastSent.Single();
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                f.Transport.Responder = async (_, _) =>
                {
                    entered.SetResult(); await release.Task;
                    return manual ? Response(HttpStatusCode.Created, "{\"reference\":\"LP_LATE_SYNTHETIC\"}") : Response(HttpStatusCode.Accepted, "{\"accepted\":1,\"duplicates\":0}");
                };
                Task<ReportSendResult>? sendingReport = null; Task? sendingBatch = null;
                if (manual) sendingReport = f.Client.SendReportAsync(f.Client.PrepareReport(ReportingApplication.Gng, ReportingCategory.Gng, false), default);
                else { Assert(f.Client.RecordUsage(ReportingEventName.SetupStep, new(Stage: ReportingStage.Gng))); sendingBatch = f.Client.FlushOnceAsync(); }
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                // Another process changes the disk document; the sender has no watcher in this fixture.
                using var other = f.NewClient();
                Assert(change switch { "rotate" => other.TryRegenerateId(), "clear" => other.ClearPending(), _ => other.TrySavePreferences(false, false, false) });
                release.SetResult();
                if (sendingReport is not null)
                {
                    var outcome = await sendingReport;
                    Assert(!outcome.Succeeded && outcome.Reference is null, "A superseded manual send must not expose its old reference.");
                }
                else await sendingBatch!;
                var history = other.History;
                Assert(change == "rotate" ? history.LastSent.Count == 0 : history.LastSent.Single() == prior);
            }
        });

        await Test("shutdown prevents a delayed accepted copy from recreating local storage", async () =>
        {
            using var f = new Fixture(); Assert(f.Client.TrySavePreferences(true, false));
            Assert(f.Client.RecordUsage(ReportingEventName.SessionStarted));
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Transport.Responder = async (_, _) => { entered.SetResult(); await release.Task; return Response(HttpStatusCode.Accepted, "{\"accepted\":1,\"duplicates\":0}"); };
            var sending = f.Client.FlushOnceAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            f.Client.Dispose(); string before = File.ReadAllText(f.StatePath); release.SetResult(); await sending;
            Assert(File.ReadAllText(f.StatePath) == before);
            using var reopened = f.NewClient(); Assert(reopened.History.LastSent.Count == 0);
        });

        await Test("a received manual report stays successful when its local receipt write fails", async () =>
        {
            using var f = new Fixture(); FileStream? held = null;
            try
            {
                f.Transport.Responder = (_, _) => { held = f.LockStore(); return Task.FromResult(Response(HttpStatusCode.Created, "{\"reference\":\"LP_SYNTHETIC_0001\"}")); };
                var result = await f.Client.SendReportAsync(f.Client.PrepareReport(ReportingApplication.Gng, ReportingCategory.Gng, false), default);
                Assert(result.Succeeded && result.Reference == "LP_SYNTHETIC_0001");
                Assert(result.Message.Contains("local", StringComparison.OrdinalIgnoreCase) && result.Message.Contains("saved", StringComparison.OrdinalIgnoreCase));
            }
            finally { held?.Dispose(); }
            Assert(f.Client.History.StorageAvailable && f.Client.History.LastSent.Count == 0);
        });

        await Test("an acknowledged batch with a failed local write retries unchanged and records a duplicate ACK", async () =>
        {
            using var f = new Fixture(); Assert(f.Client.TrySavePreferences(true, false));
            Assert(f.Client.RecordUsage(ReportingEventName.SessionStarted)); FileStream? held = null;
            try
            {
                f.Transport.Responder = (_, _) => { held = f.LockStore(); return Task.FromResult(Response(HttpStatusCode.Accepted, "{\"accepted\":1,\"duplicates\":0}")); };
                await f.Client.FlushOnceAsync();
            }
            finally { held?.Dispose(); }
            Assert(f.Client.History.LastSent.Count == 0 && f.Client.History.UsagePending == 1);
            f.Transport.Responder = (_, _) => Task.FromResult(Response(HttpStatusCode.Accepted, "{\"accepted\":0,\"duplicates\":1}"));
            await f.Client.FlushOnceAsync();
            var requests = f.Transport.Requests.ToArray(); Assert(requests.Length == 2 && requests[0].Json == requests[1].Json);
            Assert(f.Client.History.UsagePending == 0 && f.Client.History.LastSent.Single().Json == requests[0].Json);
        });

        await Test("legacy reporting documents without sent copies load without inferring any delivery", () =>
        {
            using var f = new Fixture(); Assert(f.Client.TrySavePreferences(true, false));
            Assert(f.Client.RecordUsage(ReportingEventName.SessionStarted));
            var json = JsonNode.Parse(File.ReadAllText(f.StatePath))!.AsObject(); json.Remove("last_sent");
            File.WriteAllText(f.StatePath, json.ToJsonString());
            using var next = f.NewClient("2.9.0");
            Assert(next.History.StorageAvailable && next.History.LastSent.Count == 0 && next.History.UsagePending == 1 && f.Transport.Requests.IsEmpty);
            return Task.CompletedTask;
        });

        await Test("corrupt, foreign, duplicate, oversized and invalid-stream sent copies fail closed", async () =>
        {
            using var f = new Fixture(); await Send(f, ReportingStream.ManualReport);
            var valid = Read(f); var copy = valid.LastSent!.Single();
            List<List<ReportingDelivery>> badCopies =
            [
                [copy, copy],
                [copy with { Stream = ReportingStream.Usage, Reference = null }],
                [copy with { ConfirmedAt = DateTime.SpecifyKind(copy.ConfirmedAt, DateTimeKind.Local) }],
                [copy with { Reference = "short" }],
                [copy with { Json = "not JSON" }],
                [copy with { Json = copy.Json.Replace("\"application\":", "\"application\":\"launchpad\",\"application\":", StringComparison.Ordinal) }],
                [copy with { Json = copy.Json[..^1] + ",\"private_note\":\"SYNTHETIC\"}" }],
                [copy with { Json = copy.Json.Replace(valid.SupportId.ToString(), Guid.NewGuid().ToString(), StringComparison.Ordinal) }],
                [copy with { Json = new string(' ', ReportingWire.MaximumReportBytes) + copy.Json }]
            ];
            foreach (var invalid in badCopies)
            {
                File.WriteAllBytes(f.StatePath, ReportingWire.Bytes(valid with { LastSent = invalid }));
                using var next = f.NewClient();
                Assert(!next.History.StorageAvailable, "Invalid sent-copy storage must not be treated as a valid history.");
                int requests = f.Transport.Requests.Count; await next.FlushOnceAsync(); Assert(f.Transport.Requests.Count == requests);
            }
            foreach (JsonNode invalidStream in new JsonNode[] { JsonValue.Create("invalid_stream")!, JsonValue.Create(999)! })
            {
                var corrupt = JsonNode.Parse(ReportingWire.Bytes(valid))!;
                corrupt["last_sent"]![0]!["stream"] = invalidStream;
                File.WriteAllText(f.StatePath, corrupt.ToJsonString());
                using var next = f.NewClient(); Assert(!next.History.StorageAvailable);
            }
            using (var automatic = new Fixture())
            {
                Assert(automatic.Client.TrySavePreferences(true, false)); await Send(automatic, ReportingStream.Usage);
                string stored = File.ReadAllText(automatic.StatePath);
                foreach (string corrupt in new[]
                {
                    stored.Replace("\"stream\":\"usage\",", "", StringComparison.Ordinal),
                    stored.Replace("\"stream\":\"usage\"", "\"stream\":\"usage\",\"stream\":\"usage\"", StringComparison.Ordinal)
                })
                {
                    Assert(corrupt != stored); File.WriteAllText(automatic.StatePath, corrupt);
                    using var next = automatic.NewClient(); Assert(!next.History.StorageAvailable, "Missing or duplicate receipt wrapper fields must not be inferred.");
                }
            }
            File.WriteAllText(f.StatePath, new string(' ', ReportingStore.MaximumBytes + 1));
            using var oversized = f.NewClient(); Assert(!oversized.History.StorageAvailable);
        });

        await Test("receipt persistence stays within the shared byte cap and evicts pending usage before errors", async () =>
        {
            using var f = new Fixture(); Assert(f.Client.TrySavePreferences(true, true, true));
            for (int i = 0; i < 20; i++) Assert(f.Client.RecordUsage(ReportingEventName.OperationFinished, LargeDetails()));
            var draft = f.Client.PrepareReport(ReportingApplication.Gng, ReportingCategory.Gng, true);
            Assert(Encoding.UTF8.GetByteCount(draft.Json) > 4096);
            var store = new ReportingStore(f.Root); Assert(store.Read(out var document));
            var usage = Enumerable.Range(0, 60).Select(_ => LargeEvent(document!.SupportId, f.Now, ReportingEventName.OperationFinished)).ToArray();
            var errors = Enumerable.Range(0, 3000).Select(_ => LargeEvent(document!.SupportId, f.Now, ReportingEventName.OperationFailed)).ToArray();
            int low = 0, high = errors.Length;
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                var trial = document! with { Pending = usage.Concat(errors.Take(middle)).ToList() };
                if (ReportingWire.Bytes(trial).Length <= ReportingStore.MaximumBytes - 256) low = middle; else high = middle - 1;
            }
            Assert(low > 100);
            Assert(store.Change(current => current with { Pending = usage.Concat(errors.Take(low)).ToList() }, out var full));
            int originalCount = full!.Pending.Count, originalErrors = full.Pending.Count(entry => ReportingWire.IsError(entry.EventName));
            Assert(new FileInfo(f.StatePath).Length > ReportingStore.MaximumBytes - 2048);
            var result = await f.Client.SendReportAsync(draft, default);
            Assert(result.Succeeded && f.Client.History.LastSent.Single().Json == draft.Json);
            var after = Read(f);
            Assert(new FileInfo(f.StatePath).Length <= ReportingStore.MaximumBytes && after.Pending.Count < originalCount);
            Assert(after.Pending.Count(entry => ReportingWire.IsError(entry.EventName)) == originalErrors, "Usage is evicted before pending errors.");
            Assert(f.Client.History.StorageAvailable && !Directory.EnumerateFiles(f.Root, "*.tmp").Any());
        });

        return passed;
        async Task Test(string name, Func<Task> body) { await body(); passed++; Console.WriteLine("PASS " + name); }
    }

    private static async Task Send(Fixture f, ReportingStream stream)
    {
        switch (stream)
        {
            case ReportingStream.Usage: Assert(f.Client.RecordUsage(ReportingEventName.SetupStep, new(Stage: ReportingStage.Applications))); break;
            case ReportingStream.Inventory: Assert(f.Client.RecordInventory(f.Client.BeginInventoryCapture()!, InventoryTests.Items())); break;
            case ReportingStream.Errors: Assert(f.Client.RecordError(ReportingErrorCode.NetworkUnavailable)); break;
            case ReportingStream.ManualReport:
                Assert((await f.Client.SendReportAsync(f.Client.PrepareReport(ReportingApplication.Gng, ReportingCategory.Gng, true), default)).Succeeded); return;
        }
        await f.Client.FlushOnceAsync();
    }
    private static ReportingStream Stream(Request request) => request.Path.EndsWith("/reports", StringComparison.Ordinal) ? ReportingStream.ManualReport :
        request.Path.EndsWith("inventory/batches", StringComparison.Ordinal) ? ReportingStream.Inventory : request.Path.EndsWith("errors/batches", StringComparison.Ordinal) ? ReportingStream.Errors : ReportingStream.Usage;
    private static ReportingDocument Read(Fixture f) { Assert(new ReportingStore(f.Root).Read(out var document)); return document!; }
    private static string Diagnostics(Fixture f, ReportingHistoryState history)
    {
        bool read = new ReportingStore(f.Root).Read(out var document);
        return $"history available={history.StorageAvailable}, copies={history.LastSent.Count}, requests={f.Transport.Requests.Count}, " +
            $"direct read={read}, stored copies={document?.LastSent?.Count}, pending={document?.Pending.Count}, bytes={new FileInfo(f.StatePath).Length}";
    }
    private static ReportingDetails LargeDetails() => new(OperationId: Guid.NewGuid(), Application: ReportingApplication.Euroscope, Operation: ReportingOperation.RestoreCleanup,
        Outcome: ReportingOutcome.Completed, Stage: ReportingStage.Prepare, DurationBucket: ReportingDurationBucket.Under5m,
        PackageVersion: "12345.12345.12345-preview.12345.12345", PreviousVersion: "12345.12345.12345-preview.12345.12345",
        Source: ReportingSource.Discovery, RestartRequired: false, RecoveryAvailable: true);
    private static ReportingEvent LargeEvent(Guid supportId, DateTime now, ReportingEventName name) => new(Guid.NewGuid(), supportId, Guid.NewGuid(), now, "2.2.0", name,
        OperationId: Guid.NewGuid(), Application: ReportingApplication.Euroscope, Operation: ReportingOperation.RestoreCleanup,
        Outcome: ReportingOutcome.Completed, Stage: ReportingStage.Prepare, ErrorCode: ReportingWire.IsError(name) ? ReportingErrorCode.UnexpectedError : null,
        DurationBucket: ReportingDurationBucket.Under5m, PackageVersion: "12345.12345.12345-preview.12345.12345",
        PreviousVersion: "12345.12345.12345-preview.12345.12345", Source: ReportingSource.Discovery, RestartRequired: false, RecoveryAvailable: true);
    private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static void Assert(bool condition, string message = "Reporting-history assertion failed.") { if (!condition) throw new Exception(message); }
}
