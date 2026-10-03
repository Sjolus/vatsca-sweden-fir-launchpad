using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

// The real SDK checks and downloads synthetic packages into temporary installations. Process starts
// and exit are rejected by the locator. Apply/Restart is deliberately never invoked by this harness.
var tests = new (string Name, Func<Fixture, Task> Run)[]
{
    ("uninstalled build never accesses the feed", async f =>
    {
        var service = f.Service(installed: false);
        Equal(LaunchpadUpdateStatus.Unsupported, (await service.CheckAsync()).Status);
        Equal(LaunchpadUpdateStatus.Unsupported, (await service.DownloadAsync()).Status);
        Equal(0, f.Source.Checks);
    }),
    ("portable build never accesses the feed", async f =>
    {
        var service = f.Service(portable: true);
        True(!service.IsInstalled);
        Equal(LaunchpadUpdateStatus.Unsupported, (await service.CheckAsync()).Status);
        Equal(0, f.Source.Checks);
    }),
    ("missing installer feed is not up to date", async f =>
    {
        f.Feed();
        Equal(LaunchpadUpdateStatus.NoFeed, (await f.Service().CheckAsync()).Status);
    }),
    ("GitHub releases containing only legacy assets produce NoFeed", async f =>
    {
        var downloader = new LegacyGithubDownloader();
        var source = new GithubSource(LaunchpadUpdateService.RepositoryUrl, null, false, downloader);
        var service = f.Service(source: source);
        Equal(LaunchpadUpdateStatus.NoFeed, (await service.CheckAsync()).Status);
        True(!downloader.SentAuthorization);
        Equal(1, downloader.Requests);
    }),
    ("valid current-version feed is up to date", async f =>
    {
        f.Feed(f.Package("1.0.0"));
        Equal(LaunchpadUpdateStatus.UpToDate, (await f.Service().CheckAsync()).Status);
    }),
    ("older feed never downgrades", async f =>
    {
        f.Feed(f.Package("0.9.0"));
        Equal(LaunchpadUpdateStatus.UpToDate, (await f.Service().CheckAsync()).Status);
        Equal(0, f.Source.Downloads);
    }),
    ("prerelease packages are excluded from stable channel", async f =>
    {
        f.Feed(f.Package("2.0.0-beta.1"));
        Equal(LaunchpadUpdateStatus.NoFeed, (await f.Service().CheckAsync()).Status);
    }),
    ("other package IDs cannot become updates", async f =>
    {
        f.Feed(f.Package("2.0.0") with { PackageId = "OtherApp" });
        Equal(LaunchpadUpdateStatus.NoFeed, (await f.Service().CheckAsync()).Status);
    }),
    ("download verifies and stages without starting a process", async f =>
    {
        var asset = f.Package("1.1.0");
        f.Feed(asset);
        var service = f.Service();
        var states = new List<LaunchpadUpdateState>();
        service.StateChanged += (_, state) => states.Add(state);
        Equal(LaunchpadUpdateStatus.Available, (await service.CheckAsync()).Status);
        Equal(LaunchpadUpdateStatus.Ready, (await service.DownloadAsync()).Status);
        Equal("1.1.0", service.State.AvailableVersion);
        True(states.Any(s => s.Status == LaunchpadUpdateStatus.Downloading && s.ProgressPercent == 100));
        Equal(asset.SHA256, Hash(Path.Combine(f.Packages, asset.FileName)));
        Equal(1, f.Source.Downloads);
        Equal(LaunchpadUpdateService.Channel, f.Source.LastChannel);
    }),
    ("verified pending download is ready after restart while offline", async f =>
    {
        f.Feed(f.Package("1.1.0"));
        var first = f.Service();
        await first.CheckAsync();
        await first.DownloadAsync();
        f.Source.CheckError = new HttpRequestException("synthetic offline");
        var checks = f.Source.Checks;
        Equal(LaunchpadUpdateStatus.Ready, (await f.Service().CheckAsync()).Status);
        Equal(checks, f.Source.Checks);
    }),
    ("SDK cached same-target package is validated and recognized", async f =>
    {
        var asset = f.Package("1.1.0");
        f.Feed(asset);
        File.Copy(Path.Combine(f.FeedDirectory, asset.FileName), Path.Combine(f.Packages, asset.FileName));
        Equal(LaunchpadUpdateStatus.Ready, (await f.Service().CheckAsync()).Status);
        Equal(0, f.Source.Downloads);
    }),
    ("corrupt cached package is redownloaded instead of trusted", async f =>
    {
        var asset = f.Package("1.1.0");
        f.Feed(asset);
        File.WriteAllText(Path.Combine(f.Packages, asset.FileName), "corrupt cached download");
        var service = f.Service();
        Equal(LaunchpadUpdateStatus.Available, (await service.CheckAsync()).Status);
        Equal(LaunchpadUpdateStatus.Ready, (await service.DownloadAsync()).Status);
        Equal(asset.SHA256, Hash(Path.Combine(f.Packages, asset.FileName)));
    }),
    ("modified receipt-backed package is never ready", async f =>
    {
        var asset = f.Package("1.1.0");
        f.Feed(asset);
        var service = f.Service();
        await service.CheckAsync();
        await service.DownloadAsync();
        File.AppendAllText(Path.Combine(f.Packages, asset.FileName), "changed");
        Equal(LaunchpadUpdateStatus.Available, (await f.Service().CheckAsync()).Status);
    }),
    ("bad remote checksum blocks readiness", async f =>
    {
        f.Feed(f.Package("1.1.0") with { SHA256 = new string('0', 64) });
        var service = f.Service();
        await service.CheckAsync();
        var state = await service.DownloadAsync();
        Equal(LaunchpadUpdateStatus.Error, state.Status);
        True(state.Message.Contains("verified"));
        True(!File.Exists(Path.Combine(f.Packages, "launchpad-ready.json")));
    }),
    ("package payload identity must agree with feed", async f =>
    {
        f.Feed(f.Package("1.1.0", nuspecId: "OtherApp"));
        var service = f.Service();
        await service.CheckAsync();
        Equal(LaunchpadUpdateStatus.Error, (await service.DownloadAsync()).Status);
    }),
    ("missing SHA256 metadata fails closed", async f =>
    {
        f.Feed(f.Package("1.1.0") with { SHA256 = "" });
        Equal(LaunchpadUpdateStatus.Error, (await f.Service().CheckAsync()).Status);
    }),
    ("feed path traversal fails before download", async f =>
    {
        f.Feed(f.Package("1.1.0") with { FileName = "../escape-full.nupkg" });
        Equal(LaunchpadUpdateStatus.Error, (await f.Service().CheckAsync()).Status);
        Equal(0, f.Source.Downloads);
    }),
    ("network errors are clean and retryable", async f =>
    {
        f.Source.CheckError = new HttpRequestException("synthetic secret diagnostic");
        var service = f.Service();
        var state = await service.CheckAsync();
        Equal(LaunchpadUpdateStatus.Error, state.Status);
        True(!state.Message.Contains("secret"));
        f.Source.CheckError = null;
        f.Feed(f.Package("1.1.0"));
        Equal(LaunchpadUpdateStatus.Available, (await service.CheckAsync()).Status);
    }),
    ("check calls cannot overlap a delayed check", async f =>
    {
        f.Feed(f.Package("1.1.0"));
        f.Source.CheckPause = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = f.Service();
        var check = service.CheckAsync();
        await f.Source.CheckEntered.Task;
        Equal(LaunchpadUpdateStatus.Checking, (await service.CheckAsync()).Status);
        Equal(LaunchpadUpdateStatus.Checking, (await service.DownloadAsync()).Status);
        Equal(1, f.Source.Checks);
        f.Source.CheckPause.SetResult();
        Equal(LaunchpadUpdateStatus.Available, (await check).Status);
    }),
    ("cancelled download leaves retry available and no receipt", async f =>
    {
        f.Feed(f.Package("1.1.0"));
        f.Source.PauseDownload = true;
        var service = f.Service();
        await service.CheckAsync();
        using var cancel = new CancellationTokenSource();
        var download = service.DownloadAsync(cancel.Token);
        await f.Source.DownloadEntered.Task;
        Equal(LaunchpadUpdateStatus.Downloading, (await service.CheckAsync()).Status);
        cancel.Cancel();
        Equal(LaunchpadUpdateStatus.Available, (await download).Status);
        True(!File.Exists(Path.Combine(f.Packages, "launchpad-ready.json")));
        f.Source.PauseDownload = false;
        Equal(LaunchpadUpdateStatus.Ready, (await service.DownloadAsync()).Status);
    }),
    ("check cancellation waits out SDK work and remains retryable", async f =>
    {
        f.Feed(f.Package("1.1.0"));
        var service = f.Service();
        await service.CheckAsync();
        f.Source.CheckPause = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var check = service.CheckAsync(cancel.Token);
        await f.Source.CheckEntered.Task;
        cancel.Cancel();
        Equal(LaunchpadUpdateStatus.Checking, (await service.CheckAsync()).Status);
        f.Source.CheckPause.SetResult();
        Equal(LaunchpadUpdateStatus.Available, (await check).Status);
        Equal(LaunchpadUpdateStatus.Ready, (await service.DownloadAsync()).Status);
    }),
    ("receipt from a now-installed version does not offer restart", async f =>
    {
        f.Feed(f.Package("1.1.0"));
        var service = f.Service();
        await service.CheckAsync();
        await service.DownloadAsync();
        Equal(LaunchpadUpdateStatus.UpToDate, (await f.Service(version: "1.1.0").CheckAsync()).Status);
    }),
    ("malformed receipt safely falls back to feed", async f =>
    {
        f.Feed(f.Package("1.1.0"));
        File.WriteAllText(Path.Combine(f.Packages, "launchpad-ready.json"), "{not JSON");
        Equal(LaunchpadUpdateStatus.Available, (await f.Service().CheckAsync()).Status);
    })
};

