using System.Net;
using System.Reflection.PortableExecutable;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

if (args is ["--verify-package", var appText, var package] && Enum.TryParse<SoftwareApp>(appText, out var app))
{
    // Explicit static verification only; no registry query, installer execution, or live network.
    SoftwareInstaller.RejectReparse(package);
    using var file = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read);
    SoftwarePrerequisiteService.VerifyPackage(app, package, new SoftwarePrerequisiteEnvironment());
    Console.WriteLine($"PASS {app} Microsoft prerequisite identity and trusted signature; installer was not executed.");
    return;
}
if (args.Length != 0) throw new ArgumentException("Usage: --verify-package Vacs|TrackAudio <absolute path>, or no arguments for offline synthetic tests.");

var tests = new (string Name, Func<Task> Run)[]
{
    ("check has no download, trust, or installer side effects", () =>
    {
        using var f = new Fixture(SoftwareApp.Vacs);
        Check(SoftwarePrerequisiteService.GetProblem(f.App, f.Environment())!.Contains("Machine-wide"));
        f.Installed = new(140, 0, 0, 0);
        Check(SoftwarePrerequisiteService.GetProblem(f.App, f.Environment()) == null);
        Check(f.Requests == 0 && f.TrustChecks == 0 && f.Runs == 0); return Task.CompletedTask;
    }),
    ("invalid WebView registration version is not installed", () =>
    {
        using var f = new Fixture(SoftwareApp.Vacs); f.Installed = new(0, 0, 0, 0);
        Check(SoftwarePrerequisiteService.GetProblem(f.App, f.Environment()) != null); return Task.CompletedTask;
    }),
    ("TrackAudio requires the reviewed v14 x64 baseline", () =>
    {
        using var f = new Fixture(SoftwareApp.TrackAudio); f.Installed = new(14, 30, 0, 0);
        Check(SoftwarePrerequisiteService.GetProblem(f.App, f.Environment()) != null);
        f.Installed = new(14, 44, 35211, 0); Check(SoftwarePrerequisiteService.GetProblem(f.App, f.Environment()) == null);
        f.Installed = new(15, 0, 0, 0); Check(SoftwarePrerequisiteService.GetProblem(f.App, f.Environment()) != null); return Task.CompletedTask;
    }),
    ("vATIS no-op does not even query runtime registration", async () =>
    {
        using var f = new Fixture(SoftwareApp.Vatis); f.ReadVersion = () => throw new Exception("No probe expected.");
        Check(SoftwarePrerequisiteService.GetProblem(f.App, f.Environment()) == null);
        Check(!(await f.Install()).RestartRequired && f.Requests == 0 && f.Runs == 0);
    }),
    ("VatEFS uses the shared x86 runtime route without launching the client", async () =>
    {
        using var f = new Fixture(SoftwareApp.VatEfs);
        Check(SoftwarePrerequisiteService.GetProblem(f.App, f.Environment())!.Contains("x86"));
        f.Response = (request, _) =>
        {
            Check(request.RequestUri!.AbsoluteUri == EuroScopePrerequisiteService.DownloadUrl);
            return Fixture.Bytes();
        };
        await f.Install();
        Check(f.TrustChecks == 1 && f.Runs == 1 && f.Command!.Elevate);
        Check(Path.GetFileName(f.Command!.Executable) == "vc_redist.x86.exe");
        Check(f.Command.Arguments.SequenceEqual(new[] { "/install", "/quiet", "/norestart" }));
        Check(SoftwarePrerequisiteService.GetProblem(f.App, f.Environment()) == null);
        await f.Install(); Check(f.Runs == 1);
    }),
    ("VatEFS rejects an x64 runtime package and retains restart after failed verification", async () =>
    {
        using var wrong = new Fixture(SoftwareApp.VatEfs);
        wrong.Binary = wrong.Binary with { OriginalFilename = "VC_redist.x64.exe" };
        await Reject(() => wrong.Install(), "supported Microsoft"); Check(wrong.Runs == 0);
        using var restart = new Fixture(SoftwareApp.VatEfs);
        restart.ExitCode = 3010; restart.SetInstalled = false;
        bool latched = false; restart.OnRestartRequired = () => latched = true;
        await Reject(() => restart.Install(), "could not be confirmed"); Check(latched && restart.Runs == 1);
    }),
    ("inaccessible check returns actionable problem", () =>
    {
        using var f = new Fixture(SoftwareApp.Vacs); f.ReadVersion = () => throw new UnauthorizedAccessException();
        Check(SoftwarePrerequisiteService.GetProblem(f.App, f.Environment())!.Contains("could not be checked")); return Task.CompletedTask;
    }),
    ("present runtime avoids download and execution", async () =>
    {
        using var f = new Fixture(SoftwareApp.TrackAudio); f.Installed = EuroScopePrerequisiteService.MinimumVersion;
        Check(!(await f.Install()).RestartRequired && f.Requests == 0 && f.Runs == 0);
    }),
    ("WebView setup is explicit elevated silent machine installation", async () =>
    {
        using var f = new Fixture(SoftwareApp.Vacs); await f.Install();
        Check(f.TrustChecks == 1 && f.Runs == 1 && f.Command!.Elevate);
        Check(f.Command!.Arguments.SequenceEqual(new[] { "/silent", "/install" }));
        Check(!File.Exists(f.Command.Executable));
    }),
    ("x64 VC setup is elevated quiet with no restart", async () =>
    {
        using var f = new Fixture(SoftwareApp.TrackAudio); await f.Install();
        Check(f.TrustChecks == 1 && f.Runs == 1 && f.Command!.Elevate);
        Check(f.Command!.Arguments.SequenceEqual(new[] { "/install", "/quiet", "/norestart" }));
    }),
    ("runtime that appears during verification avoids execution", async () =>
    {
        using var f = new Fixture(SoftwareApp.Vacs); f.OnTrust = () => f.Installed = new(140, 0, 0, 0);
        await f.Install(); Check(f.TrustChecks == 1 && f.Runs == 0);
    }),
    ("reboot required remains explicit", async () =>
    {
        using var f = new Fixture(SoftwareApp.TrackAudio); f.ExitCode = 3010;
        int notices = 0; f.OnRestartRequired = () => notices++;
        Check((await f.Install()).RestartRequired && notices == 1);
    }),
    ("WebView reboot requirement survives failed machine registration verification", async () =>
    {
        using var f = new Fixture(SoftwareApp.Vacs); f.ExitCode = 3010; f.SetInstalled = false;
        int notices = 0; f.OnRestartRequired = () => notices++;
        f.ReadVersion = () =>
        {
            if (f.Runs > 0) Check(notices == 1);
            return null;
        };
        await Reject(f.Install, "could not be confirmed");
        Check(notices == 1 && f.Runs == 1 && f.Delays == 15 && f.Installed == null);
    }),
    ("x64 runtime reboot requirement is reported before failed verification", async () =>
    {
        using var f = new Fixture(SoftwareApp.TrackAudio); f.ExitCode = 3010; f.SetInstalled = false;
        int notices = 0; f.OnRestartRequired = () => notices++;
        f.ReadVersion = () =>
        {
            if (f.Runs > 0) Check(notices == 1);
            return null;
        };
        await Reject(f.Install, "could not be confirmed");
        Check(notices == 1 && f.Runs == 1 && f.Installed == null);
    }),
    ("UAC cancellation is reported without retry", async () =>
    {
        using var f = new Fixture(SoftwareApp.Vacs); f.ExitCode = 1223;
        await Reject(f.Install, "elevation"); Check(f.Runs == 1);
    }),
    ("native failure cannot claim success", async () =>
    {
        using var f = new Fixture(SoftwareApp.TrackAudio); f.ExitCode = 1603;
        int notices = 0; f.OnRestartRequired = () => notices++;
        await Reject(f.Install, "1603"); Check(f.Runs == 1 && notices == 0);
    }),
    ("successful exit still requires machine registration", async () =>
    {
        using var f = new Fixture(SoftwareApp.Vacs); f.SetInstalled = false;
        await Reject(f.Install, "could not be confirmed"); Check(f.Delays == 15);
    }),
    ("WebView asynchronous registration is bounded and observed", async () =>
    {
        using var f = new Fixture(SoftwareApp.Vacs); f.SetInstalled = false;
        f.OnDelay = () => { if (f.Delays == 2) f.Installed = new(140, 0, 0, 0); };
        await f.Install(); Check(f.Delays == 2 && f.Runs == 1);
    }),
    ("untrusted Microsoft-looking binary never executes", async () =>
    {
        using var f = new Fixture(SoftwareApp.Vacs); f.TrustFailure = true;
        await Reject(f.Install, "signature"); Check(f.Runs == 0);
    }),
    ("wrong bootstrapper architecture is rejected", async () =>
    {
        using var f = new Fixture(SoftwareApp.TrackAudio); f.Binary = f.Binary with { Machine = Machine.Amd64 };
        await Reject(f.Install, "not the supported"); Check(f.Runs == 0 && f.TrustChecks == 0);
    }),
    ("x86 VC payload cannot satisfy x64 product identity", async () =>
    {
        using var f = new Fixture(SoftwareApp.TrackAudio); f.Binary = f.Binary with { OriginalFilename = "VC_redist.x86.exe" };
        await Reject(f.Install, "not the supported"); Check(f.Runs == 0);
    }),
    ("Microsoft unrelated product cannot satisfy WebView identity", async () =>
    {
        using var f = new Fixture(SoftwareApp.Vacs); f.Binary = f.Binary with { ProductName = "Microsoft Edge" };
        await Reject(f.Install, "not the supported"); Check(f.Runs == 0);
    }),
    ("obsolete WebView bootstrapper is rejected", async () =>
    {
        using var f = new Fixture(SoftwareApp.Vacs); f.Binary = f.Binary with { ProductVersion = "1.3.0.0" };
        await Reject(f.Install, "not the supported"); Check(f.Runs == 0);
    }),
    ("Microsoft WebView delivery redirect is followed explicitly", async () =>
    {
        using var f = new Fixture(SoftwareApp.Vacs);
        f.Response = (_, count) => count == 1 ? Fixture.Redirect("https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/file") : Fixture.Bytes();
        await f.Install(); Check(f.Requests == 2 && f.Runs == 1);
    }),
    ("foreign redirect is blocked before its request", async () =>
    {
        using var f = new Fixture(SoftwareApp.Vacs); f.Response = (_, _) => Fixture.Redirect("https://go.microsoft.com.evil.test/setup.exe");
        await Reject(f.Install, "unsupported source"); Check(f.Requests == 1 && f.Runs == 0);
    }),
    ("HTTP redirect is blocked", async () =>
    {
        using var f = new Fixture(SoftwareApp.TrackAudio); f.Response = (_, _) => Fixture.Redirect("http://download.microsoft.com/setup.exe");
        await Reject(f.Install, "unsupported source"); Check(f.Runs == 0);
    }),
    ("redirect loop is bounded", async () =>
    {
        using var f = new Fixture(SoftwareApp.Vacs); f.Response = (_, _) => Fixture.Redirect(SoftwarePrerequisiteService.WebViewDownloadUrl);
        await Reject(f.Install, "too many"); Check(f.Requests == 6 && f.Runs == 0);
    }),
    ("oversized WebView response is rejected before writing", async () =>
    {
        using var f = new Fixture(SoftwareApp.Vacs); f.Response = (_, _) => { var r = Fixture.Bytes(); r.Content.Headers.ContentLength = 33L * 1024 * 1024; return r; };
        await Reject(f.Install, "invalid size"); Check(f.Runs == 0);
    }),
    ("truncated download cannot reach verification", async () =>
    {
        using var f = new Fixture(SoftwareApp.TrackAudio); f.Response = (_, _) => { var r = Fixture.Bytes(); r.Content.Headers.ContentLength = 500; return r; };
        await Reject(f.Install, "incomplete"); Check(f.TrustChecks == 0 && f.Runs == 0);
    }),
    ("cancellation after verification prevents native start", async () =>
    {
        using var f = new Fixture(SoftwareApp.Vacs); using var c = new CancellationTokenSource(); f.OnTrust = c.Cancel;
        try { await f.Install(c.Token); throw new Exception("Expected cancellation."); } catch (OperationCanceledException) { }
        Check(f.Runs == 0);
    }),
    ("cancellation after native start cannot interrupt installation", async () =>
    {
        using var f = new Fixture(SoftwareApp.TrackAudio); using var c = new CancellationTokenSource(); f.OnRun = c.Cancel;
        await f.Install(c.Token); Check(f.Runs == 1 && f.Installed != null);
    })
};
foreach (var test in tests) { await test.Run(); Console.WriteLine("PASS prerequisite: " + test.Name); }
Console.WriteLine($"{tests.Length} checks passed. No registry, installer, application or live network was used.");

