using System.Net;
using System.Security.Cryptography;
using System.Text;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

var tests = new (string Name, Func<Task> Run)[]
{
    ("policy pins the reviewed token-authentication MSI instead of latest", () =>
    {
        Check(EuroScopePolicy.SupportedVersion == "3.2.3.2" && EuroScopePolicy.MsiVersion == "3.2.3");
        Check(EuroScopePolicy.PackageSize == 18_494_976 && EuroScopePolicy.PackageSha256 == "DE11BF2F62E47D8BDA7E6C54F49F24FD96E46DE37B0D2381E6020623C48CC7F1");
        Check(EuroScopePolicy.DownloadUrl == "https://euroscope.hu/install/EuroScopeSetup.3.2.3.2.msi");
        return Task.CompletedTask;
    }),
    ("fresh preview is read-only and specifies the default machine destination", () =>
    {
        using var f = new Fixture(); var plan = f.Preview();
        Check(plan.Action == EuroScopeInstallAction.Install && plan.Scope == "AllUsers");
        Check(plan.ExecutablePath == f.PathOf("ProgramFilesX86/EuroScope/EuroScope.exe"));
        Check(!Directory.Exists(plan.InstallRoot) && f.Commands.Count == 0 && f.Requests == 0);
        return Task.CompletedTask;
    }),
    ("registered copy without an adopted executable requires Settings review", async () =>
    {
        using var f = new Fixture(); f.Install("3.2.3.2"); f.Settings.EuroscopeExePath = "";
        await Reject(() => Task.FromResult(f.Preview())); Check(f.Commands.Count == 0);
    }),
    ("portable unknown and multiple installed copies fail closed", async () =>
    {
        using var f = new Fixture(); f.Install("3.2.3.2"); var registration = f.Registrations.Single();
        f.Registrations.Clear(); await Reject(() => Task.FromResult(f.Preview()));
        f.Registrations.Add(registration); f.Registrations.Add(registration with { Scope = "CurrentUser" });
        await Reject(() => Task.FromResult(f.Preview()));
        f.Registrations.RemoveAt(1); f.Registrations[0] = registration with { Publisher = "Unrelated publisher" };
        await Reject(() => Task.FromResult(f.Preview())); Check(f.Commands.Count == 0);
    }),
    ("fresh install refuses an unregistered standard copy and nonempty destination", async () =>
    {
        using var f = new Fixture(); var existing = f.File("ProgramFilesX86/EuroScope/unrelated.txt");
        await Reject(() => Task.FromResult(f.Preview())); Check(File.Exists(existing));
        f.File("ProgramFiles/EuroScope/EuroScope.exe"); await Reject(() => Task.FromResult(f.Preview()));
    }),
    ("exact build repairs while newer and older builds require labeled replacement", () =>
    {
        using var f = new Fixture(); f.Install("3.2.3.2");
        Check(f.Preview().Action == EuroScopeInstallAction.Repair);
        f.Install("3.2.4.0"); var higher = f.Preview();
        Check(higher.Action == EuroScopeInstallAction.Replace && higher.IsDowngrade && higher.Review.Contains("DOWNGRADE"));
        f.Install("3.2.2.0"); var lower = f.Preview();
        Check(lower.Action == EuroScopeInstallAction.Replace && !lower.IsDowngrade);
        return Task.CompletedTask;
    }),
    ("repair and replacement require an external recovery export", async () =>
    {
        using var f = new Fixture(); f.Install("3.2.3.2");
        await Reject(() => Task.FromResult(f.Service.Preview(f.Settings, null)));
        await Reject(() => Task.FromResult(f.Service.Preview(f.Settings, Path.GetDirectoryName(f.Settings.EuroscopeExePath))));
        f.Install("3.2.4.0"); await Reject(() => Task.FromResult(f.Service.Preview(f.Settings, null)));
    }),
    ("recovery snapshot covers installation configured roaming and legacy data", () =>
    {
        using var f = new Fixture(); f.Install("3.2.3.2");
        f.Settings.EuroscopeDataPath = f.Dir("CustomData");
        var configured = f.File("CustomData/custom.prf", "FAKE-CREDENTIALS");
        var roaming = f.File("Roaming/EuroScope/sample.prf", "FAKE-PERSONAL");
        var documents = f.File("Documents/EuroScope/old.prf", "FAKE-LEGACY");
        var empty = f.Dir("CustomData/empty"); var plan = f.Preview();
        Check(new[] { configured, roaming, documents, f.Settings.EuroscopeExePath }.All(path => plan.BackupFiles.Files.Any(file => file.FullPath == path)));
        Check(plan.BackupFiles.Directories.Any(directory => directory.FullPath == empty));
        f.Settings.EuroscopeExePath = f.PathOf("unrelated.exe");
        Check(plan.ExecutablePath != f.Settings.EuroscopeExePath && plan.Settings.EuroscopeExePath == plan.ExecutablePath);
        return Task.CompletedTask;
    }),
    ("missing prerequisite blocks review and never invokes another installer", async () =>
    {
        using var f = new Fixture(); f.PrerequisiteProblem = "Install the supported x86 runtime separately.";
        await Reject(() => Task.FromResult(f.Preview())); Check(f.Requests == 0 && f.Commands.Count == 0);
    }),
    ("changed reviewed files registration and running process block before download", async () =>
    {
        using var f = new Fixture(); f.Install("3.2.3.2"); var plan = f.Preview();
        File.WriteAllText(plan.ExecutablePath, "changed"); await Reject(() => f.Service.ApplyAsync(plan));
        plan = f.Preview(); f.Registrations[0] = f.Registrations[0] with { Scope = "CurrentUser" };
        await Reject(() => f.Service.ApplyAsync(plan));
        plan = f.Preview(); f.Running = true; await Reject(() => f.Service.ApplyAsync(plan));
        Check(f.Requests == 0 && f.Commands.Count == 0);
    }),
    ("a previously absent data folder appearing after review invalidates the plan", async () =>
    {
        using var f = new Fixture(); var plan = f.Preview();
        var newFile = f.File("Roaming/EuroScope/new.prf", "FAKE-NEW");
        await Reject(() => f.Service.ApplyAsync(plan)); Check(File.Exists(newFile) && f.Commands.Count == 0);
    }),
    ("another standard copy appearing after review blocks before download", async () =>
    {
        using var f = new Fixture(); var plan = f.Preview(); f.File("ProgramFiles/EuroScope/EuroScope.exe");
        await Reject(() => f.Service.ApplyAsync(plan)); Check(f.Requests == 0 && f.Commands.Count == 0);
    }),
    ("wrong download hash prevents backup and every installer call", async () =>
    {
        using var f = new Fixture(); f.Install("3.2.4.0"); var plan = f.Preview();
        f.Response = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[f.Bytes.Length]) });
        await Reject(() => f.Service.ApplyAsync(plan)); Check(f.Commands.Count == 0 && !Directory.EnumerateFileSystemEntries(f.Recovery).Any());
    }),
    ("oversized incomplete and foreign-redirect downloads cannot reach installation", async () =>
    {
        using var f = new Fixture(); var plan = f.Preview();
        foreach (var length in new[] { f.Bytes.Length - 1, f.Bytes.Length + 1 })
        {
            f.Response = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[length]) });
            await Reject(() => f.Service.ApplyAsync(plan));
        }
        f.Response = (_, _) => { var response = new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location = new("https://unrelated.invalid/package.msi"); return Task.FromResult(response); };
        await Reject(() => f.Service.ApplyAsync(plan)); Check(f.Requests == 3 && f.Commands.Count == 0);
    }),
    ("download-body timeout stops before any destructive step", async () =>
    {
        using var f = new Fixture(TimeSpan.FromMilliseconds(50));
        f.Response = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) });
        await Reject(() => f.Service.ApplyAsync(f.Preview())); Check(f.Commands.Count == 0 && !f.Service.IsBusy);
    }),
    ("cancelled preparation leaves the installation and sources intact", async () =>
    {
        using var f = new Fixture(); f.Install("3.2.3.2"); using var cancel = new CancellationTokenSource();
        var plan = f.Preview(); var progress = new CallbackProgress(value => { if (value.Phase == EuroScopeInstallPhase.BackingUp) cancel.Cancel(); });
        await Reject(() => f.Service.ApplyAsync(plan, progress, cancel.Token));
        Check(File.Exists(plan.ExecutablePath) && f.Commands.Count == 0 && !f.Service.IsBusy);
    }),
    ("fresh installation requests only pinned MSI quiet no-restart machine installation", async () =>
    {
        using var f = new Fixture(); var plan = f.Preview(); var result = await f.Service.ApplyAsync(plan);
        Check(result.InstallationCompleted && !result.RestartRequired && result.BackupFolder == null && f.Commands.Count == 1);
        var command = f.Commands.Single();
        Check(command.Arguments[0] == "/i" && command.Arguments.Contains("/qn") && command.Arguments.Contains("/norestart"));
        Check(command.Arguments.Contains("ALLUSERS=1") && command.Arguments.Contains("TARGETDIR=" + plan.InstallRoot) && command.Elevate && command.IsMsi);
        Check(command.Arguments.Contains(EuroScopeMsiFootprint.DataDirectoryProperty + "=" + f.PathOf("Roaming/EuroScope")));
        Check(!command.Arguments.Any(arg => arg.StartsWith("REINSTALL")) && !f.Running);
        var start = AtcRemovalVendor.BuildStartInfo(command with { Arguments = ["/i", @"C:\package cache\EuroScope.msi", "/qn", @"TARGETDIR=C:\Program Files (x86)\EuroScope", "ALLUSERS=", @"EUROSCOPE=C:\Users\Controller Name\AppData\Roaming\EuroScope\"] });
        Check(start.Arguments == "/i \"C:\\package cache\\EuroScope.msi\" /qn TARGETDIR=\"C:\\Program Files (x86)\\EuroScope\" ALLUSERS=\"\" EUROSCOPE=\"C:\\Users\\Controller Name\\AppData\\Roaming\\EuroScope\\\"");
        Check(start.WindowStyle == System.Diagnostics.ProcessWindowStyle.Normal && !start.CreateNoWindow);
    }),
    ("exact build repair preserves scope and recaches only the pinned package", async () =>
    {
        using var f = new Fixture(); f.Install("3.2.3.2", "CurrentUser"); var plan = f.Preview();
        var result = await f.Service.ApplyAsync(plan); var command = f.Commands.Single();
        Check(result.Action == EuroScopeInstallAction.Repair && result.InstallationCompleted && result.BackupFolder == f.Service.LastBackupFolder);
        Check(command.Arguments.Contains("REINSTALL=ALL") && command.Arguments.Contains("REINSTALLMODE=vamus"));
        Check(command.Arguments.Contains("ALLUSERS=") && !command.Elevate);
        RemovalFileService.VerifyBackup(plan.BackupFiles, result.BackupFolder!);
    }),
    ("replacement prepares verified package and recovery before uninstall", async () =>
    {
        using var f = new Fixture(); f.Install("3.2.4.0"); var plan = f.Preview();
        f.OnCommand = command =>
        {
            if (command.Arguments[0] == "/x")
            {
                Check(f.Requests == 1 && f.Service.LastBackupFolder != null && !f.Service.CanCancel);
                RemovalFileService.VerifyBackup(plan.BackupFiles, f.Service.LastBackupFolder!);
                var cached = Directory.EnumerateFiles(f.Cache, "*.msi", SearchOption.AllDirectories).Single();
                Check(File.ReadAllBytes(cached).SequenceEqual(f.Bytes));
            }
            return f.SimulateCommand(command);
        };
        var result = await f.Service.ApplyAsync(plan);
        Check(result.InstallationCompleted && f.Commands.Select(c => c.Arguments[0]).SequenceEqual(new[] { "/x", "/i" }));
        Check(result.BackupFolder == Directory.GetDirectories(f.Recovery).Single());
    }),
    ("vendor removal of reviewed standard sample files permits replacement", async () =>
    {
        using var f = new Fixture(); f.Install("3.2.4.0"); var sample = f.File("Roaming/EuroScope/sample.prf", "FAKE-PERSONAL");
        var plan = f.Preview(); f.OnCommand = command => { if (command.Arguments[0] == "/x") File.Delete(sample); return f.SimulateCommand(command); };
        var result = await f.Service.ApplyAsync(plan);
        Check(result.InstallationCompleted && f.Commands.Count == 2); RemovalFileService.VerifyBackup(plan.BackupFiles, result.BackupFolder!);
    }),
    ("changed surviving data after uninstall stops the following install", async () =>
    {
        using var f = new Fixture(); f.Install("3.2.4.0"); var sample = f.File("Roaming/EuroScope/sample.prf", "FAKE-OLD");
        var plan = f.Preview(); f.OnCommand = command => { var result = f.SimulateCommand(command); File.WriteAllText(sample, "FAKE-NEW"); return result; };
        await Reject(() => f.Service.ApplyAsync(plan)); Check(f.Commands.Count == 1 && File.ReadAllText(sample) == "FAKE-NEW");
        RemovalFileService.VerifyBackup(plan.BackupFiles, f.Service.LastBackupFolder!);
    }),
    ("previously absent data appearing during uninstall stops the following install", async () =>
    {
        using var f = new Fixture(); f.Install("3.2.4.0"); var plan = f.Preview();
        f.OnCommand = command => { var result = f.SimulateCommand(command); f.File("Documents/EuroScope/new.prf", "FAKE-NEW"); return result; };
        await Reject(() => f.Service.ApplyAsync(plan)); Check(f.Commands.Count == 1);
        RemovalFileService.VerifyBackup(plan.BackupFiles, f.Service.LastBackupFolder!);
    }),
    ("corrupted export after uninstall prevents the following install", async () =>
    {
        using var f = new Fixture(); f.Install("3.2.4.0"); var plan = f.Preview();
        f.OnCommand = command =>
        {
            var result = f.SimulateCommand(command);
            var exported = Directory.EnumerateFiles(Path.Combine(f.Service.LastBackupFolder!, "Files"), "EuroScope.exe", SearchOption.AllDirectories).Single();
            File.WriteAllText(exported, "corrupted synthetic export"); return result;
        };
        await Reject(() => f.Service.ApplyAsync(plan)); Check(f.Commands.Count == 1 && f.Service.LastBackupFolder == Directory.GetDirectories(f.Recovery).Single());
    }),
    ("failed or elevation-cancelled uninstall never starts installation", async () =>
    {
        using var f = new Fixture(); f.Install("3.2.4.0"); var plan = f.Preview();
        foreach (var code in new[] { 7, 1223 })
        {
            f.Commands.Clear(); f.OnCommand = _ => code;
            await Reject(() => f.Service.ApplyAsync(plan)); Check(f.Commands.Count == 1 && File.Exists(plan.ExecutablePath));
            RemovalFileService.VerifyBackup(plan.BackupFiles, f.Service.LastBackupFolder!);
        }
    }),
    ("restart required by uninstall stops safely before installing supported build", async () =>
    {
        using var f = new Fixture(); f.Install("3.2.4.0"); var plan = f.Preview();
        f.OnCommand = command => { f.SimulateCommand(command); return 3010; };
        var result = await f.Service.ApplyAsync(plan);
        Check(result.RestartRequired && !result.InstallationCompleted && f.Commands.Count == 1 && !File.Exists(plan.ExecutablePath));
        RemovalFileService.VerifyBackup(plan.BackupFiles, result.BackupFolder!);
    }),
    ("failed installation retains recovery after the old build was removed", async () =>
    {
        using var f = new Fixture(); f.Install("3.2.4.0"); var plan = f.Preview();
        f.OnCommand = command => command.Arguments[0] == "/x" ? f.SimulateCommand(command) : 1223;
        await Reject(() => f.Service.ApplyAsync(plan)); Check(f.Commands.Count == 2 && f.Service.LastBackupFolder != null);
        RemovalFileService.VerifyBackup(plan.BackupFiles, f.Service.LastBackupFolder!);
    }),
    ("uninstall restart survives failed verification and blocks replacement retry", async () =>
    {
        using var f = new Fixture(); f.Install("3.2.4.0"); var plan = f.Preview();
        f.OnCommand = _ => 3010; // Registration deliberately remains: verification must still fail.
        await Reject(() => f.Service.ApplyAsync(plan));
        Check(f.Service.RestartRequired && f.Commands.Count == 1 && File.Exists(plan.ExecutablePath));
        var backup = f.Service.LastBackupFolder;
        await Reject(() => f.Service.ApplyAsync(plan));
        await Reject(() => Task.Run(() => f.Preview()));
        Check(f.Commands.Count == 1 && f.Service.LastBackupFolder == backup);
        RemovalFileService.VerifyBackup(plan.BackupFiles, backup!);
    }),
    ("package preparation cannot silently change the reviewed Windows Installer", async () =>
    {
        using var f = new Fixture(); var plan = f.Preview();
        var progress = new CallbackProgress(value => { if (value.Phase == EuroScopeInstallPhase.Installing) File.WriteAllText(plan.MsiexecPath, "changed-msiexec"); });
        await Reject(() => f.Service.ApplyAsync(plan, progress)); Check(f.Commands.Count == 0);
    }),
    ("cancellation after the boundary does not interrupt the installer", async () =>
    {
        using var f = new Fixture(); using var cancel = new CancellationTokenSource();
        var progress = new CallbackProgress(value => { if (value.Phase == EuroScopeInstallPhase.Installing) { Check(!value.CanCancel && !f.Service.CanCancel); cancel.Cancel(); } });
        var result = await f.Service.ApplyAsync(f.Preview(), progress, cancel.Token);
        Check(result.InstallationCompleted && f.Commands.Count == 1);
    }),
    ("successful exit alone is not enough without exact installed version", async () =>
    {
        using var f = new Fixture(); f.OnCommand = command => { var result = f.SimulateCommand(command); f.Binaries[f.InstalledExe!] = new("3.2.4.0", "EuroScope Application"); return result; };
        await Reject(() => f.Service.ApplyAsync(f.Preview())); Check(f.Commands.Count == 1);
    }),
    ("installation restart requirement is returned without restarting anything", async () =>
    {
        using var f = new Fixture(); f.OnCommand = command => { f.SimulateCommand(command); return 3010; };
        var result = await f.Service.ApplyAsync(f.Preview());
        Check(result.InstallationCompleted && result.RestartRequired && f.Commands.Count == 1);
    }),
    ("installation restart survives missing installed files and blocks retry", async () =>
    {
        using var f = new Fixture(); var plan = f.Preview(); f.OnCommand = _ => 3010;
        await Reject(() => f.Service.ApplyAsync(plan));
        Check(f.Service.RestartRequired && f.Commands.Count == 1 && !File.Exists(plan.ExecutablePath));
        await Reject(() => f.Service.ApplyAsync(plan));
        await Reject(() => Task.Run(() => f.Preview()));
        Check(f.Commands.Count == 1);
    }),
    ("concurrent apply is rejected while the first remains cancellable", async () =>
    {
        using var f = new Fixture(); using var cancel = new CancellationTokenSource(); var started = new TaskCompletionSource();
        f.Response = async (_, token) => { started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); throw new Exception("unreachable"); };
        var plan = f.Preview(); var first = f.Service.ApplyAsync(plan, null, cancel.Token); await started.Task;
        Check(f.Service.IsBusy && f.Service.CanCancel);
        await Reject(() => f.Service.ApplyAsync(plan)); cancel.Cancel(); await Reject(() => first);
        Check(!f.Service.IsBusy && f.Commands.Count == 0);
    })
};
int failures = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL " + test.Name + ": " + ex); }
}
Console.WriteLine($"EuroScope installation: {tests.Length - failures}/{tests.Length} passed. Synthetic files and fake process runner only.");
return failures == 0 ? 0 : 1;

