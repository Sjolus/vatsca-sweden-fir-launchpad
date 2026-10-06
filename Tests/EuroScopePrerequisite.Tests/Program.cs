using System.Net;
using System.Reflection.PortableExecutable;
using VatscaUpdateChecker.Services;

if (args is ["--verify-package", var package])
{
    // Opt-in static verification of a publicly downloaded package: no registry, network or process execution.
    SoftwareInstaller.RejectReparse(package);
    using var file = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read);
    EuroScopePrerequisiteService.VerifyPackage(package, new EuroScopePrerequisiteEnvironment());
    Console.WriteLine("PASS Microsoft x86 runtime identity and trusted signature; installer was not executed.");
    return;
}
if (args.Length != 0) throw new ArgumentException("Unknown test argument.");

var tests = new (string Name, Func<Task> Run)[]
{
    ("baseline accepts reviewed and newer v14 only", () =>
    {
        Check(!EuroScopePrerequisiteService.MeetsBaseline(null));
        Check(!EuroScopePrerequisiteService.MeetsBaseline(new(14, 0, 24212, 0)));
        Check(!EuroScopePrerequisiteService.MeetsBaseline(new(14, 44, 35210, 0)));
        Check(EuroScopePrerequisiteService.MeetsBaseline(new(14, 44, 35211, 0)));
        Check(EuroScopePrerequisiteService.MeetsBaseline(new(14, 50, 10000, 0)));
        Check(!EuroScopePrerequisiteService.MeetsBaseline(new(15, 0, 0, 0)));
        return Task.CompletedTask;
    }),
    ("check is read-only and reports missing or unsupported runtimes", () =>
    {
        using var f = new Fixture();
        Check(EuroScopePrerequisiteService.GetProblem(f.Environment()) != null);
        f.Installed = EuroScopePrerequisiteService.MinimumVersion;
        Check(EuroScopePrerequisiteService.GetProblem(f.Environment()) == null);
        Check(f.Requests == 0 && f.Runs == 0 && f.TrustChecks == 0); return Task.CompletedTask;
    }),
    ("inaccessible prerequisite returns a clear check problem", () =>
    {
        using var f = new Fixture(); f.ReadVersion = () => throw new UnauthorizedAccessException();
        Check(EuroScopePrerequisiteService.GetProblem(f.Environment())!.Contains("could not be checked")); return Task.CompletedTask;
    }),
    ("supported runtime avoids download and execution", async () =>
    {
        using var f = new Fixture(); f.Installed = EuroScopePrerequisiteService.MinimumVersion;
        Check(!(await f.Install()).RestartRequired && f.Requests == 0 && f.Runs == 0);
    }),
    ("explicit install verifies then runs quiet x86 setup with elevation", async () =>
    {
        using var f = new Fixture(); var result = await f.Install();
        Check(!result.RestartRequired && f.TrustChecks == 1 && f.Runs == 1);
        Check(f.Command!.Arguments.SequenceEqual(new[] { "/install", "/quiet", "/norestart" }) && f.Command.Elevate);
        Check(!File.Exists(f.Command.Executable));
    }),
    ("successful reboot-required result is retained", async () =>
    {
        using var f = new Fixture(); f.ExitCode = 3010;
        int notices = 0; f.OnRestartRequired = () => notices++;
        Check((await f.Install()).RestartRequired && notices == 1);
    }),
    ("reboot requirement is reported before failed runtime verification", async () =>
    {
        using var f = new Fixture(); f.ExitCode = 3010; f.SetInstalled = false;
        int notices = 0; f.OnRestartRequired = () => notices++;
        f.ReadVersion = () =>
        {
            if (f.Runs > 0) Check(notices == 1);
            return null;
        };
        await Reject(f.Install, "could not be confirmed");
        Check(notices == 1 && f.Runs == 1 && f.Installed == null);
    }),
    ("UAC cancellation is reported without retrying execution", async () =>
    {
        using var f = new Fixture(); f.ExitCode = 1223; await Reject(f.Install, "elevation"); Check(f.Runs == 1);
    }),
    ("installer failure cannot claim success", async () =>
    {
        using var f = new Fixture(); f.ExitCode = 1603;
        int notices = 0; f.OnRestartRequired = () => notices++;
        await Reject(f.Install, "1603"); Check(f.Runs == 1 && notices == 0);
    }),
    ("successful exit still requires installed runtime confirmation", async () =>
    {
        using var f = new Fixture(); f.SetInstalled = false; await Reject(f.Install, "could not be confirmed");
    }),
    ("untrusted signer never executes", async () =>
    {
        using var f = new Fixture(); f.TrustFailure = true; await Reject(f.Install, "signature"); Check(f.Runs == 0);
    }),
    ("wrong architecture never executes", async () =>
    {
        using var f = new Fixture(); f.Binary = f.Binary with { Machine = Machine.Amd64 }; await Reject(f.Install, "not the supported"); Check(f.Runs == 0);
    }),
    ("foreign Microsoft product never executes", async () =>
    {
        using var f = new Fixture(); f.Binary = f.Binary with { ProductName = "Microsoft unrelated tool" }; await Reject(f.Install, "not the supported"); Check(f.Runs == 0);
    }),
    ("wrong original filename never executes", async () =>
    {
        using var f = new Fixture(); f.Binary = f.Binary with { OriginalFilename = "VC_redist.x64.exe" }; await Reject(f.Install, "not the supported"); Check(f.Runs == 0);
    }),
    ("obsolete package cannot satisfy the reviewed baseline", async () =>
    {
        using var f = new Fixture(); f.Binary = f.Binary with { ProductVersion = "14.30.0.0" }; await Reject(f.Install, "not the supported"); Check(f.Runs == 0);
    }),
    ("current v14 product naming is recognized", async () =>
    {
        using var f = new Fixture(); f.Binary = f.Binary with { ProductName = "Microsoft Visual C++ v14 Redistributable (x86) - 14.44.35211" }; await f.Install(); Check(f.Runs == 1);
    }),
    ("Microsoft HTTPS redirect is followed explicitly", async () =>
    {
        using var f = new Fixture(); f.Response = (_, count) => count == 1 ? Fixture.Redirect("https://download.visualstudio.microsoft.com/download/test/vc_redist.x86.exe") : Fixture.Bytes();
        await f.Install(); Check(f.Requests == 2 && f.Runs == 1);
    }),
    ("foreign redirects are rejected before a request is sent", async () =>
    {
        using var f = new Fixture(); f.Response = (_, _) => Fixture.Redirect("https://download.microsoft.com.evil.test/installer.exe");
        await Reject(f.Install, "unsupported source"); Check(f.Requests == 1 && f.Runs == 0);
    }),
    ("HTTP redirects are rejected before a request is sent", async () =>
    {
        using var f = new Fixture(); f.Response = (_, _) => Fixture.Redirect("http://download.microsoft.com/installer.exe");
        await Reject(f.Install, "unsupported source"); Check(f.Requests == 1 && f.Runs == 0);
    }),
    ("redirect loops are bounded", async () =>
    {
        using var f = new Fixture(); f.Response = (_, _) => Fixture.Redirect(EuroScopePrerequisiteService.DownloadUrl);
        await Reject(f.Install, "too many"); Check(f.Requests == 6 && f.Runs == 0);
    }),
    ("oversized content declaration is rejected before writing", async () =>
    {
        using var f = new Fixture(); f.Response = (_, _) => { var r = Fixture.Bytes(); r.Content.Headers.ContentLength = EuroScopePrerequisiteService.MaximumDownloadBytes + 1; return r; };
        await Reject(f.Install, "invalid size"); Check(f.Runs == 0);
    }),
    ("truncated response cannot reach verification or execution", async () =>
    {
        using var f = new Fixture(); f.Response = (_, _) => { var r = Fixture.Bytes(); r.Content.Headers.ContentLength = 500; return r; };
        await Reject(f.Install, "incomplete"); Check(f.TrustChecks == 0 && f.Runs == 0);
    }),
    ("download cancellation never invokes the installer", async () =>
    {
        using var f = new Fixture(); using var cancellation = new CancellationTokenSource();
        f.Response = (_, _) => { cancellation.Cancel(); return Fixture.Bytes(); };
        try { await f.Install(cancellation.Token); throw new Exception("Expected cancellation."); } catch (OperationCanceledException) { }
        Check(f.Runs == 0);
    }),
    ("cancellation after installer start does not interrupt native execution", async () =>
    {
        using var f = new Fixture(); using var cancellation = new CancellationTokenSource();
        f.OnRun = () => cancellation.Cancel();
        await f.Install(cancellation.Token); Check(f.Runs == 1 && f.Installed != null);
    })
};
foreach (var test in tests) { await test.Run(); Console.WriteLine("PASS prerequisite: " + test.Name); }
Console.WriteLine($"{tests.Length} prerequisite checks passed. No registry, installer, application or live network was used.");

