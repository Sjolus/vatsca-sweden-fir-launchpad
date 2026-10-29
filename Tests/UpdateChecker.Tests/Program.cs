using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

// A pumped synchronization context exercises the same owner-thread contract as
// WPF without creating a window, reading user data or making HTTP requests.
int passed = 0, failed = 0;
Run("GNG newer package keeps every notification on its owner thread", () => GngRemote("newer"));
Run("GNG current package finishes on its owner thread", () => GngRemote("current"));
Run("GNG malformed page becomes Error instead of leaving Checking", () => GngRemote("parse"));
Run("GNG asynchronous transport failure becomes Error on its owner thread", () => GngRemote("fault"));
foreach (var mode in new[] { "unconfigured", "missing", "empty", "filename", "pending" })
    Run("GNG " + mode + " location does not request a package page", () => GngLocal(mode));
foreach (var latest in new[] { "—", "Parse error", "Not checked" })
    Run("GNG local refresh with " + latest + " latest metadata reports installed, not up to date", () => GngUnknownLatest(latest));
Run("GNG local refresh immediately reflects restore and clears the testing flag", GngRefreshAfterRestore);
Run("GNG local refresh respects pending recovery before trusting sector files or receipts", GngRefreshPending);
Run("GNG public check reads testing provenance without changing its version comparison", GngRemoteTesting);
Run("GitHub newer release keeps every notification on its owner thread", () => GitHubRemote("newer"));
Run("GitHub current release finishes on its owner thread", () => GitHubRemote("current"));
Run("GitHub absent release becomes Error instead of leaving Checking", () => GitHubRemote("missing"));
Run("GitHub asynchronous transport failure becomes Error on its owner thread", () => GitHubRemote("fault"));
Run("public metadata cache, cooldown and bounds", MetadataTests.RunAsync);
Run("public catalog validation rejects incomplete publication metadata", () =>
{
    Require(!UpdateChecker.ValidVersionPage(new("https://api.github.com/repos/example/tool/releases/latest"), "{}"), "An empty object is not a release.");
    Require(!UpdateChecker.ValidVersionPage(new("https://api.github.com/repos/example/tool/releases/latest"), "{\"tag_name\":\"v2.5.2\",\"draft\":true,\"prerelease\":false}"), "A draft is not a public latest release.");
    Require(UpdateChecker.ValidVersionPage(new("https://api.github.com/repos/example/tool/releases/latest"), "{\"tag_name\":\"v2.5.2\",\"draft\":false,\"prerelease\":false}"), "Valid public latest release rejected.");
    Require(!UpdateChecker.ValidVersionPage(new("https://api.github.com/repos/example/tool/releases"), "[{\"tag_name\":\"v2.5.2\"}]"), "Missing flags accepted.");
    return Task.CompletedTask;
});
Run("failed checks emit one typed callback while unconfigured locations do not", async () =>
{
    var observed = new List<PublicUpdateCheck>();
    Action<PublicUpdateCheck, Exception> handler = (check, _) => observed.Add(check);
    UpdateChecker.FailureObserved += handler;
    try
    {
        await GngRemote("fault");
        await GitHubRemote("fault");
        await GngLocal("unconfigured");
        Require(observed.SequenceEqual(new[] { PublicUpdateCheck.Gng, PublicUpdateCheck.GitHub }), "Failure callback count or check identity differed.");
    }
    finally { UpdateChecker.FailureObserved -= handler; }
});

Console.WriteLine($"{passed} update-check tests passed; {failed} failed. Synthetic files, injected results and no native UI or network.");
Environment.ExitCode = failed == 0 ? 0 : 1;

void Run(string name, Func<Task> test)
{
    try { OwnerContext.Run(test); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + error.Message); }
}