static void Check(bool value) { if (!value) throw new Exception("Assertion failed."); }
static async Task Reject(Func<Task> action)
{
    try { await action(); }
    catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException or TimeoutException) { return; }
    throw new Exception("Expected operation to be rejected.");
}

sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "LaunchpadEuroScopeSynthetic-" + Guid.NewGuid().ToString("N"));
    public string Recovery => PathOf("Recovery");
    public string Cache => PathOf("Cache");
    public AppSettings Settings { get; } = new();
    public List<AtcMsiRegistration> Registrations { get; } = [];
    public Dictionary<string, SoftwareBinary> Binaries { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<AtcRemovalCommand> Commands { get; } = [];
    public bool Running;
    public string? PrerequisiteProblem;
    public string? InstalledExe;
    public int Requests;
    public byte[] Bytes { get; } = Encoding.UTF8.GetBytes("Synthetic inert MSI bytes; no process execution.");
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Response;
    public Func<AtcRemovalCommand, int>? OnCommand;
    public EuroScopeInstallService Service { get; }
    private readonly HttpClient _http;
    public Fixture(TimeSpan? timeout = null)
    {
        Directory.CreateDirectory(Root); Dir("Recovery"); File("Windows/System32/msiexec.exe", "fake Windows Installer");
        var removal = new AtcRemovalEnvironment
        {
            LocalAppData = Dir("Local"), RoamingAppData = Dir("Roaming"), DocumentsDirectory = Dir("Documents"),
            ProgramFilesDirectory = Dir("ProgramFiles"), ProgramFilesX86Directory = Dir("ProgramFilesX86"), WindowsDirectory = PathOf("Windows"),
            ReadMsiRegistrations = () => Registrations.ToArray(), IsProcessRunning = _ => Running,
            GetMsiComponentPath = (_, _) => InstalledExe,
            ReadMsiFootprint = (registration, root, roaming, _) => new(registration.ProductCode, registration.Scope, root,
                Path.Combine(roaming, "EuroScope"), [Path.Combine(root, "EuroScope.exe")], "synthetic-footprint"),
            RunAsync = command => { Commands.Add(command); return Task.FromResult(OnCommand?.Invoke(command) ?? SimulateCommand(command)); },
            Software = new()
            {
                LocalAppData = PathOf("Local"), ReadRegistrations = _ => [], ReadBinary = path => Binaries[path],
                IsRunning = _ => false, PrerequisiteProblem = _ => null,
                VerifyPublisher = (_, _) => throw new Exception("Unexpected signature probe"),
                RunAsync = _ => throw new Exception("Unexpected application execution")
            }
        };
        _http = new(new Handler(async (request, token) =>
        {
            Requests++;
            return Response == null ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) } : await Response(request, token);
        }));
        var package = new EuroScopePackage(new(EuroScopePolicy.DownloadUrl), EuroScopePolicy.PackageFileName, Bytes.Length, Convert.ToHexString(SHA256.HashData(Bytes)));
        Service = new(_http, new() { Removal = removal, PrerequisiteProblem = () => PrerequisiteProblem }, package, Cache, timeout);
    }
    public EuroScopeInstallPlan Preview() => Service.Preview(Settings, Settings.EuroscopeExePath.Length == 0 ? null : Recovery);
    public string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
    public string Dir(string relative) { var path = PathOf(relative); Directory.CreateDirectory(path); return path; }
    public string File(string relative, string contents = "synthetic") { var path = PathOf(relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); System.IO.File.WriteAllText(path, contents); return path; }
    public void Install(string version, string scope = "AllUsers")
    {
        Settings.EuroscopeExePath = File("Installed/EuroScope/EuroScope.exe", "synthetic EuroScope " + version);
        InstalledExe = Settings.EuroscopeExePath;
        Binaries[InstalledExe] = new(version, "EuroScope Application");
        Registrations.Clear();
        var parsed = Version.Parse(version);
        Registrations.Add(new(version == EuroScopePolicy.SupportedVersion ? EuroScopePolicy.ProductCode : "{22222222-2222-2222-2222-222222222222}",
            "EuroScope", "Gergely Csernák", $"{parsed.Major}.{parsed.Minor}.{parsed.Build}", Path.GetDirectoryName(InstalledExe)!, "", true, scope));
    }
    public int SimulateCommand(AtcRemovalCommand command)
    {
        if (command.Arguments[0] == "/x")
        {
            if (InstalledExe != null) System.IO.File.Delete(InstalledExe);
            Registrations.Clear(); return 0;
        }
        if (command.Arguments[0] != "/i") throw new Exception("Only inert MSI commands are expected.");
        var root = command.Arguments.Single(arg => arg.StartsWith("TARGETDIR=", StringComparison.Ordinal))[10..];
        InstalledExe = Path.Combine(root, "EuroScope.exe");
        Directory.CreateDirectory(root); System.IO.File.WriteAllText(InstalledExe, "synthetic supported EuroScope");
        Binaries[InstalledExe] = new(EuroScopePolicy.SupportedVersion, "EuroScope Application");
        Registrations.Clear(); Registrations.Add(new(EuroScopePolicy.ProductCode, "EuroScope", "Gergely Csernák", EuroScopePolicy.MsiVersion,
            root, "", true, command.Arguments.Contains("ALLUSERS=1") ? "AllUsers" : "CurrentUser"));
        return 0;
    }
    public void Dispose()
    {
        _http.Dispose();
        var absolute = Path.GetFullPath(Root); var parent = Path.GetDirectoryName(absolute)!;
        if (!parent.Equals(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(absolute).StartsWith("LaunchpadEuroScopeSynthetic-", StringComparison.Ordinal)) throw new Exception("Fixture cleanup refused.");
        if (Directory.Exists(absolute)) Directory.Delete(absolute, true);
    }
}
sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{ protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
sealed class CallbackProgress(Action<EuroScopeInstallProgress> callback) : IProgress<EuroScopeInstallProgress>
{ public void Report(EuroScopeInstallProgress value) => callback(value); }
sealed class StalledStream : Stream
{
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) { await Task.Delay(Timeout.InfiniteTimeSpan, token); return 0; }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override void Flush() { }
}
