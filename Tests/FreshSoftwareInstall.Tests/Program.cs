using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

var tests = new (string Name, Func<Task> Run)[]
{
    ("preview obtains metadata without downloading or installing", async () =>
    {
        using var f = new Fixture(); var plan = await f.Preview();
        Check(plan.App == SoftwareApp.Vacs && plan.Scope == "AllUsers" && !Directory.Exists(plan.InstallRoot));
        Check(f.SourceCalls == 1 && f.Downloads == 0 && f.Commands.Count == 0 && f.VatisRuns == 0);
    }),
    ("existing configured copies redirect to adoption", async () =>
    {
        using var f = new Fixture(); f.Settings.VacsExePath = f.File("Existing/vacs-client.exe");
        await Reject(() => f.Preview()); Check(f.SourceCalls == 0 && f.Downloads == 0);
    }),
    ("stale configured folders require explicit review before fresh installation", async () =>
    {
        using var f = new Fixture(); f.Settings.VacsExePath = f.PathOf("Existing/missing.exe"); f.Dir("Existing");
        await Reject(() => f.Preview()); Check(f.Downloads == 0);
    }),
    ("registered copy and stale destination-key residue each block fresh install", async () =>
    {
        using var f = new Fixture(); f.Residue = true; await Reject(() => f.Preview());
        f.Residue = false; f.Registrations.Add(new(f.TargetRoot, f.TargetRoot, "AllUsers", "2.8.0", "vacs-client.exe"));
        await Reject(() => f.Preview()); Check(f.Downloads == 0 && f.Commands.Count == 0);
    }),
    ("empty or nonempty old installation directories are never overwritten", async () =>
    {
        foreach (var app in Enum.GetValues<SoftwareApp>())
        {
            using var f = new Fixture(app); Directory.CreateDirectory(f.TargetRoot);
            await Reject(() => f.Preview()); File.WriteAllText(Path.Combine(f.TargetRoot, "old-settings"), "FAKE-OLD");
            await Reject(() => f.Preview()); Check(f.Downloads == 0 && f.Commands.Count == 0 && f.VatisRuns == 0);
        }
    }),
    ("vATIS data-only root is refused even with a backup destination", async () =>
    {
        using var f = new Fixture(SoftwareApp.Vatis); var profile = f.File("Local/org.vatsim.vatis/Profiles/fake.json", "FAKE-PERSONAL");
        await Reject(() => f.Service.PreviewAsync(f.App, f.Settings, f.Recovery, true));
        Check(File.ReadAllText(profile) == "FAKE-PERSONAL" && f.VatisRuns == 0);
    }),
    ("running clients and missing prerequisites do not auto-install anything", async () =>
    {
        using var f = new Fixture(); f.Running = true; await Reject(() => f.Preview());
        f.Running = false; f.Problem = "Install the machine runtime separately."; await Reject(() => f.Preview());
        Check(f.SourceCalls == 0 && f.Commands.Count == 0);
    }),
    ("existing VACS and TrackAudio settings require an external export", async () =>
    {
        foreach (var app in new[] { SoftwareApp.Vacs, SoftwareApp.TrackAudio })
        {
            using var f = new Fixture(app); var settings = f.AddSettings();
            await Reject(() => f.Service.PreviewAsync(app, f.Settings, null));
            var plan = await f.Preview(); Check(plan.BackupFiles.Files.Single().FullPath == settings && File.Exists(settings));
            await Reject(() => f.Service.PreviewAsync(app, f.Settings, Path.GetDirectoryName(settings)));
        }
    }),
    ("changed reviewed settings prevent package download and installation", async () =>
    {
        using var f = new Fixture(); var settings = f.AddSettings(); var plan = await f.Preview();
        File.WriteAllText(settings, "FAKE-CHANGED"); await Reject(() => f.Service.ApplyAsync(plan));
        Check(f.Downloads == 0 && f.Commands.Count == 0);
    }),
    ("new settings and installer residue after review invalidate the plan", async () =>
    {
        using var f = new Fixture(); var plan = await f.Preview(); f.AddSettings();
        await Reject(() => f.Service.ApplyAsync(plan));
        plan = await f.Preview(); f.Residue = true; await Reject(() => f.Service.ApplyAsync(plan));
        Check(f.Downloads == 0 && f.Commands.Count == 0);
    }),
    ("a changed official release requires a new review", async () =>
    {
        using var f = new Fixture(); var plan = await f.Preview(); f.Release = f.Release with { Sha256 = new string('A', 64) };
        await Reject(() => f.Service.ApplyAsync(plan)); Check(f.Downloads == 0 && f.Commands.Count == 0);
    }),
    ("a release changing during package preparation cannot start its installer", async () =>
    {
        using var f = new Fixture(); var plan = await f.Preview();
        var progress = new CallbackProgress(value => { if (value.Phase == FreshSoftwareInstallPhase.Verifying) f.Release = f.Release with { Sha256 = new string('B', 64) }; });
        await Reject(() => f.Service.ApplyAsync(plan, progress)); Check(f.Downloads == 1 && f.Commands.Count == 0);
    }),
    ("wrong official hash or installer product rejects before any native call", async () =>
    {
        using var f = new Fixture(); var plan = await f.Preview(); f.ResponseBytes = new byte[f.Bytes.Length];
        await Reject(() => f.Service.ApplyAsync(plan)); f.ResponseBytes = null; f.BadProduct = true;
        await Reject(() => f.Service.ApplyAsync(plan)); Check(f.Commands.Count == 0);
    }),
    ("incomplete oversized and foreign-redirect downloads remain inert", async () =>
    {
        using var f = new Fixture(); var plan = await f.Preview();
        foreach (var size in new[] { f.Bytes.Length - 1, f.Bytes.Length + 1 })
        { f.ResponseBytes = new byte[size]; await Reject(() => f.Service.ApplyAsync(plan)); }
        f.Respond = (_, _) => { var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new("https://unrelated.invalid/setup.exe"); return Task.FromResult(response); };
        await Reject(() => f.Service.ApplyAsync(plan)); Check(f.Commands.Count == 0 && f.Downloads == 3);
    }),
    ("stalled download body times out without a native process", async () =>
    {
        using var f = new Fixture(timeout: TimeSpan.FromMilliseconds(50));
        f.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) });
        var plan = await f.Preview(); await Reject(() => f.Service.ApplyAsync(plan)); Check(f.Commands.Count == 0 && !f.Service.IsBusy);
    }),
    ("cancelled backup preparation leaves original settings intact", async () =>
    {
        using var f = new Fixture(); var settings = f.AddSettings(); var plan = await f.Preview(); using var cancel = new CancellationTokenSource();
        var progress = new CallbackProgress(value => { if (value.Phase == FreshSoftwareInstallPhase.BackingUp) cancel.Cancel(); });
        await Reject(() => f.Service.ApplyAsync(plan, progress, cancel.Token));
        Check(File.ReadAllText(settings) == "FAKE-PRIVATE-SETTINGS" && f.Commands.Count == 0);
    }),
    ("VACS fresh command preserves data and never requests run-after-install", async () =>
    {
        using var f = new Fixture(); var settings = f.AddSettings(); var plan = await f.Preview();
        f.OnRun = command => { RemovalFileService.VerifyBackup(plan.BackupFiles, f.Service.LastBackupFolder!); return Task.FromResult(f.Simulate(command)); };
        var result = await f.Service.ApplyAsync(plan); var command = f.Commands.Single();
        Check(command.Elevate && command.Arguments.SequenceEqual(new[] { "/S", "/AllUsers" }));
        Check(command.InstallDirectory == plan.InstallRoot && result.ExecutablePath == plan.ExecutablePath && !result.RestartRequired);
        Check(File.ReadAllText(settings) == "FAKE-PRIVATE-SETTINGS" && result.BackupFolder == f.Service.LastBackupFolder);
        var args = FreshSoftwareInstallNative.BuildArguments(command);
        Check(args == "/S /AllUsers /D=" + plan.InstallRoot && !args.Contains("/D=\""));
    }),
    ("TrackAudio fresh command stays per-user without force-run", async () =>
    {
        using var f = new Fixture(SoftwareApp.TrackAudio); f.AddSettings(); var plan = await f.Preview();
        var result = await f.Service.ApplyAsync(plan); var command = f.Commands.Single();
        Check(!command.Elevate && command.Arguments.SequenceEqual(new[] { "/S", "/currentuser" }));
        Check(plan.Scope == "CurrentUser" && result.BackupFolder != null && !f.Running);
    }),
    ("vATIS beta is rejected without the explicit channel choice", async () =>
    {
        using var f = new Fixture(SoftwareApp.Vatis); await Reject(() => f.Service.PreviewAsync(f.App, f.Settings, null));
        var plan = await f.Preview(); Check(plan.AllowVatisBeta && plan.Review.Contains("BETA RELEASE") && f.SourceBetaFlags.SequenceEqual(new[] { false, true }));
    }),
    ("unreviewed vATIS Setup generation is refused during preview", async () =>
    {
        using var f = new Fixture(SoftwareApp.Vatis); f.UnreviewedVatis = true;
        await Reject(() => f.Preview()); Check(f.Downloads == 0 && f.VatisRuns == 0);
    }),
    ("vATIS preparation stays cancellable until the final adapter callback", async () =>
    {
        using var f = new Fixture(SoftwareApp.Vatis); var plan = await f.Preview();
        f.BeforeVatisBoundary = () => { Check(f.Service.CanCancel && f.VatisRuns == 0); return Task.CompletedTask; };
        var result = await f.Service.ApplyAsync(plan);
        Check(f.VatisRuns == 1 && f.Commands.Count == 0 && result.ExecutablePath == plan.ExecutablePath && f.TrustChecks > 0);
    }),
    ("invalid vATIS publisher stops before the Setup adapter", async () =>
    {
        using var f = new Fixture(SoftwareApp.Vatis); var plan = await f.Preview(); f.BadSignature = true;
        await Reject(() => f.Service.ApplyAsync(plan)); Check(f.VatisRuns == 0);
    }),
    ("installation appearing during vATIS Setup preparation blocks the final callback", async () =>
    {
        using var f = new Fixture(SoftwareApp.Vatis); var plan = await f.Preview();
        f.BeforeVatisBoundary = () => { Directory.CreateDirectory(f.TargetRoot); return Task.CompletedTask; };
        await Reject(() => f.Service.ApplyAsync(plan)); Check(f.VatisRuns == 0);
    }),
    ("damaged recovery export blocks the native installer", async () =>
    {
        using var f = new Fixture(); f.AddSettings(); var plan = await f.Preview();
        var progress = new CallbackProgress(value =>
        {
            if (value.Phase == FreshSoftwareInstallPhase.Installing)
            { var exported = Directory.EnumerateFiles(Path.Combine(f.Service.LastBackupFolder!, "Files"), "settings.json", SearchOption.AllDirectories).Single(); File.WriteAllText(exported, "CORRUPTED"); }
        });
        await Reject(() => f.Service.ApplyAsync(plan, progress)); Check(f.Commands.Count == 0 && f.Service.LastBackupFolder != null);
    }),
    ("UAC denial and vendor failure retain exact settings recovery location", async () =>
    {
        using var f = new Fixture(); var original = f.AddSettings(); var plan = await f.Preview();
        foreach (int code in new[] { 1223, 7 })
        {
            f.OnRun = _ => Task.FromResult(code); await Reject(() => f.Service.ApplyAsync(plan));
            Check(File.ReadAllText(original) == "FAKE-PRIVATE-SETTINGS"); RemovalFileService.VerifyBackup(plan.BackupFiles, f.Service.LastBackupFolder!);
        }
    }),
    ("no cancellation or forced restart occurs after native installation begins", async () =>
    {
        using var f = new Fixture(); using var cancel = new CancellationTokenSource(); var plan = await f.Preview();
        f.OnRun = command => { Check(!f.Service.CanCancel); cancel.Cancel(); f.Simulate(command); return Task.FromResult(3010); };
        var result = await f.Service.ApplyAsync(plan, null, cancel.Token);
        Check(result.RestartRequired && f.Commands.Count == 1);
    }),
    ("a zero exit is insufficient without exact post-install registration/version", async () =>
    {
        using var f = new Fixture(); var plan = await f.Preview();
        f.OnRun = command => { f.Simulate(command); f.Registrations.Clear(); return Task.FromResult(0); };
        await Reject(() => f.Service.ApplyAsync(plan)); Check(f.Commands.Count == 1);
    }),
    ("restart survives failed post-install verification and blocks fresh retries", async () =>
    {
        foreach (var app in new[] { SoftwareApp.Vacs, SoftwareApp.TrackAudio })
        {
            using var f = new Fixture(app); var plan = await f.Preview();
            f.OnRun = _ => Task.FromResult(3010);
            await Reject(() => f.Service.ApplyAsync(plan));
            Check(f.Service.RestartRequired && f.Commands.Count == 1 && !File.Exists(plan.ExecutablePath));
            await Reject(() => f.Service.ApplyAsync(plan));
            await Reject(() => f.Preview());
            Check(f.Commands.Count == 1 && !f.Service.CanCancel && !f.Service.IsBusy);
        }
    }),
    ("unexpected client launch is reported without killing a process", async () =>
    {
        using var f = new Fixture(); var plan = await f.Preview();
        f.OnRun = command => { var code = f.Simulate(command); f.Running = true; return Task.FromResult(code); };
        await Reject(() => f.Service.ApplyAsync(plan)); Check(f.Commands.Count == 1 && f.Running);
    }),
    ("concurrent apply is rejected while package preparation is active", async () =>
    {
        using var f = new Fixture(); var plan = await f.Preview(); using var cancel = new CancellationTokenSource(); var started = new TaskCompletionSource();
        f.Respond = async (_, token) => { started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new Exception("Unreachable"); };
        var active = f.Service.ApplyAsync(plan, null, cancel.Token); await started.Task;
        await Reject(() => f.Service.ApplyAsync(plan)); cancel.Cancel(); await Reject(() => active);
        Check(f.Commands.Count == 0 && !f.Service.IsBusy);
    }),
    ("fresh beta catalog flag does not change stable update channel behavior", async () =>
    {
        var body = JsonSerializer.Serialize(new { Assets = new[] { new { PackageId = "org.vatsim.vatis", Version = "4.1.0-beta.19", Type = "Full", FileName = "org.vatsim.vatis-4.1.0-beta.19-full.nupkg", SHA256 = new string('A', 64), Size = 100 } } });
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) })));
        var source = new SoftwareReleaseSource(http);
        Check(await source.GetLatestFreshAsync(SoftwareApp.Vatis, false) == null);
        Check((await source.GetLatestFreshAsync(SoftwareApp.Vatis, true))?.Version == "4.1.0-beta.19");
        Check(await source.GetLatestAsync(SoftwareApp.Vatis, "4.0.0", default) == null);
        Check((await source.GetLatestAsync(SoftwareApp.Vatis, "4.1.0-beta.18", default))?.Version == "4.1.0-beta.19");
    })
};
int failed = 0;
foreach (var test in tests) { try { await test.Run(); Console.WriteLine("PASS " + test.Name); } catch (Exception e) { failed++; Console.WriteLine("FAIL " + test.Name + ": " + e); } }
Console.WriteLine($"Fresh installation: {tests.Length - failed}/{tests.Length} passed. Synthetic files and fake runners only.");
return failed == 0 ? 0 : 1;
static void Check(bool value) { if (!value) throw new Exception("Assertion failed."); }
static async Task Reject(Func<Task> action)
{
    try { await action(); }
    catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException or TimeoutException) { return; }
    throw new Exception("Expected rejection before continuing installation.");
}

sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "LaunchpadFreshSynthetic-" + Guid.NewGuid().ToString("N"));
    public SoftwareApp App { get; }
    public string Recovery => PathOf("Recovery");
    public string TargetRoot => App switch { SoftwareApp.Vacs => PathOf("Program Files/vacs"), SoftwareApp.TrackAudio => PathOf("Local/Programs/trackaudio"), _ => PathOf("Local/org.vatsim.vatis") };
    public AppSettings Settings { get; } = new();
    public List<SoftwareRegistration> Registrations { get; } = [];
    public List<FreshSoftwareCommand> Commands { get; } = [];
    public List<bool> SourceBetaFlags { get; } = [];
    public bool Running, Residue, BadProduct, BadSignature, UnreviewedVatis;
    public string? Problem;
    public int SourceCalls, Downloads, VatisRuns, TrustChecks;
    public byte[] Bytes { get; }
    public byte[]? ResponseBytes;
    public SoftwareRelease Release;
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Respond;
    public Func<FreshSoftwareCommand, Task<int>>? OnRun;
    public Func<Task>? BeforeVatisBoundary;
    public FreshSoftwareInstallService Service { get; }
    private readonly HttpClient _http;
    public Fixture(SoftwareApp app = SoftwareApp.Vacs, TimeSpan? timeout = null)
    {
        App = app; Dir("Recovery");
        string version = app == SoftwareApp.Vacs ? "2.8.0" : app == SoftwareApp.TrackAudio ? "1.4.0" : "4.1.0-beta.19";
        string fileName = app == SoftwareApp.Vacs ? "vacs_2.8.0_x64-setup.exe" : app == SoftwareApp.TrackAudio ? "trackaudio-1.4.0-x64-setup.exe" : "org.vatsim.vatis-4.1.0-beta.19-full.nupkg";
        string uri = app == SoftwareApp.Vacs ? "https://github.com/vacs-project/vacs/releases/download/vacs-client-v2.8.0/" + fileName :
            app == SoftwareApp.TrackAudio ? "https://github.com/pierr3/TrackAudio/releases/download/1.4.0/" + fileName : "https://vatis.app/updates/windows/" + fileName;
        Bytes = app == SoftwareApp.Vatis ? Package() : Encoding.UTF8.GetBytes("Synthetic inert installer " + app);
        Release = new(app, version, new(uri), fileName, Convert.ToHexString(SHA256.HashData(Bytes)), Bytes.Length);
        _http = new(new Handler(async (request, token) =>
        {
            Downloads++;
            return Respond == null ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(ResponseBytes ?? Bytes) } : await Respond(request, token);
        }));
        var software = new SoftwareInstallerEnvironment
        {
            LocalAppData = Dir("Local"), ReadRegistrations = _ => Registrations.ToArray(), IsRunning = _ => Running,
            PrerequisiteProblem = _ => Problem,
            ReadBinary = path => new(Path.GetFileName(path) is "Update.exe" or "Squirrel.exe" ? "0.0.1251" : version,
                BadProduct ? "Unrelated" : app == SoftwareApp.Vacs ? "vacs" : app == SoftwareApp.TrackAudio ? "TrackAudio" : "vATIS"),
            VerifyPublisher = (_, publisher) => { if (publisher != "Justin Shannon") throw new Exception("Unexpected publisher"); TrustChecks++; if (BadSignature) throw new InvalidDataException("Untrusted synthetic signature"); },
            RunAsync = _ => throw new Exception("Unexpected production process runner")
        };
        var environment = new FreshSoftwareInstallEnvironment
        {
            LocalAppData = PathOf("Local"), RoamingAppData = Dir("Roaming"), ProgramFiles = Dir("Program Files"), ProgramFilesX86 = Dir("Program Files (x86)"),
            Software = software, PrerequisiteProblem = _ => Problem, HasInstallerResidue = _ => Residue,
            RunAsync = command => { Commands.Add(command); return OnRun?.Invoke(command) ?? Task.FromResult(Simulate(command)); },
            ValidateVatisDestination = root => { if (Directory.Exists(root) || System.IO.File.Exists(root) || Residue) throw new InvalidDataException("Synthetic vATIS residue"); },
            ValidateVatisRelease = _ => { if (UnreviewedVatis) throw new InvalidDataException("Unreviewed Setup generation"); },
            InstallVatisAsync = async (_, _, root, _, token, beforeInstall) =>
            {
                if (BeforeVatisBoundary != null) await BeforeVatisBoundary(); token.ThrowIfCancellationRequested();
                await beforeInstall(); VatisRuns++; Directory.CreateDirectory(root);
                File("Local/org.vatsim.vatis/current/vATIS.exe"); File("Local/org.vatsim.vatis/Update.exe"); File("Local/org.vatsim.vatis/current/sq.version", Manifest);
                return new(Path.Combine(root, "current", "vATIS.exe"));
            }
        };
        Service = new(_http, environment, (_, beta, token) => { token.ThrowIfCancellationRequested(); SourceCalls++; SourceBetaFlags.Add(beta); return Task.FromResult<SoftwareRelease?>(Release); },
            cacheRoot: PathOf("Cache"), downloadTimeout: timeout);
    }
    public Task<FreshSoftwareInstallPlan> Preview() => Service.PreviewAsync(App, Settings, Recovery, App == SoftwareApp.Vatis);
    public string AddSettings() => File(App == SoftwareApp.TrackAudio ? "Roaming/trackaudio/settings.json" : "Roaming/app.vacs.vacs-client/settings.json", "FAKE-PRIVATE-SETTINGS");
    public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
    public string Dir(string relative) { var path = PathOf(relative); Directory.CreateDirectory(path); return path; }
    public string File(string relative, string contents = "synthetic") { var path = PathOf(relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); System.IO.File.WriteAllText(path, contents); return path; }
    public int Simulate(FreshSoftwareCommand command)
    {
        Directory.CreateDirectory(command.InstallDirectory); var main = App == SoftwareApp.Vacs ? "vacs-client.exe" : "trackaudio.exe";
        System.IO.File.WriteAllText(Path.Combine(command.InstallDirectory, main), "synthetic installed binary");
        var scope = App == SoftwareApp.Vacs ? "AllUsers" : "CurrentUser";
        Registrations.Add(new(command.InstallDirectory, command.InstallDirectory, scope, Release.Version, main)); return 0;
    }
    private const string Manifest = "<package><metadata><id>org.vatsim.vatis</id><version>4.1.0-beta.19</version><channel>win</channel><mainExe>vATIS.exe</mainExe><os>win</os></metadata></package>";
    private static byte[] Package()
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            foreach (var pair in new[] { ("org.vatsim.vatis.nuspec", Manifest), ("lib/app/sq.version", Manifest), ("lib/app/vATIS.exe", "synthetic"), ("lib/app/Squirrel.exe", "synthetic") })
            { using var writer = new StreamWriter(zip.CreateEntry(pair.Item1).Open()); writer.Write(pair.Item2); }
        }
        return memory.ToArray();
    }
    public void Dispose()
    {
        _http.Dispose(); var root = Path.GetFullPath(Root);
        if (!Path.GetDirectoryName(root)!.Equals(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(root).StartsWith("LaunchpadFreshSynthetic-", StringComparison.Ordinal)) throw new Exception("Fixture cleanup refused.");
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{ protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
sealed class CallbackProgress(Action<FreshSoftwareInstallProgress> action) : IProgress<FreshSoftwareInstallProgress>
{ public void Report(FreshSoftwareInstallProgress value) => action(value); }
sealed class StalledStream : Stream
{
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) { await Task.Delay(Timeout.InfiniteTimeSpan, token); return 0; }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