async Task GngRemote(string mode)
{
    using var files = new SyntheticFiles();
    files.Sector();
    var row = new CheckResult { AppName = "EuroScope (GNG Pack)", HasFontsCheck = true, Status = CheckStatus.Checking };
    using var observer = new OwnerObserver(row);
    var remote = new DeferredResult<string>();
    int guards = 0;
    var checking = UpdateChecker.CheckGng(row, files.Root, remote.Fetch, path =>
    {
        Require(path == files.Root, "The pending-update guard received a different folder.");
        guards++;
    });
    Require(guards == 1 && remote.Calls == 1 && !checking.IsCompleted, "The check must await an incomplete remote result.");
    Require(row.InstalledVersion == "2605/01  rev.1", "Local GNG metadata was not read before the asynchronous request.");
    if (mode == "fault") await remote.FailFromWorker();
    else await remote.CompleteFromWorker(mode switch
    {
        "parse" => "<table><tr><td>No package row</td></tr></table>",
        "current" => PackagePage("2605 / 01", 1),
        _ => PackagePage("2610 / 01", 3)
    });
    await checking;
    observer.AssertNotified();
    Require(row.Status == (mode == "newer" ? CheckStatus.UpdateAvailable : mode == "current" ? CheckStatus.UpToDate : CheckStatus.Error),
        "GNG did not reach the expected final status.");
    if (mode == "newer") Require(row.LatestVersion == "2610/01  rev.3" && row.DownloadUrl == "https://files.aero-nav.com/ESAA", "Latest package metadata was lost.");
    if (mode == "parse") Require(row.LatestVersion == "Parse error", "Malformed HTML did not produce its diagnostic.");
    if (mode == "fault") Require(row.StatusMessage == DeferredResult<string>.FailureMessage && row.LatestVersion == "—", "The transport error was not shown safely.");
}

async Task GngLocal(string mode)
{
    using var files = new SyntheticFiles();
    if (mode == "pending") files.Sector();
    if (mode == "filename") File.WriteAllText(Path.Combine(files.Root, "ESAA-Sweden_invalid.sct"), "Synthetic fixture; never loaded.");
    var row = new CheckResult { AppName = "EuroScope (GNG Pack)", HasFontsCheck = true, Status = CheckStatus.Checking };
    using var observer = new OwnerObserver(row);
    int requests = 0, guards = 0;
    string path = mode switch { "unconfigured" => "", "missing" => Path.Combine(files.Root, "missing"), _ => files.Root };
    await UpdateChecker.CheckGng(row, path, () =>
    {
        requests++;
        throw new InvalidOperationException("An invalid local installation must not request remote metadata.");
    }, _ =>
    {
        guards++;
        if (mode == "pending") throw new InvalidOperationException("Synthetic pending update requires recovery.");
    });
    observer.AssertNotified();
    Require(requests == 0, "The check requested remote metadata before accepting the local installation.");
    Require(guards == (mode is "unconfigured" or "missing" ? 0 : 1), "The pending-update guard was skipped or run too early.");
    Require(row.Status == (mode == "unconfigured" ? CheckStatus.NotConfigured : CheckStatus.Error), "Local validation left the row Checking.");
    if (mode == "pending") Require(row.StatusMessage == "Synthetic pending update requires recovery.", "The recovery guard explanation was lost.");
}

Task GngUnknownLatest(string latest)
{
    using var files = new SyntheticFiles();
    files.Sector();
    var row = new CheckResult { AppName = "EuroScope (GNG Pack)", HasFontsCheck = true,
        Status = CheckStatus.UpToDate, LatestVersion = latest, GngIsTesting = true };
    using var observer = new OwnerObserver(row);
    UpdateChecker.RefreshGngLocal(row, files.Root, _ => { }, _ => false);
    observer.AssertNotified();
    Require(row.Status == CheckStatus.Installed, "Local presence was incorrectly presented as up to date.");
    Require(row.StatusMessage == "Installed; check for updates.", "The unknown-latest action was lost.");
    Require(row.LatestVersion == latest && row.InstalledVersion == "2605/01  rev.1", "Local refresh replaced public metadata or failed to inspect local files.");
    Require(!row.GngIsTesting, "A previous testing marker survived a normal installation.");
    return Task.CompletedTask;
}

