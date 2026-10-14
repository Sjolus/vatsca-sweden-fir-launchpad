using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;
using VatscaUpdateChecker;

// Synthetic temporary storage + injected HTTP handler only. No production facade initialization.
if (args.SequenceEqual(new[] { "--emit-wire" })) { WireFixture.Write(); return; }
int passed = 0;
await Test("unset choices never queue or transmit, but an explicitly submitted report works", async () =>
{
    using var f = new Fixture();
    Assert(!f.Client.State.Preferences.Confirmed && f.Client.State.StorageAvailable);
    Assert(!f.Client.RecordUsage(ReportingEventName.SessionStarted));
    Assert(!f.Client.RecordError(ReportingErrorCode.UnexpectedError));
    await f.Client.FlushOnceAsync();
    Assert(f.Client.State.PendingCount == 0 && f.Transport.Requests.IsEmpty);
    var report = f.Client.PrepareReport(ReportingApplication.Gng, ReportingCategory.Gng, true);
    Assert(JsonDocument.Parse(report.Json).RootElement.GetProperty("context").GetArrayLength() == 2);
    var result = await f.Client.SendReportAsync(report, default);
    Assert(result.Succeeded && result.Reference == "LP_SYNTHETIC_0001");
    Assert(f.Transport.Requests.Single().Json == report.Json);
});
await Test("all four confirmed consent combinations permit only their own stream", async () =>
{
    foreach (bool usage in new[] { false, true })
    foreach (bool errors in new[] { false, true })
    {
        using var f = new Fixture(); var id = f.Client.State.SupportId;
        Assert(f.Client.TrySavePreferences(usage, errors));
        Assert(f.Client.RecordUsage(ReportingEventName.SessionStarted) == usage);
        Assert(f.Client.RecordError(ReportingErrorCode.NetworkUnavailable) == errors);
        await f.Client.FlushOnceAsync(); await f.Client.FlushOnceAsync();
        Assert(f.Transport.Requests.Count == (usage ? 1 : 0) + (errors ? 1 : 0));
        Assert(f.Client.State.SupportId == id && f.Client.State.Preferences == new ReportingPreferences(true, usage, errors));
        foreach (var request in f.Transport.Requests)
            foreach (var item in JsonDocument.Parse(request.Json).RootElement.GetProperty("events").EnumerateArray())
                Assert(item.GetProperty("support_id").GetGuid() == id);
    }
});
await Test("identity and confirmed settings survive a new client/version", () =>
{
    using var f = new Fixture(); var id = f.Client.State.SupportId;
    Assert(ReportingWire.IsUuid(id) && f.Client.TrySavePreferences(true, false));
    using var next = f.NewClient("2.3.0");
    Assert(next.State.SupportId == id && next.State.Preferences == new ReportingPreferences(true, true, false));
    return Task.CompletedTask;
});
await Test("consent enable does not backfill earlier memory context", async () =>
{
    using var f = new Fixture(); f.Client.RecordUsage(ReportingEventName.SetupStep, new(Stage: ReportingStage.Welcome));
    Assert(f.Client.TrySavePreferences(true, false));
    await f.Client.FlushOnceAsync(); Assert(f.Transport.Requests.IsEmpty);
    f.Client.RecordUsage(ReportingEventName.SetupStep, new(Stage: ReportingStage.Applications));
    await f.Client.FlushOnceAsync();
    var events = JsonDocument.Parse(f.Transport.Requests.Single().Json).RootElement.GetProperty("events");
    Assert(events.GetArrayLength() == 1 && events[0].GetProperty("stage").GetString() == "applications");
});
await Test("withdrawing one category deletes only its pending events and keeps ID", async () =>
{
    using var f = new Fixture(); f.Client.TrySavePreferences(true, true); var id = f.Client.State.SupportId;
    f.Client.RecordUsage(ReportingEventName.SessionStarted); f.Client.RecordError(ReportingErrorCode.OperationStopped);
    Assert(f.Client.TrySavePreferences(false, true)); Assert(f.Client.State.PendingCount == 1);
    await f.Client.FlushOnceAsync();
    Assert(f.Transport.Requests.Single().Path.EndsWith("errors/batches") && f.Client.State.SupportId == id);
    Assert(f.Client.TrySavePreferences(false, false) && f.Client.State.Preferences.Confirmed);
});
await Test("failed permission writes fail closed, preserve durable choices and do not transmit", async () =>
{
    using var f = new Fixture(); f.Client.TrySavePreferences(true, true); f.Client.RecordUsage(ReportingEventName.SessionStarted);
    using (var held = f.LockStore()) Assert(!f.Client.TrySavePreferences(false, false));
    Assert(!f.Client.State.StorageAvailable);
    await f.Client.FlushOnceAsync(); Assert(f.Transport.Requests.IsEmpty);
    Assert(f.Client.TrySavePreferences(false, false) && f.Client.State.PendingCount == 0 && f.Client.State.StorageAvailable);
});
await Test("rotation drops old events/context and rejects old manual drafts without a mapping", async () =>
{
    using var f = new Fixture(); f.Client.TrySavePreferences(true, true); var old = f.Client.State.SupportId;
    f.Client.RecordUsage(ReportingEventName.SessionStarted);
    var draft = f.Client.PrepareReport(ReportingApplication.Launchpad, ReportingCategory.Other, true);
    Assert(f.Client.TryRegenerateId()); var current = f.Client.State.SupportId;
    Assert(old != current && f.Client.State.PendingCount == 0 && f.Client.State.Preferences.Usage);
    Assert(!(await f.Client.SendReportAsync(draft, default)).Succeeded && f.Transport.Requests.IsEmpty);
    var replacement = f.Client.PrepareReport(ReportingApplication.Launchpad, ReportingCategory.Other, true);
    Assert(!replacement.Json.Contains(old.ToString()) && !replacement.Json.Contains(draft.ReportId.ToString()));
    Assert(JsonDocument.Parse(replacement.Json).RootElement.GetProperty("context").GetArrayLength() == 0);
    Assert(!File.ReadAllText(f.StatePath).Contains(old.ToString()));
});
await Test("clear pending keeps identity/preferences and removes both queues plus context", () =>
{
    using var f = new Fixture(); f.Client.TrySavePreferences(true, true); var id = f.Client.State.SupportId;
    f.Client.RecordUsage(ReportingEventName.SessionStarted); f.Client.RecordError(ReportingErrorCode.UnexpectedError);
    Assert(f.Client.ClearPending() && f.Client.State.PendingCount == 0 && f.Client.State.SupportId == id);
    Assert(JsonDocument.Parse(f.Client.PrepareReport(ReportingApplication.Launchpad, ReportingCategory.Other, true).Json)
        .RootElement.GetProperty("context").GetArrayLength() == 0);
    return Task.CompletedTask;
});
await Test("schema rejects identity-like version text and invalid enum/operation IDs", async () =>
{
    using var f = new Fixture(); f.Client.TrySavePreferences(true, true);
    foreach (string unsafeValue in new[] { "Tester Öberg", "1234567", "C:\\Users\\Tester\\secret", "https://vatsim.net/?token=FAKE", "1.0.0-private-CID", "1.0\npassword=FAKE", "1.0.0+FAKE" })
        Assert(!f.Client.RecordUsage(ReportingEventName.OperationFinished, new(PackageVersion: unsafeValue)));
    Assert(!f.Client.RecordUsage((ReportingEventName)99));
    Assert(!f.Client.RecordUsage(ReportingEventName.OperationFinished, new(Application: (ReportingApplication)99)));
    Assert(!f.Client.RecordUsage(ReportingEventName.OperationFinished, new(OperationId: Guid.Empty)));
    Assert(!f.Client.RecordUsage(ReportingEventName.OperationFinished, new(ErrorCode: ReportingErrorCode.Unknown)));
    Assert(!f.Client.RecordError((ReportingErrorCode)99));
    await f.Client.FlushOnceAsync(); Assert(f.Transport.Requests.IsEmpty && f.Client.State.PendingCount == 0);
});
await Test("wire enum names and normalized public versions match the strict server contract", async () =>
{
    using var f = new Fixture(); f.Client.TrySavePreferences(true, false);
    f.Client.RecordUsage(ReportingEventName.OperationFinished, new(Application: ReportingApplication.Vatefs,
        Operation: ReportingOperation.RestoreCleanup, Outcome: ReportingOutcome.Completed,
        DurationBucket: ReportingDurationBucket.Under1m, PackageVersion: "4.1.0-beta.19", PreviousVersion: "3.2.3.2"));
    await f.Client.FlushOnceAsync(); var text = f.Transport.Requests.Single().Json;
    Assert(text.Contains("\"application\":\"vatefs\"") && text.Contains("\"operation\":\"restore_cleanup\"") && text.Contains("\"duration_bucket\":\"under_1m\""));
    using var json = JsonDocument.Parse(text); var entry = json.RootElement.GetProperty("events")[0];
    Assert(entry.GetProperty("launchpad_version").GetString() == "2.2.0" && entry.GetProperty("occurred_at").GetString()!.EndsWith('Z'));
    Assert(entry.EnumerateObject().All(p => !new[] { "message", "stack", "path", "username", "cid", "email" }.Contains(p.Name)));
});
await Test("errors get first delivery and requests contain at most 25 events /64KiB", async () =>
{
    using var f = new Fixture(); f.Client.TrySavePreferences(true, true);
    for (int i = 0; i < 28; i++) Assert(f.Client.RecordUsage(ReportingEventName.SetupStep));
    Assert(f.Client.RecordError(ReportingErrorCode.PackageInvalid));
    await f.Client.FlushOnceAsync(); await f.Client.FlushOnceAsync(); await f.Client.FlushOnceAsync();
    Assert(f.Transport.Requests.First().Path.EndsWith("errors/batches") && f.Client.State.PendingCount == 0);
    Assert(f.Transport.Requests.Select(r => JsonDocument.Parse(r.Json).RootElement.GetProperty("events").GetArrayLength()).SequenceEqual(new[] { 1, 25, 3 }));
    Assert(f.Transport.Requests.All(r => Encoding.UTF8.GetByteCount(r.Json) <= 65536));
});
await Test("queue is durable, seven-day expired events never send even during backoff", async () =>
{
    using var f = new Fixture(); f.Client.TrySavePreferences(true, true);
    f.Client.RecordUsage(ReportingEventName.SessionStarted); f.Client.RecordError(ReportingErrorCode.UnexpectedError);
    using var reloaded = f.NewClient(); Assert(reloaded.State.PendingCount == 2);
    f.Transport.Responder = (_, _) => Task.FromResult(Response(HttpStatusCode.TooManyRequests, retrySeconds: 20 * 86400));
    await reloaded.FlushOnceAsync(); f.Now = f.Now.AddDays(8);
    await reloaded.FlushOnceAsync(); Assert(reloaded.State.PendingCount == 0 && f.Transport.Requests.Count == 1);
});
await Test("one MiB queue evicts usage before errors and leaves at most one bounded staging file", () =>
{
    using var f = new Fixture(); f.Client.TrySavePreferences(true, true, true);
    var store = new ReportingStore(f.Root); Assert(store.Read(out var doc));
    var error = Event(doc!.SupportId, f.Now, ReportingEventName.OperationFailed);
    var pending = new List<ReportingEvent> { error };
    for (int i = 0; i < 2100; i++) pending.Add(Event(doc.SupportId, f.Now, ReportingEventName.OperationFinished));
    while (ReportingWire.Bytes(doc with { Pending = pending }).Length > ReportingStore.MaximumBytes - 256) pending.RemoveAt(pending.Count - 1);
    Assert(store.Change(d => d with { Pending = pending }, out _));
    for (int i = 0; i < 10; i++)
    {
        f.Now = f.Now.AddMinutes(1);
        Assert(f.Client.RecordError(ReportingErrorCode.UnexpectedError,
            new(Application: ReportingApplication.Launchpad, Operation: ReportingOperation.Update, PackageVersion: "2.2.0", PreviousVersion: "2.1.0")));
    }
    Assert(new FileInfo(f.StatePath).Length <= ReportingStore.MaximumBytes);
    Assert(store.Read(out var result) && result!.Pending.Any(e => e.EventId == error.EventId));
    Assert(result!.Pending.Count(e => ReportingWire.IsError(e.EventName)) == 11);
    Assert(!Directory.EnumerateFiles(f.Root, "*.tmp").Any());
    return Task.CompletedTask;
});
await Test("429 Retry-After persists, applies to both streams and retries exact event IDs", async () =>
{
    using var f = new Fixture(); f.Client.TrySavePreferences(true, true);
    f.Client.RecordUsage(ReportingEventName.SessionStarted); f.Client.RecordError(ReportingErrorCode.NetworkUnavailable);
    f.Transport.Responder = (_, _) => Task.FromResult(Response(HttpStatusCode.TooManyRequests, retrySeconds: 120));
    await f.Client.FlushOnceAsync(); string original = f.Transport.Requests.Single().Json;
    using var restarted = f.NewClient(); await restarted.FlushOnceAsync(); Assert(f.Transport.Requests.Count == 1);
    f.Now = f.Now.AddSeconds(121); f.Transport.Responder = null;
    await restarted.FlushOnceAsync(); Assert(f.Transport.Requests.Last().Json == original);
    await restarted.FlushOnceAsync(); Assert(restarted.State.PendingCount == 0);
});
await Test("malformed acknowledgement keeps queue; atomic accepted+duplicate acknowledgement clears it", async () =>
{
    using var f = new Fixture(); f.Client.TrySavePreferences(true, false); f.Client.RecordUsage(ReportingEventName.SessionStarted);
    f.Transport.Responder = (_, _) => Task.FromResult(Response(HttpStatusCode.Accepted, "{\"accepted\":0,\"duplicates\":0}"));
    await f.Client.FlushOnceAsync(); Assert(f.Client.State.PendingCount == 1);
    f.Now = f.Now.AddHours(2);
    f.Transport.Responder = (_, _) => Task.FromResult(Response(HttpStatusCode.Accepted, "{\"accepted\":0,\"duplicates\":1}"));
    await f.Client.FlushOnceAsync(); Assert(f.Client.State.PendingCount == 0);
});
await Test("permanent validation rejection is discarded instead of retried forever", async () =>
{
    using var f = new Fixture(); f.Client.TrySavePreferences(true, false); f.Client.RecordUsage(ReportingEventName.SessionStarted);
    f.Transport.Responder = (_, _) => Task.FromResult(Response(HttpStatusCode.BadRequest));
    await f.Client.FlushOnceAsync(); await f.Client.FlushOnceAsync();
    Assert(f.Client.State.PendingCount == 0 && f.Transport.Requests.Count == 1);
});
await Test("manual retry preserves exact payload/report ID and requires a durable acknowledgement", async () =>
{
    using var f = new Fixture(); var report = f.Client.PrepareReport(ReportingApplication.Trackaudio, ReportingCategory.Update, false);
    Assert(!report.Json.Contains("context"));
    f.Transport.Responder = (_, _) => throw new HttpRequestException("SYNTHETIC private URL token MUST NOT appear in result");
    var failed = await f.Client.SendReportAsync(report, default);
    Assert(!failed.Succeeded && !failed.Message.Contains("private"));
    f.Transport.Responder = null; Assert((await f.Client.SendReportAsync(report, default)).Succeeded);
    Assert(f.Transport.Requests.Count == 2 && f.Transport.Requests.All(r => r.Json == report.Json));
});
await Test("a possibly received manual report retains its exact retry after context ages", async () =>
{
    using var f = new Fixture(); f.Client.RecordError(ReportingErrorCode.NetworkUnavailable);
    var report = f.Client.PrepareReport(ReportingApplication.Gng, ReportingCategory.Gng, true);
    f.Transport.Responder = (_, _) => throw new HttpRequestException("Synthetic acknowledgement lost");
    Assert(!(await f.Client.SendReportAsync(report, default)).Succeeded);
    f.Now = f.Now.AddDays(8); f.Transport.Responder = null;
    Assert((await f.Client.SendReportAsync(report, default)).Succeeded);
    Assert(f.Transport.Requests.All(r => r.Json == report.Json));
    var fresh = f.Client.PrepareReport(ReportingApplication.Gng, ReportingCategory.Gng, true);
    Assert(JsonDocument.Parse(fresh.Json).RootElement.GetProperty("context").GetArrayLength() == 0);
});
await Test("oversized/untrusted server response cannot become a report reference", async () =>
{
    using var f = new Fixture(); var report = f.Client.PrepareReport(ReportingApplication.Launchpad, ReportingCategory.Other, false);
    foreach (var body in new[] { "{\"reference\":\"https://evil.example/private\"}", new string('x', 5000), "{\"reference\":\"VALID_ID123\",\"extra\":true}" })
    {
        f.Transport.Responder = (_, _) => Task.FromResult(Response(HttpStatusCode.Created, body));
        Assert(!(await f.Client.SendReportAsync(report, default)).Succeeded);
    }
});
await Test("withdrawing consent cancels an in-flight automatic request and empties queue", async () =>
{
    using var f = new Fixture(); f.Client.TrySavePreferences(true, false); f.Client.RecordUsage(ReportingEventName.SessionStarted);
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    f.Transport.Responder = async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return Response(HttpStatusCode.Accepted); };
    var sending = f.Client.FlushOnceAsync(); await entered.Task;
    Assert(f.Client.TrySavePreferences(false, false)); await sending;
    Assert(f.Client.State.PendingCount == 0);
});
await Test("rotation cancels an in-flight manual report and never retries it under a new ID", async () =>
{
    using var f = new Fixture(); var report = f.Client.PrepareReport(ReportingApplication.Launchpad, ReportingCategory.Other, false);
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    f.Transport.Responder = async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return Response(HttpStatusCode.Created); };
    var sending = f.Client.SendReportAsync(report, default); await entered.Task;
    Assert(f.Client.TryRegenerateId()); Assert(!(await sending).Succeeded);
    Assert(!(await f.Client.SendReportAsync(report, default)).Succeeded && f.Transport.Requests.Count == 1);
});
await Test("cross-client identity observation invalidates an old draft and earlier context", async () =>
{
    using var f = new Fixture(); var old = f.Client.PrepareReport(ReportingApplication.Launchpad, ReportingCategory.Other, false);
    using var other = f.NewClient(); Assert(other.TryRegenerateId());
    Assert(f.Client.State.SupportId == other.State.SupportId);
    Assert(!(await f.Client.SendReportAsync(old, default)).Succeeded && f.Transport.Requests.IsEmpty);
});
await Test("corrupt and oversized state fail closed without replacement", async () =>
{
    foreach (string content in new[] { "{broken", new string('x', 1024 * 1024 + 1), "{\"schema_version\":999}" })
    {
        using var f = new Fixture(); f.Client.Dispose(); File.WriteAllText(f.StatePath, content);
        using var other = f.NewClient(); Assert(!other.State.StorageAvailable && !other.TrySavePreferences(true, true));
        other.RecordError(ReportingErrorCode.UnexpectedError); await other.FlushOnceAsync();
        Assert(f.Transport.Requests.IsEmpty && File.ReadAllText(f.StatePath) == content);
    }
});
await Test("a concurrent process lease prevents identity overwrite or automatic sending", async () =>
{
    using var f = new Fixture(); var original = File.ReadAllText(f.StatePath);
    using (var held = f.LockStore())
    {
        Assert(!f.Client.TryRegenerateId());
        using var other = f.NewClient(); Assert(!other.State.StorageAvailable);
        await other.FlushOnceAsync();
    }
    Assert(File.ReadAllText(f.StatePath) == original && f.Transport.Requests.IsEmpty);
});
await Test("fatal error queues only with permission and never starts a send in the failing client", async () =>
{
    using var f = new Fixture(); f.Client.TrySavePreferences(false, true);
    Assert(f.Client.RecordError(ReportingErrorCode.UnexpectedError, fatal: true));
    await f.Client.FlushOnceAsync(); Assert(f.Transport.Requests.IsEmpty && f.Client.State.PendingCount == 1);
    using var next = f.NewClient(); await next.FlushOnceAsync(); Assert(f.Transport.Requests.Count == 1);
    using var noConsent = new Fixture(); Assert(!noConsent.Client.RecordError(ReportingErrorCode.UnexpectedError, fatal: true));
    Assert(noConsent.Client.State.PendingCount == 0);
});
await Test("request timeout is bounded and retains a retryable immutable manual draft", async () =>
{
    using var f = new Fixture(TimeSpan.FromMilliseconds(50));
    f.Transport.Responder = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Response(HttpStatusCode.Created); };
    var draft = f.Client.PrepareReport(ReportingApplication.Launchpad, ReportingCategory.Other, false);
    var result = await f.Client.SendReportAsync(draft, default);
    Assert(!result.Succeeded && f.Transport.Requests.Single().Json == draft.Json);
});
await Test("a pre-cancelled manual send returns safely without transport", async () =>
{
    using var f = new Fixture();
    var draft = f.Client.PrepareReport(ReportingApplication.Launchpad, ReportingCategory.Other, false);
    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
    Assert(!(await f.Client.SendReportAsync(draft, cancellation.Token)).Succeeded && f.Transport.Requests.IsEmpty);
});
await Test("shutdown prevents delayed transport completion or callers from rewriting reviewed storage", async () =>
{
    using var f = new Fixture(); f.Client.TrySavePreferences(true, false); f.Client.RecordUsage(ReportingEventName.SessionStarted);
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    // An uncooperative transport still must not write after shutdown.
    f.Transport.Responder = async (_, _) => { entered.SetResult(); await release.Task; return Response(HttpStatusCode.Accepted, "{\"accepted\":1,\"duplicates\":0}"); };
    var sending = f.Client.FlushOnceAsync(); await entered.Task;
    f.Client.Dispose(); var reviewed = File.ReadAllText(f.StatePath);
    Assert(!f.Client.TrySavePreferences(false, false) && !f.Client.TryRegenerateId() && !f.Client.ClearPending());
    Assert(!f.Client.RecordError(ReportingErrorCode.UnexpectedError));
    release.SetResult(); await sending;
    Assert(File.ReadAllText(f.StatePath) == reviewed);
});
await Test("store close drains an active local write and blocks every later write", async () =>
{
    using var f = new Fixture(); var store = new ReportingStore(f.Root);
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var release = new ManualResetEventSlim();
    var writing = Task.Run(() => store.Change(document => { entered.SetResult(); release.Wait(); return document; }, out _));
    await entered.Task;
    var closing = Task.Run(store.Close);
    release.Set(); Assert(await writing); await closing;
    Assert(!store.Read(out _) && !store.Change(document => document with { SupportId = Guid.NewGuid() }, out _));
});
await Test("stale staging output is replaced safely without accumulated temporary files", () =>
{
    using var f = new Fixture(); File.WriteAllText(Path.Combine(f.Root, "reporting.pending.tmp"), "synthetic interrupted write");
    Assert(f.Client.TrySavePreferences(false, true));
    Assert(!Directory.EnumerateFiles(f.Root, "*.tmp").Any() && f.Client.State.Preferences.Errors);
    return Task.CompletedTask;
});
await Test("normal session starts only after usage permission and toggles never duplicate it", () =>
{
    using var f = new Fixture(); f.Client.StartNormalSession(); Assert(f.Client.State.PendingCount == 0);
    Assert(f.Client.TrySavePreferences(false, true) && f.Client.State.PendingCount == 0);
    f.Now = f.Now.AddHours(1);
    Assert(f.Client.TrySavePreferences(true, true) && f.Client.State.PendingCount == 1);
    var store = new ReportingStore(f.Root); Assert(store.Read(out var doc));
    Assert(doc!.Pending.Single().EventName == ReportingEventName.SessionStarted && doc.Pending.Single().OccurredAt == f.Now);
    Assert(f.Client.TrySavePreferences(true, false) && f.Client.State.PendingCount == 1);
    Assert(f.Client.TrySavePreferences(false, false) && f.Client.State.PendingCount == 0);
    Assert(f.Client.TrySavePreferences(true, false) && f.Client.State.PendingCount == 0);
    return Task.CompletedTask;
});
await Test("a new normal launch gets one new session while helper-like clients remain inert", () =>
{
    using var f = new Fixture(); Assert(f.Client.TrySavePreferences(true, true));
    Assert(f.Client.State.PendingCount == 0); f.Client.StartNormalSession(); f.Client.StartNormalSession();
    Assert(f.Client.State.PendingCount == 1);
    using var next = f.NewClient(); Assert(next.State.PendingCount == 1); next.StartNormalSession();
    var store = new ReportingStore(f.Root); Assert(store.Read(out var doc));
    Assert(doc!.Pending.Count == 2 && doc.Pending.Select(e => e.SessionId).Distinct().Count() == 2);
    var oldId = next.State.SupportId; Assert(next.TryRegenerateId());
    Assert(next.State.SupportId != oldId && next.State.PendingCount == 1);
    Assert(next.ClearPending() && next.State.PendingCount == 0);
    return Task.CompletedTask;
});
await Test("public versions normalize release tags and GNG while unsafe metadata is omitted without losing outcomes", () =>
{
    Assert(ReportingEvents.PublicVersion("v1.4.0") == "1.4.0");
    Assert(ReportingEvents.PublicVersion("2610/01 rev.3") == "2610.1.3");
    Assert(ReportingEvents.PublicVersion("2610/01  rev.3") == "2610.1.3");
    Assert(ReportingEvents.PublicVersion("4.1.0-beta.19") == "4.1.0-beta.19");
    Assert(ReportingEvents.PublicVersion("2.3.0-rc.4") == "2.3.0-rc.4");
    Assert(ReportingWire.NormalizeAppVersion("2.3.0-rc.4+synthetic.commit") == "2.3.0-rc.4");
    foreach (var unsafeVersion in new[] { "SYNTHETIC PRIVATE NAME", "C:\\Users\\synthetic\\profile", "1.4.0+private", "1.2.3-secret", "https://private.invalid/token", "1.2.3\nsecret", "٢٦١٠/٠١ rev.٣" })
    {
        Assert(ReportingEvents.PublicVersion(unsafeVersion) is null);
        using var f = new Fixture(); f.Client.TrySavePreferences(true, false);
        Assert(f.Client.RecordUsage(ReportingEventName.OperationFinished, new(Outcome: ReportingOutcome.Completed,
            PackageVersion: ReportingEvents.PublicVersion(unsafeVersion))));
        var store = new ReportingStore(f.Root); Assert(store.Read(out var doc));
        var json = Encoding.UTF8.GetString(ReportingWire.Bytes(doc!.Pending.Single()));
        Assert(!json.Contains("package_version") && json.Contains("completed"));
    }
    return Task.CompletedTask;
});
await Test("error classification sends fixed codes without exception text and excludes guards or declined UAC", () =>
{
    const string privateMessage = "SYNTHETIC PRIVATE PATH AND CREDENTIAL";
    Assert(ReportingEvents.Classify(new TimeoutException(privateMessage)) == ReportingErrorCode.DownloadTimeout);
    Assert(ReportingEvents.Classify(new HttpRequestException(privateMessage)) == ReportingErrorCode.NetworkUnavailable);
    Assert(ReportingEvents.Classify(new InvalidDataException(privateMessage)) == ReportingErrorCode.PackageInvalid);
    Assert(ReportingEvents.Classify(new SoftwareInstallBehaviorException(privateMessage)) == ReportingErrorCode.InstallationFailed);
    Assert(ReportingEvents.Classify(new Exception(privateMessage)) == ReportingErrorCode.UnexpectedError);
    foreach (var expected in new Exception[] { new InvalidOperationException(privateMessage), new OperationCanceledException(privateMessage), new Win32Exception(1223, privateMessage) })
        Assert(ReportingEvents.Classify(expected) is null);
    using var f = new Fixture(); f.Client.TrySavePreferences(false, true);
    Assert(f.Client.RecordError(ReportingEvents.Classify(new TimeoutException(privateMessage))!.Value));
    Assert(!File.ReadAllText(f.StatePath).Contains(privateMessage));
    return Task.CompletedTask;
});
await Test("typed operation timings are consumed once and software mapping is complete", () =>
{
    Assert(ReportingEvents.Finish(ReportingApplication.Vacs, ReportingOperation.Install) is null);
    ReportingEvents.Start(ReportingApplication.Vacs, ReportingOperation.Install);
    Assert(ReportingEvents.Finish(ReportingApplication.Vacs, ReportingOperation.Install) is not null);
    Assert(ReportingEvents.Finish(ReportingApplication.Vacs, ReportingOperation.Install) is null);
    Assert(Enum.GetValues<SoftwareApp>().Select(ReportingEvents.ForSoftware).Distinct().Count() == Enum.GetValues<SoftwareApp>().Length);
    Assert(Enum.GetValues<SoftwareApp>().All(app => ReportingEvents.ForSoftware(app) != ReportingApplication.Launchpad));
    return Task.CompletedTask;
});
await Test("repeated errors are combined before storage access while fatal envelopes remain distinct", () =>
{
    using var f = new Fixture(); f.Client.TrySavePreferences(false, true);
    var details = new ReportingDetails(Application: ReportingApplication.Vacs, Operation: ReportingOperation.Update);
    Assert(f.Client.RecordError(ReportingErrorCode.NetworkUnavailable, details));
    string first = File.ReadAllText(f.StatePath);
    using (var held = f.LockStore())
        for (int i = 0; i < 100; i++) Assert(!f.Client.RecordError(ReportingErrorCode.NetworkUnavailable, details with { OperationId = Guid.NewGuid() }));
    Assert(File.ReadAllText(f.StatePath) == first && f.Client.State.SuppressedCount == 100);
    Assert(f.Client.RecordError(ReportingErrorCode.NetworkUnavailable, details, fatal: true));
    Assert(f.Client.State.PendingCount == 2);
    f.Now = f.Now.AddMinutes(1); Assert(f.Client.RecordError(ReportingErrorCode.NetworkUnavailable, details));
    Assert(f.Client.State.PendingCount == 3);
    Assert(f.Client.TryRegenerateId() && f.Client.State.SuppressedCount == 0 && f.Client.State.PendingCount == 0);
    return Task.CompletedTask;
});
passed += await InventoryTests.Run();
passed += PluginVersionTests.Run();
passed += PluginRegistrationTests.Run();
passed += await DllInventoryTests.Run();
passed += await HistoryTests.Run();
passed += HistoryPresentationTests.Run();
Console.WriteLine($"{passed} reporting checks passed. Synthetic temporary storage and fake HTTP only.");