var failures = 0;
foreach (var (name, run) in tests)
{
    using var fixture = new Fixture();
    try
    {
        await run(fixture).WaitAsync(TimeSpan.FromSeconds(20));
        Equal(0, fixture.Process.Calls);
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL {name}: {ex.Message}");
    }
}
Console.WriteLine($"{tests.Length - failures} passed; {failures} failed.");
return failures == 0 ? 0 : 1;

static void True(bool value) { if (!value) throw new Exception("Assertion failed."); }
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}.");
}
static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

sealed class Fixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LaunchpadUpdate.Tests", Guid.NewGuid().ToString("N"));
    public string FeedDirectory { get; }
    public string Packages { get; }
    public BlockedProcess Process { get; } = new();
    public ControlledSource Source { get; }

    public Fixture()
    {
        FeedDirectory = Directory.CreateDirectory(Path.Combine(_root, "feed")).FullName;
        Packages = Directory.CreateDirectory(Path.Combine(_root, "installed", "packages")).FullName;
        Source = new ControlledSource(new SimpleFileSource(new DirectoryInfo(FeedDirectory)));
    }

    public LaunchpadUpdateService Service(bool installed = true, bool portable = false, string version = "1.0.0", IUpdateSource? source = null) =>
        new(source ?? Source, new TestLocator(Packages, Process, installed ? version : null, portable));

    public VelopackAsset Package(string version, string? nuspecId = null)
    {
        var filename = $"SwedenFirLaunchpad-{version}-full.nupkg";
        var path = Path.Combine(FeedDirectory, filename);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("SwedenFirLaunchpad.nuspec").Open(), Encoding.UTF8))
                writer.Write($"<package><metadata><id>{nuspecId ?? LaunchpadUpdateService.PackageId}</id><version>{version}</version><authors>Fixture</authors><description>Synthetic update</description><channel>win-x64</channel></metadata></package>");
            using (var writer = new StreamWriter(zip.CreateEntry("lib/net45/test.txt").Open(), Encoding.UTF8))
                writer.Write("Synthetic payload, never executed.");
        }
        var bytes = File.ReadAllBytes(path);
        return new VelopackAsset
        {
            PackageId = LaunchpadUpdateService.PackageId, Version = SemanticVersion.Parse(version), Type = VelopackAssetType.Full,
            FileName = filename, Size = bytes.Length, SHA1 = Convert.ToHexString(SHA1.HashData(bytes)), SHA256 = Convert.ToHexString(SHA256.HashData(bytes))
        };
    }

    public void Feed(params VelopackAsset[] assets) => File.WriteAllText(Path.Combine(FeedDirectory, "releases.win-x64.json"),
        JsonSerializer.Serialize(new { Assets = assets.Select(a => new { a.PackageId, Version = a.Version.ToString(), Type = a.Type.ToString(), a.FileName, a.Size, a.SHA1, a.SHA256 }) }));

    public void Dispose() => Directory.Delete(_root, recursive: true);
}