Task GngRefreshAfterRestore()
{
    using var files = new SyntheticFiles();
    files.Sector();
    string newer = Path.Combine(files.Root, "ESAA-Sweden_20260601000000-260601-0002.sct");
    File.WriteAllText(newer, "Synthetic newer generation; never loaded.");
    var row = new CheckResult { AppName = "EuroScope (GNG Pack)", HasFontsCheck = true,
        Status = CheckStatus.Checking, LatestVersion = "2606/01 rev.2" };
    using var observer = new OwnerObserver(row);
    var order = new List<string>();
    UpdateChecker.RefreshGngLocal(row, files.Root, root =>
    {
        Require(root == files.Root, "Unexpected data root."); order.Add("guard");
    }, root => { Require(root == files.Root, "Unexpected receipt root."); order.Add("receipt"); return true; });
    Require(order.SequenceEqual(new[] { "guard", "receipt" }), "Receipt inspection preceded the recovery guard.");
    Require(row.Status == CheckStatus.UpToDate && row.GngIsTesting && row.InstalledVersion == "2606/01  rev.2", "The current local generation was not reflected synchronously.");
    File.Delete(newer);
    UpdateChecker.RefreshGngLocal(row, files.Root, _ => { }, _ => false);
    Require(row.Status == CheckStatus.UpdateAvailable && !row.GngIsTesting, "A restore retained the previous installed-version/status marker.");
    Require(row.InstalledVersion == "2605/01  rev.1" && row.LatestVersion == "2606/01 rev.2", "Restore did not reread local files while retaining the known latest generation.");
    observer.AssertNotified();
    return Task.CompletedTask;
}

Task GngRefreshPending()
{
    using var files = new SyntheticFiles();
    files.Sector();
    var row = new CheckResult { AppName = "EuroScope (GNG Pack)", HasFontsCheck = true,
        Status = CheckStatus.UpToDate, LatestVersion = "2605/01 rev.1", GngIsTesting = true };
    bool receiptRead = false;
    UpdateChecker.RefreshGngLocal(row, files.Root,
        _ => throw new InvalidOperationException("Synthetic pending recovery."), _ => { receiptRead = true; return true; });
    Require(!receiptRead && !row.GngIsTesting && row.ObservedLocalVersion is null, "Pending recovery was treated as an installed package.");
    Require(row.Status == CheckStatus.Error && row.StatusMessage == "Synthetic pending recovery.", "Recovery no longer blocks local refresh.");
    Require(row.LatestVersion == "2605/01 rev.1", "Local recovery failure erased unrelated public metadata.");
    return Task.CompletedTask;
}

async Task GngRemoteTesting()
{
    using var files = new SyntheticFiles();
    files.Sector();
    var row = new CheckResult { AppName = "EuroScope (GNG Pack)", HasFontsCheck = true, Status = CheckStatus.Checking };
    await UpdateChecker.CheckGng(row, files.Root, () => Task.FromResult(PackagePage("2610 / 01", 3)), _ => { }, _ => true);
    Require(row.GngIsTesting && row.Status == CheckStatus.UpdateAvailable, "Testing provenance changed the actual public comparison.");
    await UpdateChecker.CheckGng(row, "", () => throw new InvalidOperationException("No request is expected."), _ => { }, _ => true);
    Require(!row.GngIsTesting && row.Status == CheckStatus.NotConfigured, "An unset root retained old testing provenance.");
}

