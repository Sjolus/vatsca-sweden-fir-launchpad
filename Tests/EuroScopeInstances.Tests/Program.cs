using System.Text.Json;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

// Synthetic launch sets, receipts and process identities only. Never starts, inspects or closes
// real processes and never reads the user's settings or instance receipts.
var root = Path.Combine(Path.GetTempPath(), "Launchpad-EuroScope-instances-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
try
{
    var exe = Path.Combine(root, "EuroScope.exe");
    var twr = Path.Combine(root, "ESAA TWR Ö.prf");
    var app = Path.Combine(root, "ESAA APP ACC.prf");
    ApplicationProcessIdentity Identity(int id, int minute = 0) => new(id, new DateTime(2026, 11, 9, 12, minute, 0, DateTimeKind.Utc), exe);
    EuroScopeLaunchEntry Entry(string profile, int delay = 5, bool enabled = true) => new(Guid.NewGuid(), profile, delay, enabled);
    EuroScopeInstanceRecord Record(EuroScopeLaunchEntry entry, int order, ApplicationProcessIdentity process) =>
        new(entry.Id, entry.ProfilePath, EuroScopeLaunchPlanner.ProfileName(entry.ProfilePath), order, process);

    // Planner
    Run("planner keeps order, skips disabled entries and starts the first instance without delay", () =>
    {
        var first = Entry(twr, delay: 30);
        var disabled = Entry(app, enabled: false);
        var second = Entry(app, delay: 12);

        var steps = EuroScopeLaunchPlanner.Build([first, disabled, second], [], _ => null);

        Equal(2, steps.Count);
        Equal(first.Id, steps[0].Entry.Id); Equal(1, steps[0].Order); Equal(0, steps[0].DelaySeconds);
        Equal(second.Id, steps[1].Entry.Id); Equal(2, steps[1].Order); Equal(12, steps[1].DelaySeconds);
        True(steps.All(step => step.Kind == EuroScopeLaunchStepKind.Start && step.Message is null));
        Equal("ESAA TWR Ö", steps[0].ProfileName);
    });

    Run("already running entries are left open and the next start becomes the first without delay", () =>
    {
        var first = Entry(twr);
        var second = Entry(app, delay: 20);

        var steps = EuroScopeLaunchPlanner.Build([first, second], [Record(first, 1, Identity(10))], _ => null);

        Equal(EuroScopeLaunchStepKind.AlreadyRunning, steps[0].Kind);
        True(steps[0].Message!.Contains("left open"));
        Equal(EuroScopeLaunchStepKind.Start, steps[1].Kind);
        Equal(0, steps[1].DelaySeconds);
    });

    Run("validation reasons and validator exceptions skip only that entry", () =>
    {
        var blocked = Entry(twr);
        var throws = Entry(app);
        var ok = Entry("", delay: 7);

        var steps = EuroScopeLaunchPlanner.Build([blocked, throws, ok], [],
            entry => entry.Id == blocked.Id ? "Profile is missing." :
                entry.Id == throws.Id ? throw new IOException("Synthetic read failure.") : null);

        Equal(EuroScopeLaunchStepKind.Skip, steps[0].Kind); Equal("Skipped · Profile is missing.", steps[0].Message);
        Equal(EuroScopeLaunchStepKind.Skip, steps[1].Kind); Equal("Skipped · Synthetic read failure.", steps[1].Message);
        Equal(EuroScopeLaunchStepKind.Start, steps[2].Kind); Equal(0, steps[2].DelaySeconds);
        Equal("No profile", steps[2].ProfileName);
    });

    Run("a profile used twice is warned about but not blocked", () =>
    {
        var steps = EuroScopeLaunchPlanner.Build([Entry(twr), Entry(twr.ToUpperInvariant())], [], _ => null);

        Equal(EuroScopeLaunchStepKind.Start, steps[1].Kind);
        Equal<string?>(null, steps[0].Message);
        True(steps[1].Message!.Contains("same session files"));
    });

    Run("blank profiles are not reported as duplicates", () =>
    {
        var steps = EuroScopeLaunchPlanner.Build([Entry(""), Entry("")], [], _ => null);

        True(steps.All(step => step.Message is null));
    });

    Run("normalization clamps delays, fills identifiers and bounds the entry count", () =>
    {
        var input = Enumerable.Range(0, EuroScopeLaunchEntry.MaxEntries + 4)
            .Select(i => new EuroScopeLaunchEntry(i == 0 ? Guid.Empty : Guid.NewGuid(), i == 1 ? null! : twr,
                i == 2 ? -5 : i == 3 ? 10_000 : 4, true)).Append(null!).ToArray();

        var normalized = EuroScopeLaunchPlanner.Normalize(input);

        Equal(EuroScopeLaunchEntry.MaxEntries, normalized.Count);
        True(normalized[0].Id != Guid.Empty);
        Equal(string.Empty, normalized[1].ProfilePath);
        Equal(0, normalized[2].DelaySeconds);
        Equal(EuroScopeLaunchEntry.MaxDelaySeconds, normalized[3].DelaySeconds);
        Equal(0, EuroScopeLaunchPlanner.Normalize(null).Count);
    });

    // Tracker
    var alive = new HashSet<int>();
    EuroScopeInstanceTracker Tracker(string name) => new(Path.Combine(root, name), identity => alive.Contains(identity.Id));

    Run("receipts round-trip in launch order and only live processes are returned", () =>
    {
        alive.Clear(); alive.UnionWith([1, 2]);
        var tracker = Tracker("order.json");
        var a = Entry(twr); var b = Entry(app);

        tracker.Record(Record(b, 2, Identity(2)));
        tracker.Record(Record(a, 1, Identity(1)));

        var live = Tracker("order.json").Live();

        Equal(2, live.Count);
        Equal(a.Id, live[0].EntryId); Equal(b.Id, live[1].EntryId);
        Equal(Identity(1), live[0].Process);
    });

    Run("exited processes are pruned and an empty receipt file is removed", () =>
    {
        alive.Clear(); alive.UnionWith([3, 4]);
        var tracker = Tracker("prune.json");
        tracker.Record(Record(Entry(twr), 1, Identity(3)));
        tracker.Record(Record(Entry(app), 2, Identity(4)));

        alive.Remove(3);
        Equal(1, tracker.Live().Count);
        Equal(1, JsonSerializer.Deserialize<List<EuroScopeInstanceRecord>>(File.ReadAllText(tracker.ReceiptPath))!.Count);

        alive.Clear();
        Equal(0, tracker.Live().Count);
        True(!File.Exists(tracker.ReceiptPath));
    });

    Run("recording the same entry again replaces its earlier process", () =>
    {
        alive.Clear(); alive.UnionWith([5, 6]);
        var tracker = Tracker("replace.json");
        var entry = Entry(twr);

        tracker.Record(Record(entry, 1, Identity(5)));
        tracker.Record(Record(entry, 1, Identity(6)));
        var live = tracker.Live();

        Equal(1, live.Count);
        Equal(6, live[0].Process.Id);
    });

    Run("receipts are bounded to the most recent records", () =>
    {
        alive.Clear(); alive.UnionWith(Enumerable.Range(100, 40));
        var tracker = Tracker("bounded.json");

        for (int i = 0; i < 40; i++) tracker.Record(Record(Entry(twr), i, Identity(100 + i)));
        var live = tracker.Live();

        Equal(32, live.Count);
        Equal(8, live[0].Order);
    });

    Run("malformed, oversized and missing receipts read as empty without throwing", () =>
    {
        alive.Clear(); alive.Add(1);
        File.WriteAllText(Path.Combine(root, "malformed.json"), "{ not json");
        Equal(0, Tracker("malformed.json").Live().Count);

        File.WriteAllText(Path.Combine(root, "nullprocess.json"), "[{\"EntryId\":\"" + Guid.NewGuid() + "\",\"Order\":1,\"Process\":null}]");
        Equal(0, Tracker("nullprocess.json").Live().Count);

        var oversized = Path.Combine(root, "oversized.json");
        File.WriteAllText(oversized, new string(' ', 33 * 1024));
        Equal(0, Tracker("oversized.json").Live().Count);

        Equal(0, Tracker("missing.json").Live().Count);
    });

    Run("untracked copies are matched by full identity, not by PID alone", () =>
    {
        var entry = Entry(twr);
        var tracked = new[] { Record(entry, 1, Identity(7)) };
        var reusedPid = Identity(7, minute: 5);
        var otherPath = Identity(8) with { ExecutablePath = Path.Combine(root, "Other", "EuroScope.exe") };

        var untracked = EuroScopeInstanceTracker.Untracked([Identity(7), reusedPid, Identity(8), otherPath], tracked);

        Equal(3, untracked.Count);
        True(untracked.Contains(reusedPid) && untracked.Contains(Identity(8)) && untracked.Contains(otherPath));
    });

    // Settings model
    Run("Copy gives editors their own launch-set lists", () =>
    {
        var set = new EuroScopeLaunchSetDefinition(Guid.NewGuid(), "Day", [Entry(twr)]);
        var settings = new AppSettings { EuroScopeLaunchSets = [set], ActiveEuroScopeLaunchSetId = set.Id };

        var copy = settings.Copy();
        True(!ReferenceEquals(settings.EuroScopeLaunchSets, copy.EuroScopeLaunchSets));

        copy.EuroScopeLaunchSets.Add(new(Guid.NewGuid(), "Night", []));

        Equal(1, settings.EuroScopeLaunchSets.Count);
        Equal(set.Id, copy.ActiveEuroScopeLaunchSetId);
    });

    Run("settings JSON round-trips named sets", () =>
    {
        var set = new EuroScopeLaunchSetDefinition(Guid.NewGuid(), "Åre & Östersund", [Entry(twr, delay: 9, enabled: false)]);

        var json = JsonSerializer.Serialize(new AppSettings { EuroScopeLaunchSets = [set], ActiveEuroScopeLaunchSetId = set.Id });

        var read = JsonSerializer.Deserialize<AppSettings>(json)!;
        Equal(set.Id, read.ActiveEuroScopeLaunchSetId);
        Equal(set.Name, read.EuroScopeLaunchSets[0].Name);
        True(read.EuroScopeLaunchSets[0].Entries.SequenceEqual(set.Entries));

        var older = JsonSerializer.Deserialize<AppSettings>("{\"LastEuroscopeProfile\":\"x\"}")!;
        Equal(0, older.EuroScopeLaunchSets.Count);
        Equal<Guid?>(null, older.ActiveEuroScopeLaunchSetId);
    });

    Console.WriteLine($"{passed} EuroScope launch-set checks passed; no processes started.");
}
finally
{
    var full = Path.GetFullPath(root);
    if (!string.Equals(Path.GetDirectoryName(full), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(full).StartsWith("Launchpad-EuroScope-instances-tests-", StringComparison.Ordinal))
        throw new Exception("Refusing cleanup outside the synthetic temporary root.");
    Directory.Delete(full, recursive: true);
}

void Run(string name, Action action)
{
    action();
    passed++;
    Console.WriteLine("PASS " + name);
}

static void True(bool value)
{
    if (!value) throw new InvalidOperationException("Launch-set assertion failed.");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}
