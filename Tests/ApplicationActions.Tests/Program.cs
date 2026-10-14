using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

// Every process observation and action is injected. Never enumerate, start, show
// or close a real process, and never load settings, browser receipts or profiles.
int passed = 0;
var stopped = new ApplicationActionObservation([], []);
var identity = new ApplicationProcessIdentity(123, new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc), @"C:\Synthetic\Client.exe");
var running = new ApplicationActionObservation([identity], [identity]);

Run("start review excludes folders and Launchpad and explains unused or running apps", () =>
{
    var probed = new List<string>();
    var choices = ApplicationBatchActions.Prepare(ApplicationBatchAction.Start,
        [Target("ready"), Target("folder") with { IsFolder = true }, Target("launchpad") with { IsLaunchpad = true },
         Target("unused") with { IsNotUsed = true }, Target("running")], target =>
        { probed.Add(target.Id); return target.Id == "running" ? running : stopped; });
    Assert(choices.Count == 3 && choices.Single(c => c.Target.Id == "ready").IsSelected);
    Assert(!choices.Single(c => c.Target.Id == "unused").CanSelect && !choices.Single(c => c.Target.Id == "running").CanSelect);
    Assert(probed.SequenceEqual(new[] { "ready", "running" }));
});
Run("close review includes verified running applications even when marked not used", () =>
{
    var choices = ApplicationBatchActions.Prepare(ApplicationBatchAction.Close,
        [Target("unused") with { IsNotUsed = true }, Target("stopped")], target => target.Id == "unused" ? running : stopped);
    Assert(choices[0].IsSelected && choices[0].Processes.Count == 1 && !choices[1].CanSelect);
});
Run("an application without a verified window cannot be selected for closing", () =>
{
    var choices = ApplicationBatchActions.Prepare(ApplicationBatchAction.Close, [Target("headless")], _ => new([identity], []));
    choices[0].IsSelected = true;
    Assert(!choices[0].CanSelect && !choices[0].IsSelected);
});
Run("unready VatEFS is skipped without starting its server or later auto-selecting it", () =>
{
    var choices = ApplicationBatchActions.Prepare(ApplicationBatchAction.Start,
        [Target("VatEFS") with { IsBrowser = true, IsLocalBrowser = true }], _ => new([], [], "The local server is not ready."));
    int starts = 0;
    var results = ApplicationBatchActions.Execute(ApplicationBatchAction.Start, choices, _ => stopped,
        _ => { starts++; return true; }, _ => throw new Exception("Close must not be requested."));
    Assert(starts == 0 && results.Count == 0 && choices[0].Detail.Contains("not ready"));
});
Run("cancelled or unchecked selections perform no actions", () =>
{
    foreach (var action in Enum.GetValues<ApplicationBatchAction>())
    {
        var choices = ApplicationBatchActions.Prepare(action, [Target("client")], _ => action == ApplicationBatchAction.Start ? stopped : running);
        choices[0].IsSelected = false;
        var result = ApplicationBatchActions.Execute(action, choices, _ => throw new Exception("No probe expected."),
            _ => throw new Exception("No start expected."), _ => throw new Exception("No close expected."));
        Assert(result.Count == 0);
    }
});
Run("fresh start observation skips an application opened during review", () =>
{
    var choices = ApplicationBatchActions.Prepare(ApplicationBatchAction.Start, [Target("client")], _ => stopped);
    var results = ApplicationBatchActions.Execute(ApplicationBatchAction.Start, choices, _ => running,
        _ => throw new Exception("No duplicate start expected."), _ => throw new Exception("No close expected."));
    Assert(results.Count == 1 && !results[0].Requested && results[0].Message.Contains("Already running"));
});
Run("fresh start validation refuses a removed application or a pending GNG update", () =>
{
    foreach (string reason in new[] { "The executable disappeared.", "Restore the pending GNG update first." })
    {
        var choices = ApplicationBatchActions.Prepare(ApplicationBatchAction.Start, [Target("EuroScope")], _ => stopped);
        var result = ApplicationBatchActions.Execute(ApplicationBatchAction.Start, choices, _ => new([], [], reason),
            _ => throw new Exception("A blocked application must not start."), _ => false).Single();
        Assert(!result.Requested && result.Message == reason);
    }
});
Run("start receives the reviewed EuroScope profile and working directory unchanged", () =>
{
    var target = Target("EuroScope") with { IsEuroScope = true, DataFolder = @"C:\Synthetic\EuroScope",
        ProfilePath = @"C:\Synthetic\EuroScope\Sweden.prf", ProfileName = "Sweden" };
    var choices = ApplicationBatchActions.Prepare(ApplicationBatchAction.Start, [target], _ => stopped);
    var changed = target with { ProfilePath = @"C:\Synthetic\Other.prf", DataFolder = @"C:\Synthetic\Other" };
    ApplicationActionTarget? requested = null;
    var results = ApplicationBatchActions.Execute(ApplicationBatchAction.Start, choices, _ => stopped,
        value => { requested = value; return true; }, _ => false);
    Assert(results.Single().Requested && requested == target && requested != changed && choices[0].Detail.Contains("Sweden"));
});
Run("two rows configured to the same native executable start it only once", () =>
{
    var one = Target("one");
    var two = Target("two") with { LaunchPath = one.LaunchPath.ToUpperInvariant() };
    var choices = ApplicationBatchActions.Prepare(ApplicationBatchAction.Start, [one, two], _ => stopped);
    int requests = 0;
    var results = ApplicationBatchActions.Execute(ApplicationBatchAction.Start, choices, _ => stopped, _ => { requests++; return true; }, _ => false);
    Assert(requests == 1 && results.Count(result => result.Requested) == 1 && results[1].Message.Contains("another selected row"));
});
Run("separate browser applications remain distinct even when they use the same executable", () =>
{
    var choices = ApplicationBatchActions.Prepare(ApplicationBatchAction.Start,
        [Target("VatEFS") with { IsBrowser = true }, Target("VATIRIS") with { IsBrowser = true }], _ => stopped);
    int requests = 0;
    var results = ApplicationBatchActions.Execute(ApplicationBatchAction.Start, choices, _ => stopped, _ => { requests++; return true; }, _ => false);
    Assert(requests == 2 && results.All(result => result.Requested));
});
Run("close requests use immutable reviewed identities and never a new process search", () =>
{
    var original = new[] { identity };
    var choices = ApplicationBatchActions.Prepare(ApplicationBatchAction.Close, [Target("client")], _ => new(original, original));
    original[0] = identity with { Id = 999 };
    var seen = new List<ApplicationProcessIdentity>();
    var result = ApplicationBatchActions.Execute(ApplicationBatchAction.Close, choices, _ => throw new Exception("Never refresh close targets after review."),
        _ => throw new Exception("Never start during close."), process => { seen.Add(process); return true; }).Single();
    Assert(result.Requested && seen.Single() == identity && result.Message.Contains("Close requested"));
});
Run("PID reuse or a changed executable is refused by the identity-aware close boundary", () =>
{
    foreach (var actual in new[] { identity with { StartedUtc = identity.StartedUtc.AddSeconds(1) },
        identity with { ExecutablePath = @"C:\Other\Client.exe" } })
    {
        var choices = ApplicationBatchActions.Prepare(ApplicationBatchAction.Close, [Target("client")], _ => running);
        bool terminated = false;
        var result = ApplicationBatchActions.Execute(ApplicationBatchAction.Close, choices, _ => stopped, _ => false,
            expected => { if (!ApplicationProcessControl.SameIdentity(expected, actual)) return false; terminated = true; return true; }).Single();
        Assert(!result.Requested && !terminated);
    }
});
Run("duplicate reviewed process lifetimes receive only one normal close request", () =>
{
    var duplicate = identity with { ExecutablePath = identity.ExecutablePath.ToUpperInvariant() };
    var choices = ApplicationBatchActions.Prepare(ApplicationBatchAction.Close, [Target("one"), Target("two")],
        _ => new([identity, duplicate], [identity, duplicate]));
    int closes = 0;
    ApplicationBatchActions.Execute(ApplicationBatchAction.Close, choices, _ => stopped, _ => false, _ => { closes++; return true; });
    Assert(closes == 1 && choices.All(choice => choice.Processes.Count == 1));
});
Run("an untracked browser has no close target and browser review does not promise server shutdown", () =>
{
    var choices = ApplicationBatchActions.Prepare(ApplicationBatchAction.Close,
        [Target("VatEFS") with { IsBrowser = true, IsLocalBrowser = true }, Target("VATIRIS") with { IsBrowser = true }],
        target => target.Id == "VatEFS" ? running : stopped);
    Assert(choices[0].Detail.Contains("server is left running") && !choices[1].CanSelect);
});
Run("a failure for one start does not prevent other selected applications", () =>
{
    var choices = ApplicationBatchActions.Prepare(ApplicationBatchAction.Start, [Target("bad"), Target("good")], _ => stopped);
    var results = ApplicationBatchActions.Execute(ApplicationBatchAction.Start, choices, _ => stopped,
        target => target.Id == "bad" ? throw new IOException("Synthetic start failure.") : true, _ => false);
    Assert(!results[0].Requested && results[1].Requested && results[0].Message.Contains("Synthetic start failure"));
});
Run("a refused normal close never escalates and does not block another application", () =>
{
    var second = identity with { Id = 124 };
    var choices = ApplicationBatchActions.Prepare(ApplicationBatchAction.Close, [Target("one"), Target("two")],
        target => target.Id == "one" ? running : new([second], [second]));
    int calls = 0;
    var results = ApplicationBatchActions.Execute(ApplicationBatchAction.Close, choices, _ => stopped,
        _ => throw new Exception("No launch during close."), process => { calls++; return process.Id == second.Id; });
    Assert(calls == 2 && !results[0].Requested && results[1].Requested);
});
Run("failed observation is a disabled review row rather than permission to act", () =>
{
    var choices = ApplicationBatchActions.Prepare(ApplicationBatchAction.Start, [Target("bad"), Target("good")],
        target => target.Id == "bad" ? throw new IOException("Synthetic location unavailable.") : stopped);
    Assert(!choices[0].CanSelect && choices[1].CanSelect);
});
Run("an unknown bulk action is refused before any probe or process action", () =>
{
    bool refused = false;
    try { ApplicationBatchActions.Prepare((ApplicationBatchAction)99, [Target("client")], _ => throw new Exception("No observation expected.")); }
    catch (ArgumentOutOfRangeException) { refused = true; }
    Assert(refused);
});

Console.WriteLine($"{passed} application action checks passed. Synthetic observations only; no real processes, windows or user data accessed.");

void Run(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
static ApplicationActionTarget Target(string id) => new(id, id, @"C:\Synthetic\" + id + ".exe");
static void Assert(bool condition) { if (!condition) throw new InvalidOperationException("Application action assertion failed."); }
