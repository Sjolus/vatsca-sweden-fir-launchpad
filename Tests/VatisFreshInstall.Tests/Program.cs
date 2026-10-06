using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

internal static class Program
{
    private static int _passed;
    private static async Task Main(string[] args)
    {
        await Run("valid exact bundle uses silent current-user Setup, returns verified stopped executable", async f =>
        {
            int callbacks = 0;
            var result = await f.Install(() => { callbacks++; Equal(0, f.Runs); return Task.CompletedTask; });
            Equal(1, callbacks); Equal(1, f.Runs); Equal(Path.Combine(f.Target, "current", "vATIS.exe"), result.ExecutablePath);
            Assert(f.LastCommand!.Arguments.SequenceEqual(new[] { "--silent", "--installto", f.Target, "--log", Path.Combine(Path.GetDirectoryName(f.LastCommand.Executable)!, "setup.log") }));
            Assert(!f.LastCommand.Elevate); Assert(f.LastCommand.Executable.StartsWith(Path.Combine(f.Local, "VatscaUpdateChecker")));
        });
        await Run("even an empty existing destination is refused", async f => { Directory.CreateDirectory(f.Target); await Reject(f); Equal(0, f.Requests); });
        await Run("existing destination file is refused", async f => { File.WriteAllText(f.Target, "kept"); await Reject(f); Equal("kept", File.ReadAllText(f.Target)); });
        await Run("existing profiles remain byte-for-byte untouched", async f =>
        {
            Directory.CreateDirectory(f.Target); var path = Path.Combine(f.Target, "AppConfig.json"); File.WriteAllText(path, "fake settings");
            await Reject(f); Equal("fake settings", File.ReadAllText(path));
        });
        await Run("other known existing copy is refused", async f => { Directory.CreateDirectory(f.Legacy); await Reject(f); });
        await Run("existing registration is refused before HTTP", async f => { f.ExistingRegistration = true; await Reject(f); Equal(0, f.Requests); });
        await Run("running product blocks fresh setup", async f => { f.Running = true; await Reject(f); });
        await Run("arbitrary target is refused", async f => { await Throws(() => f.Installer.InstallAsync(f.Release, f.PackagePath, f.Local, null, default)); Equal(0, f.Runs); });
        await Run("unreviewed newer release falls back instead of assuming CLI behavior", async f => { f.Release = f.Release with { Version = "4.1.0-beta.20" }; await Reject(f); });
        await Run("release official host is exact", async f => { f.Release = f.Release with { DownloadUri = new("https://vatis.app.evil.invalid/updates/windows/" + f.Release.FileName) }; await Reject(f); });
        await Run("release query is refused", async f => { f.Release = f.Release with { DownloadUri = new(f.Release.DownloadUri + "?alternate=1") }; await Reject(f); });
        await Run("release package size is pinned", async f => { f.Release = f.Release with { Size = f.Release.Size + 1 }; await Reject(f); });
        await Run("release package digest is pinned", async f => { f.Release = f.Release with { Sha256 = new string('A', 64) }; await Reject(f); });
        await Run("changed supplied full package blocks before setup download", async f => { File.AppendAllText(f.PackagePath, "changed"); await Reject(f); Equal(0, f.Requests); });
        await Run("official Setup redirect is not followed", async f => { f.Status = HttpStatusCode.Redirect; await Reject(f); });
        await Run("automatic final URI redirect is rejected", async f => { f.FinalUri = new("https://unknown.invalid/vATIS-Setup.exe"); await Reject(f); });
        await Run("wrong setup content length is refused", async f => { f.HeaderLength = f.Setup.Length + 1; await Reject(f); });
        await Run("chunked download exceeding bound is refused", async f => { f.HttpBytes = f.Setup.Concat(new byte[] { 1 }).ToArray(); f.OmitLength = true; await Reject(f); });
        await Run("truncated chunked download is refused", async f => { f.HttpBytes = f.Setup[..^1]; f.OmitLength = true; await Reject(f); });
        await Run("same-length tampered setup digest fails", async f => { f.HttpBytes = (byte[])f.Setup.Clone(); f.HttpBytes[^1] ^= 1; await Reject(f); });
        await Run("untrusted Setup publisher is refused", async f => { f.BadSetupPublisher = true; await Reject(f); });
        await Run("wrong Setup product identity is refused", async f => { f.BadSetupIdentity = true; await Reject(f); });
        await Run("package main executable publisher must verify", async f => { f.BadPackagePublisher = true; await Reject(f); Equal(0, f.Requests); });
        await Run("cancellation before preparation prevents all work", async f =>
        { using var cancel = new CancellationTokenSource(); cancel.Cancel(); await Throws(() => f.Install(token: cancel.Token)); Equal(0, f.Requests); Equal(0, f.Runs); });
        await Run("download cancellation never starts Setup", async f =>
        { using var cancel = new CancellationTokenSource(); f.OnRequest = () => cancel.Cancel(); await Throws(() => f.Install(token: cancel.Token)); Equal(0, f.Runs); });
        await Run("Setup download deadline is a timeout, not user cancellation", async f =>
        {
            f.WaitForCancellation = true;
            await TimeoutFailure(() => f.Install()); Equal(0, f.Runs);
        }, TimeSpan.FromMilliseconds(50));
        await Run("HTTP client timeout is a timeout, not user cancellation", async f =>
        {
            f.OnRequest = () => throw new TaskCanceledException("synthetic HTTP timeout");
            await TimeoutFailure(() => f.Install()); Equal(0, f.Runs);
        });
        await Run("final callback failure prevents Setup", async f => { await Throws(() => f.Install(() => throw new IOException("changed catalog"))); Equal(0, f.Runs); });
        await Run("destination appearing during preparation is never overwritten", async f =>
        { await Throws(() => f.Install(() => { Directory.CreateDirectory(f.Target); return Task.CompletedTask; })); Equal(0, f.Runs); });
        await Run("registration appearing during preparation is refused", async f =>
        { await Throws(() => f.Install(() => { f.ExistingRegistration = true; return Task.CompletedTask; })); Equal(0, f.Runs); });
        await Run("cancellation at final boundary is honored", async f =>
        { using var cancel = new CancellationTokenSource(); await Throws(() => f.Install(() => { cancel.Cancel(); return Task.CompletedTask; }, cancel.Token)); Equal(0, f.Runs); });
        await Run("cancellation after Setup starts does not cancel or kill vendor", async f =>
        { using var cancel = new CancellationTokenSource(); f.OnRun = () => { cancel.Cancel(); return Task.CompletedTask; }; await f.Install(token: cancel.Token); Equal(1, f.Runs); });
        await Run("native error is not reported successful", async f => { f.ExitCode = 1; await Throws(() => f.Install()); Equal(1, f.Runs); });
        await Run("reboot-required exit stays an error and blocks further Setup attempts", async f =>
        {
            f.ExitCode = 3010; f.WriteInstalled = false;
            await Throws(() => f.Install()); Equal(1, f.Runs); Equal(true, f.Installer.RestartRequired);
            await Throws(() => f.Install()); Equal(1, f.Runs); Equal(1, f.Requests);
        });
        await Run("zero exit without installation is rejected", async f => { f.WriteInstalled = false; await Throws(() => f.Install()); Equal(1, f.Runs); });
        await Run("missing resulting registration is rejected", async f => { f.ResultRegistration = false; await Throws(() => f.Install()); });
        await Run("wrong resulting version is rejected", async f => { f.BadInstalledVersion = true; await Throws(() => f.Install()); });
        await Run("unexpected normal client is detected, never killed", async f => { f.OnRun = () => { f.Running = true; return Task.CompletedTask; }; await Throws(() => f.Install()); Equal(1, f.Runs); });
        await Run("cross-call gate rejects second installer without queueing", async f =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.OnRun = async () => { entered.SetResult(); await finish.Task; };
            var first = f.Install(); await entered.Task;
            await Throws(() => f.Install()); Equal(1, f.Requests); finish.SetResult(); await first;
        });
        await Run("invalid bundle offset is refused even with matching signed-container seam", async f =>
        { var bytes = (byte[])f.Setup.Clone(); BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), long.MaxValue); await Throws(() => { VatisFreshInstaller.VerifyEmbeddedPackage(bytes, f.Release); return Task.CompletedTask; }); });
        await Run("wrong embedded package length is refused", async f =>
        { var bytes = (byte[])f.Setup.Clone(); BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), -1); await Throws(() => { VatisFreshInstaller.VerifyEmbeddedPackage(bytes, f.Release); return Task.CompletedTask; }); });
        await Run("duplicate bundle markers are refused", async f =>
        { var bytes = f.Setup.Concat(VatisFreshInstaller.BundleMarker).ToArray(); await Throws(() => { VatisFreshInstaller.VerifyEmbeddedPackage(bytes, f.Release); return Task.CompletedTask; }); });
        await Run("missing bundle marker is refused", async f =>
        { var bytes = (byte[])f.Setup.Clone(); bytes[32] ^= 1; await Throws(() => { VatisFreshInstaller.VerifyEmbeddedPackage(bytes, f.Release); return Task.CompletedTask; }); });
        await Run("embedded package hash mismatch is refused", async f =>
        { var bytes = (byte[])f.Setup.Clone(); bytes[^1] ^= 1; await Throws(() => { VatisFreshInstaller.VerifyEmbeddedPackage(bytes, f.Release); return Task.CompletedTask; }); });

        if (args.Length == 2 && args[0] == "--inspect-setup")
        {
            using var http = new HttpClient(new ThrowingHandler());
            var installer = new VatisFreshInstaller(http);
            var release = new SoftwareRelease(SoftwareApp.Vatis, VatisFreshInstaller.ReviewedVersion,
                new("https://vatis.app/updates/windows/org.vatsim.vatis-4.1.0-beta.19-full.nupkg"),
                "org.vatsim.vatis-4.1.0-beta.19-full.nupkg", VatisFreshInstaller.PackageSha256, VatisFreshInstaller.PackageSize);
            await using var input = new FileStream(args[1], FileMode.Open, FileAccess.Read, FileShare.Read);
            await installer.VerifySetupAsync(args[1], input, release, default);
            Console.WriteLine("PASS official Setup: read-only size, hash, Authenticode publisher, product and embedded full package verification (no process or registry calls)");
            _passed++;
        }
        Console.WriteLine($"{_passed} checks passed; no installer or client executed.");
    }

    private static async Task Run(string name, Func<Fixture, Task> action, TimeSpan? timeout = null)
    { using var fixture = new Fixture(timeout); await action(fixture); _passed++; Console.WriteLine("PASS " + name); }
    private static async Task TimeoutFailure(Func<Task> action)
    { try { await action(); } catch (TimeoutException e) { Assert(e.Message.Contains("No installer was started")); return; } throw new Exception("Expected timeout classification."); }
    private static async Task Reject(Fixture f) { await Throws(() => f.Install()); Equal(0, f.Runs); }
    private static async Task Throws(Func<Task> action)
    { try { await action(); } catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException) { return; } throw new Exception("Expected rejection."); }
    private static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; actual {actual}."); }

    private sealed class ThrowingHandler : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new Exception("Network is forbidden in read-only artifact verification."); }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Launchpad-VatisFresh-Tests", Guid.NewGuid().ToString("N"));
        public string Local => Path.Combine(Root, "Local");
        public string Target => Path.Combine(Local, SoftwareInstaller.VatisId);
        public string Legacy => Path.Combine(Local, "vATIS");
        public string PackagePath => Path.Combine(Root, "full.nupkg");
        public VatisFreshInstaller Installer { get; }
        public SoftwareRelease Release;
        public byte[] Setup, HttpBytes;
        public bool ExistingRegistration, Running, BadSetupPublisher, BadPackagePublisher, BadSetupIdentity, BadInstalledVersion;
        public bool OmitLength, WaitForCancellation, WriteInstalled = true, ResultRegistration = true;
        public int Runs, Requests, ExitCode;
        public long? HeaderLength;
        public Uri? FinalUri;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public Action? OnRequest;
        public Func<Task>? OnRun;
        public SoftwareInstallCommand? LastCommand;
        private readonly HttpClient _http;
        private const string Manifest = "<package><metadata><id>org.vatsim.vatis</id><version>4.1.0-beta.19</version><channel>win</channel><mainExe>vATIS.exe</mainExe><os>win</os></metadata></package>";

        public Fixture(TimeSpan? timeout = null)
        {
            Directory.CreateDirectory(Local);
            using (var zip = ZipFile.Open(PackagePath, ZipArchiveMode.Create))
            {
                foreach (var name in new[] { "org.vatsim.vatis.nuspec", "lib/app/sq.version", "lib/app/vATIS.exe", "lib/app/Squirrel.exe" })
                { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(name.EndsWith(".exe") ? "synthetic, never executable" : Manifest); }
            }
            byte[] package = File.ReadAllBytes(PackagePath);
            Release = new(SoftwareApp.Vatis, VatisFreshInstaller.ReviewedVersion,
                new("https://vatis.app/updates/windows/org.vatsim.vatis-4.1.0-beta.19-full.nupkg"),
                "org.vatsim.vatis-4.1.0-beta.19-full.nupkg", Hash(package), package.Length);
            Setup = new byte[96 + package.Length];
            BinaryPrimitives.WriteInt64LittleEndian(Setup.AsSpan(16), 96);
            BinaryPrimitives.WriteInt64LittleEndian(Setup.AsSpan(24), package.Length);
            VatisFreshInstaller.BundleMarker.CopyTo(Setup, 32);
            package.CopyTo(Setup, 96); HttpBytes = Setup;
            var pin = new VatisSetupPin(Release.Version, Setup.Length, Hash(Setup), package.Length, Hash(package));
            var software = new SoftwareInstallerEnvironment
            {
                LocalAppData = Local,
                ReadRegistrations = _ => throw new Exception("Unexpected OS registration query."),
                IsRunning = _ => Running,
                PrerequisiteProblem = _ => throw new Exception("Unexpected prerequisite query."),
                ReadBinary = path =>
                {
                    string name = Path.GetFileName(path);
                    if (name == "vATIS-Setup.exe") return new(Release.Version, BadSetupIdentity ? "wrong" : "vATIS");
                    if (name is "Squirrel.exe" or "Update.exe") return new("0.0.1251", "Velopack");
                    return new(BadInstalledVersion && path.StartsWith(Target) ? "4.1.0-beta.18" : Release.Version, "vATIS");
                },
                VerifyPublisher = (path, publisher) =>
                {
                    Equal("Justin Shannon", publisher);
                    if (BadSetupPublisher && Path.GetFileName(path) == "vATIS-Setup.exe" || BadPackagePublisher && Path.GetFileName(path) == "vATIS.exe")
                        throw new InvalidDataException("Synthetic bad publisher.");
                },
                RunAsync = async command =>
                {
                    Runs++; LastCommand = command;
                    if (OnRun != null) await OnRun();
                    if (WriteInstalled)
                    {
                        Directory.CreateDirectory(Path.Combine(Target, "current"));
                        File.WriteAllText(Path.Combine(Target, "current", "vATIS.exe"), "fake");
                        File.WriteAllText(Path.Combine(Target, "current", "sq.version"), Manifest);
                        File.WriteAllText(Path.Combine(Target, "Update.exe"), "fake");
                    }
                    return ExitCode;
                }
            };
            var environment = new VatisFreshInstallerEnvironment
            { Software = software, OtherKnownRoots = [Legacy], HasRegistration = () => ExistingRegistration,
                RegistrationMatches = _ => ResultRegistration, DownloadTimeout = timeout ?? TimeSpan.FromMinutes(10) };
            _http = new(new Handler(this)); Installer = new(_http, environment, pin);
        }
        public Task<SoftwareInstallResult> Install(Func<Task>? callback = null, CancellationToken token = default) => Installer.InstallAsync(Release, PackagePath, Target, null, token, callback);
        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        public void Dispose()
        {
            _http.Dispose();
            var full = Path.GetFullPath(Root);
            if (!full.StartsWith(Path.Combine(Path.GetTempPath(), "Launchpad-VatisFresh-Tests") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe test cleanup.");
            Directory.Delete(full, true);
        }
        private sealed class Handler(Fixture f) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                f.Requests++; Equal(VatisFreshInstaller.SetupUrl, request.RequestUri!.AbsoluteUri); f.OnRequest?.Invoke(); token.ThrowIfCancellationRequested();
                if (f.WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, token);
                var response = new HttpResponseMessage(f.Status) { RequestMessage = new(HttpMethod.Get, f.FinalUri ?? request.RequestUri), Content = new ByteArrayContent(f.HttpBytes) };
                if (f.HeaderLength.HasValue) response.Content.Headers.ContentLength = f.HeaderLength;
                else if (f.OmitLength) response.Content = new StreamContent(new MemoryStream(f.HttpBytes));
                return response;
            }
        }
    }
}