async Task Test(string name, Func<Task> body) { await body(); passed++; Console.WriteLine("PASS " + name); }
void Assert(bool condition) { if (!condition) throw new Exception("Reporting assertion failed."); }
static HttpResponseMessage Response(HttpStatusCode status, string body = "{}", int? retrySeconds = null)
{
    var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    if (retrySeconds is { } value) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(value));
    return response;
}
static ReportingEvent Event(Guid support, DateTime now, ReportingEventName name) => new(Guid.NewGuid(), support,
    Guid.NewGuid(), now, "2.2.0", name, Application: ReportingApplication.Euroscope, Operation: ReportingOperation.RestoreCleanup,
    Outcome: ReportingOutcome.Completed, Stage: ReportingStage.Prepare, DurationBucket: ReportingDurationBucket.Under5m,
    PackageVersion: "12345.12345.12345-preview.12345.12345", PreviousVersion: "12345.12345.12345-preview.12345.12345",
    Source: ReportingSource.Discovery, RestartRequired: false, RecoveryAvailable: true);

sealed record Request(string Path, string Json);
sealed class FakeTransport : HttpMessageHandler
{
    internal ConcurrentQueue<Request> Requests { get; } = new();
    internal Func<Request, CancellationToken, Task<HttpResponseMessage>>? Responder { get; set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Post || request.Content?.Headers.ContentType?.MediaType != "application/json" || request.Content.Headers.ContentEncoding.Count != 0)
            throw new Exception("Invalid reporting request headers.");
        var sent = new Request(request.RequestUri!.AbsolutePath, await request.Content.ReadAsStringAsync(cancellationToken)); Requests.Enqueue(sent);
        if (Responder is not null) return await Responder(sent, cancellationToken);
        if (sent.Path.EndsWith("/reports")) return new(HttpStatusCode.Created) { Content = new StringContent("{\"reference\":\"LP_SYNTHETIC_0001\"}") };
        using var json = JsonDocument.Parse(sent.Json); int count = json.RootElement.GetProperty("events").GetArrayLength();
        return new(HttpStatusCode.Accepted) { Content = new StringContent($"{{\"accepted\":{count},\"duplicates\":0}}") };
    }
}
sealed class Fixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "Launchpad-ReportingTests-" + Guid.NewGuid().ToString("N"));
    internal string StatePath => Path.Combine(Root, "reporting.json");
    internal FakeTransport Transport { get; } = new();
    private readonly HttpClient _http;
    private readonly TimeSpan? _timeout;
    internal DateTime Now { get; set; } = DateTime.SpecifyKind(new DateTime(2026, 10, 12, 12, 0, 0), DateTimeKind.Utc);
    internal ReportingClient Client { get; }
    internal Fixture(TimeSpan? timeout = null)
    {
        _timeout = timeout; _http = new(Transport) { Timeout = Timeout.InfiniteTimeSpan };
        Client = NewClient();
    }
    internal ReportingClient NewClient(string version = "2.2.0.0") => new(Root, version, _http, new Uri("https://synthetic.invalid/api/v1/"), () => Now, _timeout);
    internal FileStream LockStore() => new(Path.Combine(Root, "reporting.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    public void Dispose()
    {
        Client.Dispose(); _http.Dispose();
        var path = Path.GetFullPath(Root);
        if (path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(path).StartsWith("Launchpad-ReportingTests-", StringComparison.Ordinal)) Directory.Delete(path, true);
    }
}