async Task GitHubRemote(string mode)
{
    // Read only this synthetic harness's own PE version; never an installed ATC client.
    string executable = typeof(OwnerContext).Assembly.Location;
    var row = new CheckResult { AppName = "Sweden FIR Launchpad", HasSelfUpdate = true, Status = CheckStatus.Checking };
    using var observer = new OwnerObserver(row);
    var remote = new DeferredResult<string?>();
    var checking = UpdateChecker.CheckGitHub(row, executable, "synthetic/fixture", remote.Fetch);
    Require(remote.Calls == 1 && !checking.IsCompleted, "The GitHub check must await an incomplete remote result.");
    Require(row.InstalledVersion == "v1.0.0", "The harness PE version was not read.");
    if (mode == "fault") await remote.FailFromWorker();
    else await remote.CompleteFromWorker(mode switch { "missing" => null, "current" => "v1.0.0", _ => "v2.0.0" });
    await checking;
    observer.AssertNotified();
    Require(row.Status == (mode == "newer" ? CheckStatus.UpdateAvailable : mode == "current" ? CheckStatus.UpToDate : CheckStatus.Error),
        "GitHub did not reach the expected final status.");
    if (mode == "newer") Require(row.LatestVersion == "v2.0.0" && row.DownloadUrl == "https://github.com/synthetic/fixture/releases/latest", "Latest GitHub metadata was lost.");
    if (mode == "missing") Require(row.LatestVersion == "Cannot fetch", "Absent release did not produce its diagnostic.");
    if (mode == "fault") Require(row.StatusMessage == DeferredResult<string?>.FailureMessage, "The transport error was not shown.");
}

static string PackagePage(string airac, int revision) =>
    $"<table><tr><td>ES</td><td>ESAA Update_Only</td><td>{airac}</td><td>{revision}</td></tr></table>";
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

sealed class OwnerObserver : IDisposable
{
    private readonly CheckResult _row;
    private readonly int _owner = Environment.CurrentManagedThreadId;
    private int _notifications, _summaryNotifications;
    public OwnerObserver(CheckResult row) { _row = row; row.PropertyChanged += Changed; }
    private void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (Environment.CurrentManagedThreadId != _owner)
            throw new InvalidOperationException($"PropertyChanged({args.PropertyName}) ran off the owner thread.");
        _notifications++;
        if (args.PropertyName == nameof(CheckResult.RowStatusKind)) _summaryNotifications++;
    }
    public void AssertNotified()
    {
        if (_notifications == 0 || _summaryNotifications == 0) throw new InvalidOperationException("The real row and summary notification path was not exercised.");
    }
    public void Dispose() => _row.PropertyChanged -= Changed;
}

sealed class DeferredResult<T>
{
    public const string FailureMessage = "Synthetic remote lookup failed.";
    private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _owner = Environment.CurrentManagedThreadId;
    public int Calls { get; private set; }
    public Task<T> Fetch() { Calls++; return _completion.Task; }
    public Task CompleteFromWorker(T value) => Task.Run(() => { CheckWorker(); _completion.SetResult(value); });
    public Task FailFromWorker() => Task.Run(() => { CheckWorker(); _completion.SetException(new HttpRequestException(FailureMessage)); });
    private void CheckWorker()
    {
        if (Environment.CurrentManagedThreadId == _owner) throw new InvalidOperationException("The remote result must complete on a different thread.");
    }
}

sealed class OwnerContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _callbacks = new();
    public override void Post(SendOrPostCallback callback, object? state) => _callbacks.Add((callback, state));
    public static void Run(Func<Task> test)
    {
        var previous = Current;
        var context = new OwnerContext();
        SetSynchronizationContext(context);
        try
        {
            var task = test();
            var elapsed = Stopwatch.StartNew();
            while (!task.IsCompleted)
            {
                if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("The synthetic update check did not finish within five seconds.");
                if (context._callbacks.TryTake(out var item, 25)) item.Callback(item.State);
            }
            task.GetAwaiter().GetResult();
        }
        finally { SetSynchronizationContext(previous); }
    }
}

sealed class SyntheticFiles : IDisposable
{
    private static readonly string Parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Launchpad.UpdateChecker.Tests"));
    public string Root { get; } = Path.Combine(Parent, Guid.NewGuid().ToString("N"));
    public SyntheticFiles() => Directory.CreateDirectory(Root);
    public void Sector() => File.WriteAllText(Path.Combine(Root, "ESAA-Sweden_20260501000000-260501-0001.sct"), "Synthetic fixture; never loaded.");
    public void Dispose()
    {
        string target = Path.GetFullPath(Root);
        if (!string.Equals(Path.GetDirectoryName(target), Parent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to remove files outside the synthetic fixture directory.");
        Directory.Delete(target, recursive: true);
    }
}