static void Check(bool result) { if (!result) throw new Exception("Prerequisite assertion failed."); }
static async Task Reject(Func<Task<SoftwarePrerequisiteResult>> action, string message)
{
    try { await action(); }
    catch (Exception ex) when (ex is IOException or InvalidDataException)
    { if (!ex.Message.Contains(message, StringComparison.OrdinalIgnoreCase)) throw; return; }
    throw new Exception("Expected rejection: " + message);
}

sealed class Fixture : IDisposable
{
    internal SoftwareApp App { get; }
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "Launchpad-SoftwarePrerequisite-tests-" + Guid.NewGuid().ToString("N"));
    internal Version? Installed;
    internal Func<Version?>? ReadVersion;
    internal int Requests, Runs, TrustChecks, Delays, ExitCode;
    internal bool TrustFailure, SetInstalled = true;
    internal Action? OnRun, OnTrust, OnDelay, OnRestartRequired;
    internal SoftwareInstallCommand? Command;
    internal EuroScopeRuntimeBinary Binary;
    internal Func<HttpRequestMessage, int, HttpResponseMessage> Response = (_, _) => Bytes();
    private readonly HttpClient _http;
    internal Fixture(SoftwareApp app)
    {
        App = app;
        Binary = app == SoftwareApp.Vacs
            ? new(Machine.I386, "Microsoft Edge Update", "1.3.275.13", "Microsoft Corporation", "MicrosoftEdgeUpdateSetup.exe")
            : app == SoftwareApp.VatEfs
                ? new(Machine.I386, "Microsoft Visual C++ 2015-2022 Redistributable (x86) - 14.44.35211", "14.44.35211.0", "Microsoft Corporation", "VC_redist.x86.exe")
                : new(Machine.I386, "Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.44.35211", "14.44.35211.0", "Microsoft Corporation", "VC_redist.x64.exe");
        _http = new(new Handler(request => Response(request, ++Requests)));
    }
    internal SoftwarePrerequisiteEnvironment Environment() => new()
    {
        LocalAppData = Root,
        ReadInstalledVersion = app => app != App ? throw new Exception("Wrong product runtime requested.") : ReadVersion != null ? ReadVersion() : Installed,
        ReadBinary = _ => Binary,
        VerifyPublisher = (_, publisher) =>
        {
            TrustChecks++;
            if (publisher != "Microsoft Corporation" || TrustFailure) throw new InvalidDataException("Untrusted signature.");
            OnTrust?.Invoke();
        },
        RunAsync = command =>
        {
            Runs++; Command = command; OnRun?.Invoke();
            if (SetInstalled && ExitCode is 0 or 3010) Installed = App == SoftwareApp.Vacs ? new(140, 0, 0, 0) : EuroScopePrerequisiteService.MinimumVersion;
            return Task.FromResult(ExitCode);
        },
        DelayAsync = _ => { Delays++; OnDelay?.Invoke(); return Task.CompletedTask; }
    };
    internal Task<SoftwarePrerequisiteResult> Install() => Install(CancellationToken.None);
    internal Task<SoftwarePrerequisiteResult> Install(CancellationToken token) => SoftwarePrerequisiteService.InstallAsync(App, Environment(), _http, cancellationToken: token, onRestartRequired: OnRestartRequired);
    internal static HttpResponseMessage Bytes() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[32]) };
    internal static HttpResponseMessage Redirect(string target)
    { var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new(target); return response; }
    public void Dispose()
    {
        _http.Dispose();
        var full = Path.GetFullPath(Root);
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("Launchpad-SoftwarePrerequisite-tests-", StringComparison.Ordinal))
            throw new Exception("Refused test cleanup outside its temporary root.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }
}

sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
}
