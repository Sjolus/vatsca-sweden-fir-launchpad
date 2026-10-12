using System.Text.Json;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

// Store paths and recovery folders are synthetic. No production store or shell action is used.
var root = Path.Combine(Path.GetTempPath(), "Launchpad-ActivityTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
try
{
    Test("reading empty history never creates files", () =>
    {
        var path = Path.Combine(root, "empty", "history.json");
        var result = new ActivityHistoryStore(path).Read();
        Assert(!result.Unavailable && result.Entries.Count == 0 && !Directory.Exists(Path.GetDirectoryName(path)));
    });
    Test("outcomes survive reload, newest first, with recovery locations", () =>
    {
        var path = Path.Combine(root, "reload", "history.json");
        var store = new ActivityHistoryStore(path);
        var old = Entry() with { Time = DateTimeOffset.UtcNow.AddDays(-2), Version = "2610/01 rev.3", Application = ActivityApplication.Gng };
        var recent = Entry() with { Outcome = ActivityOutcome.Stopped, RecoveryFolder = Path.Combine(root, "backup"), RestartRequired = true, PreviousVersion = "0.0.12" };
        Assert(store.Append(recent) && store.Append(old));
        var result = new ActivityHistoryStore(path).Read();
        Assert(!result.Unavailable && result.Entries.SequenceEqual(new[] { recent, old }));
        Assert(recent.Summary.Contains("stopped") && recent.Summary.Contains("Restart was requested"));
        Assert(recent.VersionChange == "0.0.12 → 0.0.15");
    });
    Test("retention keeps exactly the latest 100 outcomes", () =>
    {
        var store = new ActivityHistoryStore(Path.Combine(root, "bounded", "history.json"));
        var start = DateTimeOffset.UtcNow.AddHours(-5);
        for (int i = 0; i < 125; i++)
            if (!store.Append(Entry() with { Time = start.AddMinutes(i), Version = "1.0." + i }))
                throw new Exception($"Append {i} failed; history unavailable: {store.Read().Unavailable}; {store.LastFailure}.");
        var result = store.Read().Entries;
        Assert(result.Count == 100 && result[0].Version == "1.0.124" && result[^1].Version == "1.0.25");
    });
    Test("idempotent entry replacement cannot duplicate an outcome", () =>
    {
        var store = new ActivityHistoryStore(Path.Combine(root, "id", "history.json"));
        var entry = Entry(); Assert(store.Append(entry));
        Assert(store.Append(entry with { Outcome = ActivityOutcome.NeedsAttention }));
        Assert(store.Read().Entries.Count == 1 && store.Read().Entries[0].Outcome == ActivityOutcome.NeedsAttention);
    });
    Test("unknown schema and corrupt histories are preserved, not silently replaced", () =>
    {
        foreach (var json in new[] { "{broken", "{\"Schema\":42,\"Entries\":[]}", "null" })
        {
            var path = Path.Combine(root, Guid.NewGuid() + ".json"); File.WriteAllText(path, json);
            var store = new ActivityHistoryStore(path);
            Assert(store.Read().Unavailable && !store.Append(Entry()) && File.ReadAllText(path) == json);
        }
    });
    Test("oversized history is refused without rewriting it", () =>
    {
        var path = Path.Combine(root, "oversize.json"); File.WriteAllText(path, new string(' ', 600_000));
        var store = new ActivityHistoryStore(path);
        Assert(store.Read().Unavailable && !store.Append(Entry()) && new FileInfo(path).Length == 600_000);
    });
    Test("invalid enums, private text and unsafe recovery targets cannot enter the history", () =>
    {
        var store = new ActivityHistoryStore(Path.Combine(root, "invalid", "history.json"));
        foreach (var entry in new[]
        {
            Entry() with { Id = Guid.Empty }, Entry() with { Application = (ActivityApplication)999 },
            Entry() with { Operation = (ActivityOperation)999 }, Entry() with { Outcome = (ActivityOutcome)999 },
            Entry() with { Version = "password=private" }, Entry() with { Version = "1.0\nprivate" },
            Entry() with { Version = "https://example.com/?token=private" },
            Entry() with { RecoveryFolder = "relative/folder" }, Entry() with { RecoveryFolder = @"\\server\share" },
            Entry() with { RecoveryFolder = "C:\\" }, Entry() with { RecoveryFolder = "C:\\folder:stream" },
            Entry() with { DataFolder = "../profiles" }
        }) Assert(!store.Append(entry));
        Assert(store.Read().Entries.Count == 0);
    });
    Test("storage failures are reported without throwing or clobbering data", () =>
    {
        var parent = Path.Combine(root, "not-a-folder"); File.WriteAllText(parent, "keep");
        var store = new ActivityHistoryStore(Path.Combine(parent, "history.json"));
        Assert(!store.Append(Entry()) && File.ReadAllText(parent) == "keep");
    });
    Test("another writer's lease prevents lost history", () =>
    {
        var directory = Path.Combine(root, "locked"); Directory.CreateDirectory(directory);
        var store = new ActivityHistoryStore(Path.Combine(directory, "history.json")); Assert(store.Append(Entry()));
        using (var lease = new FileStream(Path.Combine(directory, "history.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert(!store.Append(Entry()));
        Assert(store.Read().Entries.Count == 1 && store.Append(Entry()) && store.Read().Entries.Count == 2);
    });
    Test("serialization contains metadata only, not rendered instructions", () =>
    {
        var path = Path.Combine(root, "metadata", "history.json");
        Assert(new ActivityHistoryStore(path).Append(Entry()));
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var names = json.RootElement.GetProperty("Entries")[0].EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert(names.SetEquals(new[] { "Id", "Time", "Application", "Operation", "Outcome", "Version", "RecoveryFolder", "DataFolder", "RestartRequired", "PreviousVersion" }));
    });
    Test("duplicate or invalid saved entries do not become actionable", () =>
    {
        var path = Path.Combine(root, "tampered.json"); var entry = Entry();
        File.WriteAllText(path, JsonSerializer.Serialize(new { Schema = 1, Entries = new[] { entry, entry } }));
        Assert(new ActivityHistoryStore(path).Read().Unavailable);
        File.WriteAllText(path, JsonSerializer.Serialize(new { Schema = 1, Entries = new[] { entry with { RecoveryFolder = @"\\server\share" } } }));
        Assert(new ActivityHistoryStore(path).Read().Unavailable);
    });
    Test("recovery actions require an existing local folder", () =>
    {
        var recovery = Path.Combine(root, "recovery"); Directory.CreateDirectory(recovery);
        Assert(ActivityHistoryService.CanOpenRecoveryFolder(recovery));
        Assert(!ActivityHistoryService.CanOpenRecoveryFolder(Path.Combine(root, "missing")));
        Assert(!ActivityHistoryService.CanOpenRecoveryFolder("https://example.com"));
        var file = Path.Combine(root, "file.exe"); File.WriteAllText(file, "inert");
        Assert(!ActivityHistoryService.CanOpenRecoveryFolder(file));
        Assert(ActivityHistoryService.SameDataFolder(recovery, recovery + Path.DirectorySeparatorChar));
        Assert(!ActivityHistoryService.SameDataFolder(recovery, Path.Combine(root, "other")));
    });
    Console.WriteLine($"{passed} activity-history checks passed.");
}
finally
{
    // The only removed tree is this test's freshly created, absolute temporary directory.
    var fullRoot = Path.GetFullPath(root);
    if (fullRoot.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(fullRoot).StartsWith("Launchpad-ActivityTests-", StringComparison.Ordinal)) Directory.Delete(fullRoot, true);
}

ActivityEntry Entry() => new(Guid.NewGuid(), DateTimeOffset.UtcNow, ActivityApplication.VatEfs, ActivityOperation.Update, ActivityOutcome.Completed, "0.0.15");
void Test(string name, Action body) { body(); passed++; Console.WriteLine("PASS " + name); }
void Assert(bool value) { if (!value) throw new Exception("Activity history assertion failed."); }