static void Check(bool result) { if (!result) throw new Exception("Prerequisite assertion failed."); }
static async Task Reject(Func<Task<EuroScopePrerequisiteResult>> action, string message)
{
    try { await action(); }
    catch (Exception ex) when (ex is IOException or InvalidDataException)
    { if (!ex.Message.Contains(message, StringComparison.OrdinalIgnoreCase)) throw; return; }
    throw new Exception("Expected rejection: " + message);
}

sealed class Fixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "Launchpad-Prerequisite-tests-" + Guid.NewGuid().ToString("N"));
    internal Version? Installed;
    internal Func<Version?>? ReadVersion;
    internal int Requests, Runs, TrustChecks, ExitCode;
    internal bool TrustFailure, SetInstalled = true;
    internal Action? OnRun, OnRestartRequired;
    internal SoftwareInstallCommand? Command;
    internal EuroScopeRuntimeBinary Binary = new(Machine.I386, "Microsoft Visual C++ 2015-2022 Redistributable (x86) - 14.44.35211", "14.44.35211.0", "Microsoft Corporation", "VC_redist.x86.exe");
    internal Func<HttpRequestMessage, int, HttpResponseMessage> Response = (_, _) => Bytes();
    private readonly HttpClient _http;
    internal Fixture() => _http = new(new Handler(request => Response(request, ++Requests)));
    internal EuroScopePrerequisiteEnvironment Environment() => new()
    {
        LocalAppData = Root,
        ReadInstalledVersion = () => ReadVersion != null ? ReadVersion() : Installed,
        ReadBinary = _ => Binary,
        VerifyPublisher = (_, publisher) =>
        {
            TrustChecks++;
            if (publisher != "Microsoft Corporation" || TrustFailure) throw new InvalidDataException("Untrusted signature.");
        },
        RunAsync = command =>
        {
            Runs++; Command = command; OnRun?.Invoke();
            if (SetInstalled && ExitCode is 0 or 3010) Installed = EuroScopePrerequisiteService.MinimumVersion;
            return Task.FromResult(ExitCode);
        }
    };
    internal Task<EuroScopePrerequisiteResult> Install() => Install(CancellationToken.None);
    internal Task<EuroScopePrerequisiteResult> Install(CancellationToken token) => EuroScopePrerequisiteService.InstallAsync(Environment(), _http, cancellationToken: token, onRestartRequired: OnRestartRequired);
    internal static HttpResponseMessage Bytes() => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[32]) };
    internal static HttpResponseMessage Redirect(string target)
    { var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new(target); return response; }
    public void Dispose()
    {
        _http.Dispose();
        var full = Path.GetFullPath(Root);
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("Launchpad-Prerequisite-tests-", StringComparison.Ordinal))
            throw new Exception("Refused test cleanup outside its temporary root.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }
}

sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
}
