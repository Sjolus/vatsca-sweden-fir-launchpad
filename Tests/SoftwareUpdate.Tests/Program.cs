using System.Net;
using System.Security.Cryptography;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

if (args.Length != 0)
{
    if (args.Length == 2 && args[0] == "--verify-vatis-package")
    {
        await InstallerTests.VerifyDownloadedVatisAsync(args[1]);
        return 0;
    }
    Console.Error.WriteLine("Usage: no arguments for offline tests, or --verify-vatis-package <official-beta.19-nupkg>");
    return 2;
}

// All installation, network and process behavior is synthetic. No installers are ever executed.
var tests = new (string Name, Func<Task> Body)[]
{
    ("constructing and checking never installs", async () =>
    {
        using var f = new Fixture();
        Equal(0, f.Source.Calls); Equal(0, f.Installer.Installs);
        Equal(SoftwareUpdatePhase.Idle, f.Service.GetState(SoftwareApp.TrackAudio).Phase);
        Equal(SoftwareUpdatePhase.Available, (await f.Check()).Phase);
        Equal(0, f.Handler.Calls); Equal(0, f.Installer.Installs); Equal(0, f.Installer.Backups);
    }),
    ("unsupported installation does not contact catalog", async () =>
    {
        using var f = new Fixture(); f.Installer.Current = f.Installer.Current with { CanUpdate = false, Reason = "Portable copy" };
        Equal(SoftwareUpdatePhase.Unavailable, (await f.Check()).Phase); Equal(0, f.Source.Calls);
    }),
    ("unknown installed version fails closed", async () =>
    {
        using var f = new Fixture(); f.Installer.Current = f.Installer.Current with { Version = "unknown" };
        Equal(SoftwareUpdatePhase.Error, (await f.Check()).Phase); Equal(0, f.Source.Calls);
    }),
    ("missing compatible release is unavailable", async () =>
    {
        using var f = new Fixture(); f.Source.Release = null;
        Equal(SoftwareUpdatePhase.Unavailable, (await f.Check()).Phase);
    }),
    ("downgrade and equal version cannot install", async () =>
    {
        foreach (var version in new[] { "1.4.0", "1.5.0" })
        {
            using var f = new Fixture(); f.Installer.Current = f.Installer.Current with { Version = version };
            Equal(SoftwareUpdatePhase.Current, (await f.Check()).Phase);
            Equal(SoftwareUpdatePhase.Error, (await f.Update()).Phase);
            Equal(0, f.Installer.Installs); Equal(0, f.Handler.Calls);
        }
    }),
    ("install requires an explicit successful check", async () =>
    {
        using var f = new Fixture(); Equal(SoftwareUpdatePhase.Error, (await f.Update()).Phase);
        Equal(0, f.Source.Calls); Equal(0, f.Installer.Installs);
    }),
    ("verified update backs up then installs and remains closed", async () =>
    {
        using var f = new Fixture(); await f.Check();
        var result = await f.Update();
        Equal(SoftwareUpdatePhase.Completed, result.Phase); Equal("1.4.0", result.InstalledVersion);
        Equal(3, f.Source.Calls); Equal(1, f.Installer.Installs); Equal(1, f.Installer.Backups);
        True(File.Exists(Path.Combine(result.BackupFolder!, "synthetic-settings.txt")));
        True(f.States.Any(s => s.Phase == SoftwareUpdatePhase.Downloading && s.ProgressPercent == 100));
        True(f.States.Any(s => s.Phase == SoftwareUpdatePhase.Installing && !s.CanCancel && s.ProgressPercent == null));
        True(!f.Service.IsBusy && !f.Service.CanCancel && !result.CanUpdate);
        Equal(0, Directory.GetFileSystemEntries(f.Cache).Length);
    }),
    ("all product flows preserve product identity", async () =>
    {
        foreach (var app in Enum.GetValues<SoftwareApp>())
        {
            using var f = new Fixture(app); await f.Check();
            Equal(SoftwareUpdatePhase.Completed, (await f.Update()).Phase); Equal(1, f.Installer.Installs);
        }
    }),
    ("native restart remains visible after apply failure and prevents another download", async () =>
    {
        using var f = new Fixture(); await f.Check();
        f.Installer.OnInstall = () => { f.Installer.RestartRequired = true; throw new IOException("Synthetic native exit 3010"); };
        var failed = await f.Update();
        Equal(SoftwareUpdatePhase.Error, failed.Phase);
        True(f.Service.RestartRequired && failed.Message.Contains("Restart Windows") && Directory.Exists(failed.BackupFolder));
        Equal(1, f.Installer.Installs); var downloads = f.Handler.Calls;
        await f.Check();
        Equal(SoftwareUpdatePhase.Error, (await f.Update()).Phase);
        Equal(downloads, f.Handler.Calls); Equal(1, f.Installer.Installs);
    }),
    ("running tool blocks before download", async () =>
    {
        using var f = new Fixture(); await f.Check(); f.Installer.Running = true;
        Equal(SoftwareUpdatePhase.Error, (await f.Update()).Phase); Equal(0, f.Handler.Calls);
    }),
    ("installation changed after check blocks before download", async () =>
    {
        using var f = new Fixture(); await f.Check();
        f.Installer.Current = f.Installer.Current with { Version = "1.3.1" };
        Equal(SoftwareUpdatePhase.Error, (await f.Update()).Phase); Equal(0, f.Handler.Calls);
    }),
    ("starting tool during backup prevents installer", async () =>
    {
        using var f = new Fixture(); await f.Check();
        f.Installer.OnBackup = _ => { f.Installer.Running = true; return Task.CompletedTask; };
        var result = await f.Update(); Equal(SoftwareUpdatePhase.Error, result.Phase); Equal(0, f.Installer.Installs);
        True(Directory.Exists(result.BackupFolder));
    }),
    ("release mutation after download prevents backup and install", async () =>
    {
        using var f = new Fixture(); await f.Check();
        f.Source.Release = f.Source.Release! with { Sha256 = new string('0', 64) };
        Equal(SoftwareUpdatePhase.Error, (await f.Update()).Phase);
        Equal(0, f.Installer.Backups); Equal(0, f.Installer.Installs);
    }),
    ("release mutation during backup prevents installer", async () =>
    {
        using var f = new Fixture(); await f.Check();
        f.Installer.OnBackup = _ => { f.Source.Release = null; return Task.CompletedTask; };
        var result = await f.Update(); Equal(SoftwareUpdatePhase.Error, result.Phase);
        Equal(0, f.Installer.Installs); True(Directory.Exists(result.BackupFolder));
    }),
    ("wrong package digest never reaches installer verification", async () =>
    {
        using var f = new Fixture(); f.Source.Release = f.Source.Release! with { Sha256 = new string('0', 64) };
        await f.Check(); Equal(SoftwareUpdatePhase.Error, (await f.Update()).Phase);
        Equal(0, f.Installer.Verifications); Equal(0, f.Installer.Installs);
    }),
    ("untrusted vendor package blocks backup and installation", async () =>
    {
        using var f = new Fixture(); f.Installer.OnVerify = _ => throw new InvalidDataException("Synthetic signature rejected");
        await f.Check(); Equal(SoftwareUpdatePhase.Error, (await f.Update()).Phase);
        Equal(0, f.Installer.Backups); Equal(0, f.Installer.Installs);
    }),
    ("server length mismatch is rejected", async () =>
    {
        using var f = new Fixture(); f.Handler.Respond = (_, _) => Task.FromResult(Response(f.Payload, f.Payload.Length + 1));
        await f.Check(); Equal(SoftwareUpdatePhase.Error, (await f.Update()).Phase); Equal(0, f.Installer.Verifications);
    }),
    ("oversized and truncated response bodies are rejected", async () =>
    {
        foreach (var offset in new[] { -1, 1 })
        {
            using var f = new Fixture(); f.Handler.Respond = (_, _) => Task.FromResult(Response(new byte[f.Payload.Length + offset], f.Payload.Length));
            await f.Check(); Equal(SoftwareUpdatePhase.Error, (await f.Update()).Phase); Equal(0, f.Installer.Verifications);
        }
    }),
    ("malformed release metadata and unsafe filenames are rejected", async () =>
    {
        using var f = new Fixture(); var release = f.Source.Release!;
        foreach (var bad in new[] { release with { FileName = "../evil.exe" }, release with { Size = SoftwareUpdateService.MaximumPackageBytes + 1 },
            release with { Size = 0 }, release with { Sha256 = "bad" }, release with { App = SoftwareApp.Vacs },
            release with { DownloadUri = new Uri("http://github.com/file.exe") }, release with { DownloadUri = new Uri("https://evil.invalid/file.exe") } })
        {
            f.Source.Release = bad; Equal(SoftwareUpdatePhase.Error, (await f.Check()).Phase);
        }
        Equal(0, f.Handler.Calls);
    }),
    ("unsafe redirect is rejected before contacting destination", async () =>
    {
        using var f = new Fixture(); f.Handler.Respond = (_, _) => Task.FromResult(Redirect("https://evil.invalid/package.exe"));
        await f.Check(); Equal(SoftwareUpdatePhase.Error, (await f.Update()).Phase); Equal(1, f.Handler.Calls);
    }),
    ("allowed GitHub CDN redirect is followed", async () =>
    {
        using var f = new Fixture(); f.Handler.Respond = (request, _) => Task.FromResult(request.RequestUri!.Host == "github.com"
            ? Redirect("https://release-assets.githubusercontent.com/synthetic/package.exe?token=fake") : Response(f.Payload));
        await f.Check(); Equal(SoftwareUpdatePhase.Completed, (await f.Update()).Phase); Equal(2, f.Handler.Calls);
    }),
    ("redirect loops are bounded", async () =>
    {
        using var f = new Fixture(); f.Handler.Respond = (_, _) => Task.FromResult(Redirect("https://release-assets.githubusercontent.com/loop"));
        await f.Check(); Equal(SoftwareUpdatePhase.Error, (await f.Update()).Phase); Equal(6, f.Handler.Calls);
    }),
    ("cancel during download cleans partial cache and requires fresh check", async () =>
    {
        using var f = new Fixture(); await f.Check();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Handler.Respond = async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return Response(f.Payload); };
        var updating = f.Update(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        True(f.Service.CanCancel); f.Service.Cancel(f.App);
        Equal(SoftwareUpdatePhase.Cancelled, (await updating).Phase); Equal(0, f.Installer.Installs);
        Equal(0, Directory.GetFileSystemEntries(f.Cache).Length);
        Equal(SoftwareUpdatePhase.Error, (await f.Update()).Phase);
    }),
    ("cancel checking and cross-product gate prevent queued installs", async () =>
    {
        using var f = new Fixture(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Source.OnGet = async token => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); };
        var checking = f.Check(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Throws<InvalidOperationException>(() => f.Service.CheckAsync(SoftwareApp.Vacs, f.Installer.Current.ExePath));
        f.Service.Cancel(SoftwareApp.Vacs); True(!checking.IsCompleted);
        f.Service.Cancel(f.App); Equal(SoftwareUpdatePhase.Cancelled, (await checking).Phase);
        True(!f.Service.IsBusy); Equal(0, f.Installer.Installs);
    }),
    ("download deadline cancels stalled response body without applying", async () =>
    {
        using var f = new Fixture(downloadTimeout: TimeSpan.FromMilliseconds(100));
        var stream = new StalledBodyStream();
        f.Handler.Respond = (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
            response.Content.Headers.ContentLength = f.Payload.Length;
            return Task.FromResult(response);
        };
        await f.Check(); var result = await f.Update().WaitAsync(TimeSpan.FromSeconds(5));
        Equal(SoftwareUpdatePhase.Error, result.Phase); True(result.Message.Contains("timed out"));
        True(stream.CancellationObserved); Equal(0, f.Installer.Installs); Equal(0, f.Installer.Verifications);
        Equal(0, Directory.GetFileSystemEntries(f.Cache).Length); True(!f.Service.IsBusy);
    }),
    ("cancel during backup prevents installation and leaves backup", async () =>
    {
        using var f = new Fixture(); await f.Check();
        f.Installer.OnBackup = token => { f.Service.Cancel(f.App); token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        Equal(SoftwareUpdatePhase.Cancelled, (await f.Update()).Phase); Equal(0, f.Installer.Installs);
        True(Directory.Exists(f.Installer.BackupPath));
    }),
    ("installer is not cancellable and gate stays held until it exits", async () =>
    {
        using var f = new Fixture(); await f.Check();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Installer.OnInstall = async () => { entered.SetResult(); await finish.Task; };
        using var cancellation = new CancellationTokenSource();
        var updating = f.Service.UpdateAsync(f.App, cancellation.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        True(f.Service.IsBusy && !f.Service.CanCancel); f.Service.Cancel(f.App); cancellation.Cancel();
        True(!updating.IsCompleted); await Throws<InvalidOperationException>(() => f.Service.CheckAsync(SoftwareApp.Vatis, f.Installer.Current.ExePath));
        finish.SetResult(); Equal(SoftwareUpdatePhase.Completed, (await updating).Phase); True(!f.Service.IsBusy);
    }),
    ("installer failure retains settings backup and invalidates retry", async () =>
    {
        using var f = new Fixture(); await f.Check(); f.Installer.OnInstall = () => throw new IOException("Synthetic installer failure");
        var result = await f.Update(); Equal(SoftwareUpdatePhase.Error, result.Phase);
        True(Directory.Exists(result.BackupFolder)); True(result.Message.Contains(result.BackupFolder!));
        Equal(SoftwareUpdatePhase.Error, (await f.Update()).Phase); Equal(1, f.Installer.Installs);
    }),
    ("incorrect installed version cannot report success", async () =>
    {
        using var f = new Fixture(); await f.Check(); f.Installer.AdoptVersion = false;
        Equal(SoftwareUpdatePhase.Error, (await f.Update()).Phase);
    }),
    ("unexpected tool launch cannot report safe completion", async () =>
    {
        using var f = new Fixture(); await f.Check(); f.Installer.OnInstall = () => { f.Installer.Running = true; return Task.CompletedTask; };
        var result = await f.Update(); Equal(SoftwareUpdatePhase.Error, result.Phase); True(f.Installer.Running);
    }),
    ("display observer exceptions do not interrupt verified update", async () =>
    {
        using var f = new Fixture(); f.Service.StateChanged += _ => throw new Exception("Synthetic display fault");
        await f.Check(); Equal(SoftwareUpdatePhase.Completed, (await f.Update()).Phase);
    }),
    ("verified package bytes cannot be changed during install", async () =>
    {
        using var f = new Fixture(); await f.Check();
        f.Installer.OnPackageInstall = path =>
        {
            try { using var writer = new FileStream(path, FileMode.Open, FileAccess.Write); }
            catch (IOException) { return; }
            throw new Exception("Package unexpectedly writable");
        };
        Equal(SoftwareUpdatePhase.Completed, (await f.Update()).Phase);
    })
};

var failures = 0;
foreach (var (name, body) in tests)
{
    try { await body(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex); }
}
Console.WriteLine($"Coordinator: {tests.Length - failures}/{tests.Length} passed.");
try { await SourceTests.RunAsync(); } catch (Exception ex) { failures++; Console.WriteLine("FAIL source tests: " + ex); }
try { await InstallerTests.RunAsync(); } catch (Exception ex) { failures++; Console.WriteLine("FAIL installer tests: " + ex); }
Console.WriteLine($"Combined regression result: {(failures == 0 ? "PASS" : "FAIL")}");
return failures == 0 ? 0 : 1;

static void True(bool value) { if (!value) throw new Exception("Assertion failed"); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
static HttpResponseMessage Response(byte[] bytes, long? length = null)
{
    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    if (length.HasValue) response.Content.Headers.ContentLength = length;
    return response;
}
static HttpResponseMessage Redirect(string uri)
{
    var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new Uri(uri); return response;
}

sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "LaunchpadSoftwareSynthetic-" + Guid.NewGuid().ToString("N"));
    public string Cache => Path.Combine(Root, "cache");
    public SoftwareApp App { get; }
    public byte[] Payload { get; } = Enumerable.Range(0, 200000).Select(x => (byte)(x % 251)).ToArray();
    public FakeSource Source { get; }
    public FakeInstaller Installer { get; }
    public FakeHandler Handler { get; }
    public SoftwareUpdateService Service { get; }
    public List<SoftwareUpdateState> States { get; } = [];
    private readonly HttpClient _http;
    public Fixture(SoftwareApp app = SoftwareApp.TrackAudio, TimeSpan? downloadTimeout = null)
    {
        App = app; Directory.CreateDirectory(Root);
        var version = "1.4.0";
        var fileName = app switch { SoftwareApp.Vacs => $"vacs_{version}_x64-setup.exe", SoftwareApp.Vatis => $"org.vatsim.vatis-{version}-full.nupkg", _ => $"trackaudio-{version}-x64-setup.exe" };
        var url = app switch { SoftwareApp.Vacs => $"https://github.com/vacs-project/vacs/releases/download/vacs-client-v{version}/{fileName}", SoftwareApp.Vatis => $"https://vatis.app/updates/windows/{fileName}", _ => $"https://github.com/pierr3/TrackAudio/releases/download/{version}/{fileName}" };
        Source = new FakeSource(new(app, version, new Uri(url), fileName, Convert.ToHexString(SHA256.HashData(Payload)), Payload.Length));
        Installer = new FakeInstaller(new(app, Path.Combine(Root, "fake-tool.exe"), "1.3.0", Root, "user", null, true, null), Path.Combine(Root, "backup"));
        Handler = new FakeHandler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) }) };
        _http = new HttpClient(Handler);
        Service = new SoftwareUpdateService(Source, Installer, _http, Cache, downloadTimeout ?? TimeSpan.FromMinutes(20));
        Service.StateChanged += state => States.Add(state);
    }
    public Task<SoftwareUpdateState> Check() => Service.CheckAsync(App, Installer.Current.ExePath);
    public Task<SoftwareUpdateState> Update() => Service.UpdateAsync(App);
    public void Dispose()
    {
        _http.Dispose();
        // Root is an absolute GUID directory created by this fixture, never an installed data path.
        if (!Path.GetFullPath(Root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            throw new Exception("Unsafe synthetic cleanup path");
        Directory.Delete(Root, recursive: true);
    }
}

sealed class FakeSource(SoftwareRelease release) : ISoftwareReleaseSource
{
    public SoftwareRelease? Release = release;
    public int Calls;
    public Func<CancellationToken, Task>? OnGet;
    public async Task<SoftwareRelease?> GetLatestAsync(SoftwareApp app, string installedVersion, CancellationToken token)
    { Calls++; if (OnGet != null) await OnGet(token); return Release; }
}

sealed class FakeInstaller(SoftwareInstallation installation, string backupPath) : ISoftwareInstaller
{
    public bool RestartRequired { get; set; }
    public SoftwareInstallation Current = installation;
    public string BackupPath => backupPath;
    public bool Running, AdoptVersion = true;
    public int Installs, Backups, Verifications;
    public Func<CancellationToken, Task>? OnVerify, OnBackup;
    public Func<Task>? OnInstall;
    public Action<string>? OnPackageInstall;
    public SoftwareInstallation Inspect(SoftwareApp app, string path) => Current;
    public bool IsRunning(SoftwareInstallation current) => Running;
    public async Task VerifyPackageAsync(SoftwareRelease release, string packagePath, CancellationToken token)
    { Verifications++; if (OnVerify != null) await OnVerify(token); }
    public async Task<string?> BackupAsync(SoftwareInstallation current, IProgress<string>? progress, CancellationToken token)
    {
        Backups++; Directory.CreateDirectory(backupPath); File.WriteAllText(Path.Combine(backupPath, "synthetic-settings.txt"), "fake-settings-only");
        if (OnBackup != null) await OnBackup(token); return backupPath;
    }
    public async Task<SoftwareInstallResult> InstallAsync(SoftwareInstallation current, SoftwareRelease release, string packagePath, IProgress<string>? progress)
    {
        Installs++; OnPackageInstall?.Invoke(packagePath); if (OnInstall != null) await OnInstall();
        if (AdoptVersion) Current = Current with { Version = release.Version };
        return new(Current.ExePath);
    }
}

sealed class FakeHandler : HttpMessageHandler
{
    public int Calls;
    public required Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    { Calls++; var response = await Respond(request, token); response.RequestMessage = request; return response; }
}

sealed class StalledBodyStream : Stream
{
    public bool CancellationObserved;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        catch (OperationCanceledException) { CancellationObserved = cancellationToken.IsCancellationRequested; throw; }
    }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