sealed class ControlledSource(IUpdateSource inner) : IUpdateSource
{
    public int Checks;
    public int Downloads;
    public string? LastChannel;
    public Exception? CheckError;
    public TaskCompletionSource? CheckPause;
    public TaskCompletionSource CheckEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool PauseDownload;
    public TaskCompletionSource DownloadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        Checks++;
        LastChannel = channel;
        CheckEntered.TrySetResult();
        if (CheckPause != null) await CheckPause.Task;
        if (CheckError != null) throw CheckError;
        return await inner.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease);
    }

    public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default)
    {
        Downloads++;
        DownloadEntered.TrySetResult();
        if (PauseDownload)
        {
            await File.WriteAllTextAsync(localFile, "partial", cancelToken);
            await Task.Delay(Timeout.Infinite, cancelToken);
        }
        await inner.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken);
    }
}

sealed class TestLocator(string packages, BlockedProcess process, string? version, bool portable) : VelopackLocator
{
    public override string AppId => LaunchpadUpdateService.PackageId;
    public override string RootAppDir => Path.GetDirectoryName(packages)!;
    public override string PackagesDir => packages;
    public override string AppContentDir => RootAppDir;
    public override string UpdateExePath => Path.Combine(RootAppDir, "Update.exe");
    public override string Channel => LaunchpadUpdateService.Channel;
    public override SemanticVersion? CurrentlyInstalledVersion => version == null ? null : SemanticVersion.Parse(version);
    public override bool IsPortable => portable;
    public override IProcessImpl Process => process;
}

sealed class BlockedProcess : IProcessImpl
{
    public int Calls;
    public string GetCurrentProcessPath() => throw new InvalidOperationException("No test process lookup is needed.");
    public uint GetCurrentProcessId() => throw new InvalidOperationException("No test process lookup is needed.");
    public void StartProcess(string exePath, IEnumerable<string> args, string workDir, bool showWindow)
    {
        Calls++;
        throw new InvalidOperationException("Process start is forbidden in regression tests.");
    }
    public void Exit(int exitCode)
    {
        Calls++;
        throw new InvalidOperationException("Process exit is forbidden in regression tests.");
    }
}

sealed class LegacyGithubDownloader : IFileDownloader
{
    public int Requests;
    public bool SentAuthorization;
    public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
    {
        Requests++;
        SentAuthorization |= headers?.ContainsKey("Authorization") == true;
        if (!url.StartsWith("https://api.github.com/repos/Sjolus/vatsca-sweden-fir-launchpad/releases?", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected test URL.");
        return Task.FromResult("""
            [{"name":"v1.4.0","prerelease":false,"published_at":"2026-09-01T00:00:00Z","assets":[{"name":"VatscaUpdateChecker.exe","browser_download_url":"https://example.invalid/never-download.exe"}]}]
            """);
    }
    public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30) =>
        throw new InvalidOperationException("No installer feed asset exists, so no further requests are allowed.");
    public Task DownloadFile(string url, string targetFile, Action<int> progress, IDictionary<string, string>? headers = null,
        double timeout = 30, CancellationToken cancelToken = default) =>
        throw new InvalidOperationException("Legacy executables must never be downloaded by the installer updater.");
}
