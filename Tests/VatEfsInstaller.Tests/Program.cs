using System.Security.Cryptography;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

if (args is ["--verify-system-installer"])
{
    string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");
    VatEfsMsiTrust.VerifySystemInstaller(path);
    Console.WriteLine("Verified trusted Microsoft Windows signer and Microsoft Corporation publisher for system msiexec. No process started.");
    return;
}
if (args is ["--inspect-msi", var msi])
{
    // Optional read-only inspection of an explicitly supplied original MSI. Never invokes installation APIs.
    using var locked = new FileStream(Path.GetFullPath(msi), FileMode.Open, FileAccess.Read, FileShare.Read);
    var metadata = VatEfsMsiPackageReader.Read(Path.GetFullPath(msi));
    Console.WriteLine($"Verified MSI layout: VatEFS {metadata.Version}, {metadata.Components.Count} components, {metadata.Components.Sum(c => c.Files.Count)} files.");
    return;
}
if (args.Length != 0) throw new ArgumentException("Use no arguments for synthetic tests, --inspect-msi <path> for read-only package inspection, or --verify-system-installer for a read-only signature check.");

int passed = 0, failed = 0;
await Test("MSI final command quotes property values and leaves diagnostics visible", () =>
{
    var command = new SoftwareInstallCommand(@"C:\Windows\System32\msiexec.exe",
        ["/i", @"C:\verified cache\vatefs.msi", "/qn", "/norestart", @"INSTALLDIR=C:\Program Files\VatEFS", "ALLUSERS=1"], true) { IsMsi = true };
    var start = SoftwareInstallerNative.BuildStartInfo(command);
    Equal("/i \"C:\\verified cache\\vatefs.msi\" /qn /norestart INSTALLDIR=\"C:\\Program Files\\VatEFS\" ALLUSERS=1", start.Arguments);
    True(start.UseShellExecute && start.Verb == "runas" && !start.CreateNoWindow && start.WindowStyle == System.Diagnostics.ProcessWindowStyle.Normal);
    return Task.CompletedTask;
});
await Test("MSI property formatting preserves trailing slashes empty values and embedded quotes", () =>
{
    Equal("INSTALLDIR=\"C:\\folder name\\\"", SoftwareInstallerNative.QuoteMsiArgument(@"INSTALLDIR=C:\folder name\"));
    Equal("ALLUSERS=\"\"", SoftwareInstallerNative.QuoteMsiArgument("ALLUSERS="));
    Equal("COMPANYNAME=\"Acme \"\"Widgets\"\"\"", SoftwareInstallerNative.QuoteMsiArgument("COMPANYNAME=Acme \"Widgets\""));
    Equal("\"C:\\package=copy path\\setup.msi\"", SoftwareInstallerNative.QuoteMsiArgument(@"C:\package=copy path\setup.msi"));
    return Task.CompletedTask;
});
await Test("non-MSI argument formatting keeps existing CRT quoting and window behavior", () =>
{
    var command = new SoftwareInstallCommand("fixture.exe", ["/S", @"PACKAGE=C:\folder name\"], false);
    var start = SoftwareInstallerNative.BuildStartInfo(command);
    Equal("/S \"PACKAGE=C:\\folder name\\\\\"", start.Arguments);
    True(!start.UseShellExecute && start.CreateNoWindow && start.WindowStyle == System.Diagnostics.ProcessWindowStyle.Hidden);
    return Task.CompletedTask;
});
await Test("package directory leases acquire root to leaf and block parent rename", () =>
{
    using var f = new Fixture(false);
    string parent = Path.GetDirectoryName(f.Package)!;
    var observed = new List<string>();
    using (var lease = new VatEfsMsiPathLease([f.Package], directory =>
    {
        observed.Add(directory);
        if (directory == parent) Throws(() => Directory.Move(parent, parent + "-moved"));
    }))
    {
        var expected = new Stack<string>();
        for (string? directory = parent; directory != null; directory = Path.GetDirectoryName(directory)) expected.Push(directory);
        True(expected.SequenceEqual(observed, StringComparer.OrdinalIgnoreCase));
        Throws(() => Directory.Move(parent, parent + "-moved"));
    }
    Directory.Move(parent, parent + "-moved");
    Directory.Move(parent + "-moved", parent);
    return Task.CompletedTask;
});
await Test("directory lease rejects reparse and non-directory handle attributes", () =>
{
    VatEfsMsiPathLease.RequireDirectoryAttributes(0x10);
    Throws(() => VatEfsMsiPathLease.RequireDirectoryAttributes(0x10 | 0x400));
    Throws(() => VatEfsMsiPathLease.RequireDirectoryAttributes(0x80));
    return Task.CompletedTask;
});
await Test("failed directory lease releases already protected parents", () =>
{
    using var f = new Fixture(false);
    string parent = Path.GetDirectoryName(f.Package)!;
    Throws(() => { using var lease = new VatEfsMsiPathLease(Path.Combine(parent, "missing", "package.msi")); });
    Directory.Move(parent, parent + "-moved");
    Directory.Move(parent + "-moved", parent);
    return Task.CompletedTask;
});
await Test("system installer signer requires both Microsoft Windows and Microsoft Corporation", () =>
{
    SoftwareInstallerNative.RequirePublisherIdentity("Microsoft Windows", "CN=Microsoft Windows, O=Microsoft Corporation", "Microsoft Corporation", "Microsoft Windows");
    Throws(() => SoftwareInstallerNative.RequirePublisherIdentity("Microsoft Windows", "CN=Microsoft Windows, O=Other", "Microsoft Corporation", "Microsoft Windows"));
    Throws(() => SoftwareInstallerNative.RequirePublisherIdentity("Other", "CN=Other, O=Microsoft Corporation", "Microsoft Corporation", "Microsoft Windows"));
    return Task.CompletedTask;
});
await Test("supported MSI tables", () => { Equal("0.0.15", new Tables().Parse().Version); return Task.CompletedTask; });
foreach (var item in new (string Name, Action<Tables> Change)[]
{
    ("wrong MSI product", t => t.Properties.Single(r => r[0] == "ProductName")[1] = "Other"),
    ("per-user MSI", t => t.Properties.Single(r => r[0] == "ALLUSERS")[1] = "2"),
    ("unrecognized product family", t => t.Properties.Single(r => r[0] == "UpgradeCode")[1] = Guid.NewGuid().ToString("B")),
    ("non-MSI version", t => t.Properties.Single(r => r[0] == "ProductVersion")[1] = "0.0.15-beta"),
    ("external file directory", t => t.Directories.Single(r => r[0] == "INSTALLDIR")[1] = "AppDataFolder"),
    ("unsafe file name", t => t.Files[0][2] = "../other.dll"),
    ("unknown executable action", t => t.Actions.Add(["RunClient", "18", "backend", ""])),
    ("unexpected environment change", t => t.Environment[0][2] = "[INSTALLDIR];other"),
    ("unreviewed registry write", t => t.Registry.Add(["x", "2", @"Software\Other", "Foo", "#1", "FirewallRules"])),
    ("unreviewed firewall rule", t => t.Firewall.Add(["x", "Other", "*", "", "6", "[INSTALLDIR]other.exe", "0", "2147483647", "FirewallRules"])),
    ("different upgrade family", t => t.Upgrades[0][0] = Guid.NewGuid().ToString("B")),
    ("wrong component scope", t => t.Components[0][3] = "0"),
    ("missing backend", t => { t.Components.RemoveAt(1); t.Files.RemoveAt(1); }),
    ("duplicate file destinations", t => t.Files[1][2] = "VatEFS.dll"),
    ("invalid file key path", t => t.Components[0][4] = "Unknown")
})
    await Test("reject " + item.Name, () => { var tables = new Tables(); item.Change(tables); Throws(() => tables.Parse()); return Task.CompletedTask; });

await Test("recognize exact machine MSI", async () =>
{
    using var fixture = new Fixture(installed: true);
    var installation = fixture.Adapter.Inspect(fixture.Exe);
    True(installation.CanUpdate); Equal("0.0.12", installation.Version); Equal("AllUsers", installation.Scope);
    await Task.CompletedTask;
});
foreach (var item in new (string Name, Action<Fixture> Change)[]
{
    ("multiple registrations", f => f.ExtraRegistration = true),
    ("component redirected outside root", f => f.ExternalComponent = true),
    ("mismatched cached product", f => f.BadCachedIdentity = true),
    ("cache outside Windows Installer", f => f.BadCachePath = true),
    ("missing backend", f => File.Delete(f.Exe))
})
    await Test("reject installed " + item.Name, () => { using var f = new Fixture(true); item.Change(f); True(!f.Adapter.Inspect(f.Exe).CanUpdate); return Task.CompletedTask; });
await Test("reject custom installed destination", () => { using var f = new Fixture(true); True(!f.Adapter.Inspect(Path.Combine(f.Root, "copy", "efs.exe")).CanUpdate); return Task.CompletedTask; });
await Test("unsupported MSI layout retains independently identified installed version", () =>
{
    using var f = new Fixture(true); f.BadCachedIdentity = true;
    var result = f.Adapter.Inspect(f.Exe); True(!result.CanUpdate); Equal("0.0.12", result.Version);
    return Task.CompletedTask;
});

await Test("verify package without execution", async () => { using var f = new Fixture(false); await f.Adapter.VerifyPackageAsync(f.Release, f.Package, default); Equal(0, f.Runs); });
await Test("reject changed package bytes", async () => { using var f = new Fixture(false); File.AppendAllText(f.Package, "changed"); await ThrowsAsync(() => f.Adapter.VerifyPackageAsync(f.Release, f.Package, default)); Equal(0, f.Runs); });
await Test("reject release source redirect", async () => { using var f = new Fixture(false); await ThrowsAsync(() => f.Adapter.VerifyPackageAsync(f.Release with { DownloadUri = new("https://example.org/evil.msi") }, f.Package, default)); });
await Test("reject MSI release version mismatch", async () => { using var f = new Fixture(false); f.BadIncomingVersion = true; await ThrowsAsync(() => f.Adapter.VerifyPackageAsync(f.Release, f.Package, default)); });

await Test("fresh install invokes quiet elevated MSI without client restart", async () =>
{
    using var f = new Fixture(false);
    var result = await f.Adapter.InstallFreshAsync(f.Release, f.Package, f.Root, null, default, () => Task.CompletedTask);
    Equal(f.Exe, result.ExecutablePath); Equal(1, f.Runs);
    True(f.Command!.Elevate); Equal(f.Msiexec, f.Command.Executable);
    True(f.Command.IsMsi);
    foreach (string arg in new[] { "/qn", "/norestart", "REBOOT=ReallySuppress", "ALLUSERS=1", "MSIRESTARTMANAGERCONTROL=Disable", "MSIDISABLERMRESTART=1", "INSTALLDIR=" + f.Root }) True(f.Command.Arguments.Contains(arg));
    int logging = f.Command.Arguments.ToList().IndexOf("/L*V!");
    True(logging >= 0 && Directory.Exists(Path.GetDirectoryName(f.Command.Arguments[logging + 1])));
    True(f.Command.Arguments[logging + 1].Contains(Path.Combine("SoftwareUpdates", "VatEfsLogs")));
    True(SoftwareInstallerNative.BuildStartInfo(f.Command).Arguments.Contains("INSTALLDIR=\"" + f.Root + "\""));
});
await Test("native wait reports elapsed time and survives display failure without another installer", async () =>
{
    using var f = new Fixture(false);
    var finish = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    var reported = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
    f.NativeWait = finish.Task;
    var run = f.Adapter.InstallFreshAsync(f.Release, f.Package, f.Root, new InlineProgress(message =>
    {
        if (!message.Contains("elapsed")) return;
        reported.TrySetResult(0);
        throw new InvalidOperationException("Synthetic display failure");
    }), default, () => Task.CompletedTask);
    try
    {
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
        True(!run.IsCompleted); Equal(1, f.Runs);
    }
    finally { finish.TrySetResult(0); }
    await run; Equal(1, f.Runs);
});
await Test("installer failure names the retained diagnostic log location", async () =>
{
    using var f = new Fixture(false); f.Exit = 1603;
    try { await f.Adapter.InstallFreshAsync(f.Release, f.Package, f.Root, null, default, () => Task.CompletedTask); }
    catch (IOException error)
    {
        True(error.Message.Contains("1603") && error.Message.Contains("VatEfsLogs") && error.Message.Contains("install.log"));
        return;
    }
    throw new Exception("Expected MSI failure");
});
foreach (var item in new (string Name, Action<Fixture> Change)[]
{
    ("EuroScope already running", f => f.EuroScope = true),
    ("backend already running", f => f.Backend = true),
    ("unknown process state", f => f.UnknownProcesses = true),
    ("existing directory even empty", f => Directory.CreateDirectory(f.Root)),
    ("existing file at destination", f => File.WriteAllText(f.Root, "residue")),
    ("existing registration", f => f.RegisteredVersion = "0.0.12")
})
    await Test("fresh blocks " + item.Name, async () => { using var f = new Fixture(false); item.Change(f); await ThrowsAsync(() => f.Adapter.InstallFreshAsync(f.Release, f.Package, f.Root, null, default, () => Task.CompletedTask)); Equal(0, f.Runs); });
await Test("fresh rechecks after confirmation callback", async () =>
{
    using var f = new Fixture(false);
    await ThrowsAsync(() => f.Adapter.InstallFreshAsync(f.Release, f.Package, f.Root, null, default, () => { f.EuroScope = true; return Task.CompletedTask; })); Equal(0, f.Runs);
});
await Test("fresh rechecks after installing progress callback", async () =>
{
    using var f = new Fixture(false);
    await ThrowsAsync(() => f.Adapter.InstallFreshAsync(f.Release, f.Package, f.Root, new InlineProgress(_ => f.Backend = true), default, () => Task.CompletedTask)); Equal(0, f.Runs);
});
await Test("fresh refuses an untrusted system installer", async () =>
{
    using var f = new Fixture(false); f.BadSystemTrust = true;
    await ThrowsAsync(() => f.Adapter.InstallFreshAsync(f.Release, f.Package, f.Root, null, default, () => Task.CompletedTask)); Equal(0, f.Runs);
});
await Test("fresh rechecks processes after system-installer verification", async () =>
{
    using var f = new Fixture(false); f.StartDuringTrust = true;
    await ThrowsAsync(() => f.Adapter.InstallFreshAsync(f.Release, f.Package, f.Root, null, default, () => Task.CompletedTask)); Equal(0, f.Runs);
});
await Test("fresh rechecks destination after callback", async () =>
{
    using var f = new Fixture(false);
    await ThrowsAsync(() => f.Adapter.InstallFreshAsync(f.Release, f.Package, f.Root, null, default, () => { Directory.CreateDirectory(f.Root); return Task.CompletedTask; })); Equal(0, f.Runs);
});
await Test("fresh cancellation before apply runs no installer", async () =>
{
    using var f = new Fixture(false); using var cancellation = new CancellationTokenSource();
    await ThrowsAsync(() => f.Adapter.InstallFreshAsync(f.Release, f.Package, f.Root, null, cancellation.Token, () => { cancellation.Cancel(); return Task.CompletedTask; })); Equal(0, f.Runs);
});
await Test("package cannot change during confirmation", async () =>
{
    using var f = new Fixture(false);
    await ThrowsAsync(() => f.Adapter.InstallFreshAsync(f.Release, f.Package, f.Root, null, default, () => { File.AppendAllText(f.Package, "changed"); return Task.CompletedTask; })); Equal(0, f.Runs);
});
await Test("successful MSI 3010 remains latched", async () =>
{
    using var f = new Fixture(false); f.Exit = 3010;
    await f.Adapter.InstallFreshAsync(f.Release, f.Package, f.Root, null, default, () => Task.CompletedTask);
    True(f.Adapter.RestartRequired); await ThrowsAsync(() => f.Adapter.VerifyPackageAsync(f.Release, f.Package, default));
});
await Test("failed postcheck still latches MSI 3010", async () =>
{
    using var f = new Fixture(false); f.Exit = 3010; f.BadPostVersion = true;
    await ThrowsAsync(() => f.Adapter.InstallFreshAsync(f.Release, f.Package, f.Root, null, default, () => Task.CompletedTask)); True(f.Adapter.RestartRequired); Equal(1, f.Runs);
});
await Test("cancelled UAC leaves no successful result", async () =>
{
    using var f = new Fixture(false); f.Exit = 1223;
    await ThrowsAsync(() => f.Adapter.InstallFreshAsync(f.Release, f.Package, f.Root, null, default, () => Task.CompletedTask)); True(!f.Adapter.RestartRequired);
});
await Test("update requires completed full backup", async () => { using var f = new Fixture(true); await ThrowsAsync(() => f.Adapter.InstallAsync(f.Adapter.Inspect(f.Exe), f.Release, f.Package, null)); Equal(0, f.Runs); });
await Test("update preserves complete recovery copy and custom data", async () =>
{
    using var f = new Fixture(true); var installation = f.Adapter.Inspect(f.Exe);
    string folder = (await f.Adapter.BackupAsync(installation, null, default))!;
    True(File.Exists(Path.Combine(folder, "Files", "0000", "personal.txt")));
    Equal("personal fixture only", File.ReadAllText(Path.Combine(folder, "Files", "0000", "personal.txt")));
    await f.Adapter.InstallAsync(installation, f.Release, f.Package, null); Equal(1, f.Runs);
    Equal("personal fixture only", File.ReadAllText(Path.Combine(f.Root, "personal.txt")));
});
await Test("update rejects changed installed configuration after backup", async () =>
{
    using var f = new Fixture(true); var installation = f.Adapter.Inspect(f.Exe);
    await f.Adapter.BackupAsync(installation, null, default); File.AppendAllText(Path.Combine(f.Root, "personal.txt"), "changed");
    await ThrowsAsync(() => f.Adapter.InstallAsync(installation, f.Release, f.Package, null)); Equal(0, f.Runs);
});
await Test("update rejects corrupt recovery copy", async () =>
{
    using var f = new Fixture(true); var installation = f.Adapter.Inspect(f.Exe);
    string folder = (await f.Adapter.BackupAsync(installation, null, default))!;
    File.AppendAllText(Path.Combine(folder, "Files", "0000", "personal.txt"), "changed");
    await ThrowsAsync(() => f.Adapter.InstallAsync(installation, f.Release, f.Package, null)); Equal(0, f.Runs);
});
await Test("update rechecks EuroScope after installing progress", async () =>
{
    using var f = new Fixture(true); var installation = f.Adapter.Inspect(f.Exe);
    await f.Adapter.BackupAsync(installation, null, default);
    await ThrowsAsync(() => f.Adapter.InstallAsync(installation, f.Release, f.Package, new InlineProgress(_ => f.EuroScope = true))); Equal(0, f.Runs);
});
await Test("update rejects same-version application", async () =>
{
    using var f = new Fixture(true); var installation = f.Adapter.Inspect(f.Exe);
    await f.Adapter.BackupAsync(installation, null, default);
    await ThrowsAsync(() => f.Adapter.InstallAsync(installation, f.Release with { Version = installation.Version }, f.Package, null)); Equal(0, f.Runs);
});
await Test("backup blocked while EuroScope is running", async () =>
{
    using var f = new Fixture(true); var installation = f.Adapter.Inspect(f.Exe); f.EuroScope = true;
    await ThrowsAsync(() => f.Adapter.BackupAsync(installation, null, default)); Equal(0, f.Runs);
});

Console.WriteLine($"{passed} passed, {failed} failed.");
Environment.ExitCode = failed == 0 ? 0 : 1;
async Task Test(string name, Func<Task> test)
{
    try { await test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); }
}
static void True(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
static void Throws(Action action) { try { action(); } catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException) { return; } throw new Exception("Expected rejection."); }
static async Task ThrowsAsync(Func<Task> action) { try { await action(); } catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or OperationCanceledException) { return; } throw new Exception("Expected rejection."); }

sealed class InlineProgress(Action<string> callback) : IProgress<string> { public void Report(string value) => callback(value); }

sealed class Tables
{
    public const string Product = "{ABCDEF12-1234-5678-9012-ABCDEF123456}";
    public List<string[]> Properties = [["ProductCode", Product], ["ProductVersion", "0.0.15"], ["ProductName", "VatEFS"], ["Manufacturer", "Martin Insulander"], ["UpgradeCode", VatEfsInstallationProbe.UpgradeCode], ["ALLUSERS", "1"], ["WIXUI_INSTALLDIR", "INSTALLDIR"], ["ProductLanguage", "1033"]];
    public List<string[]> Directories = [["TARGETDIR", "", "SourceDir"], ["ProgramFiles64Folder", "TARGETDIR", "."], ["INSTALLDIR", "ProgramFiles64Folder", "VatEFS"]];
    public List<string[]> Components = [["plugin", "{12345678-1234-1234-1234-111111111111}", "INSTALLDIR", "256", "dll"], ["backend", "{12345678-1234-1234-1234-333333333333}", "INSTALLDIR", "256", "exe"], ["ApplicationFiles", VatEfsInstallationProbe.ReadmeComponent, "INSTALLDIR", "256", "readme"]];
    public List<string[]> Files = [["dll", "plugin", "VatEFS.dll", "3"], ["exe", "backend", "efs.exe", "3"], ["readme", "ApplicationFiles", "README.txt", "3"]];
    public List<string[]> Actions = [];
    public List<string[]> Environment = [["Environment", "=-*PATH", "[INSTALLDIR];[~]", "ApplicationFiles"]];
    public List<string[]> Registry = [], Firewall = [];
    public List<string[]> Upgrades = [[VatEfsInstallationProbe.UpgradeCode, "", "0.0.15", "513", "WIX_UPGRADE_DETECTED"], [VatEfsInstallationProbe.UpgradeCode, "0.0.15", "", "2", "WIX_DOWNGRADE_DETECTED"], [VatEfsInstallationProbe.UpgradeCode, "0.0.0", "0.0.15", "256", "OLDERVERSIONBEINGUPGRADED"]];
    public VatEfsMsiPackage Parse() => VatEfsMsiPackageReader.Parse(Properties, Directories, Components, Files, Actions, Environment, Registry, Firewall, Upgrades);
}

sealed class Fixture : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "Launchpad-VatEfsInstaller-tests-" + Guid.NewGuid().ToString("N"));
    public string Root, Exe, Package, Msiexec;
    private readonly string _cache;
    public string? RegisteredVersion;
    public bool ExtraRegistration, ExternalComponent, BadCachedIdentity, BadCachePath, BadIncomingVersion, EuroScope, Backend, UnknownProcesses, BadPostVersion, BadSystemTrust, StartDuringTrust;
    public int Runs, Exit;
    public Task? NativeWait;
    public SoftwareInstallCommand? Command;
    public VatEfsInstaller Adapter;
    public SoftwareRelease Release;
    public Fixture(bool installed)
    {
        Root = Path.Combine(_base, "Program Files", "VatEFS"); Exe = Path.Combine(Root, "efs.exe");
        Package = Path.Combine(_base, "download", "vatefs-0.0.15.msi"); Msiexec = Path.Combine(_base, "Windows", "System32", "msiexec.exe");
        _cache = Path.Combine(_base, "Windows", "Installer", "cached.msi");
        Directory.CreateDirectory(Path.GetDirectoryName(Root)!);
        foreach (string path in new[] { Package, Msiexec, _cache }) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "synthetic package only"); }
        if (installed) { RegisteredVersion = "0.0.12"; Materialize(); File.WriteAllText(Path.Combine(Root, "personal.txt"), "personal fixture only"); }
        Release = new(SoftwareApp.VatEfs, "0.0.15", new("https://github.com/minsulander/vatefs/releases/download/v0.0.15/vatefs-0.0.15.msi"), "vatefs-0.0.15.msi", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Package))), new FileInfo(Package).Length) { IsPrerelease = true };
        Adapter = new(new VatEfsInstallerEnvironment
        {
            ProgramFiles = Path.Combine(_base, "Program Files"), LocalAppData = Path.Combine(_base, "LocalAppData"), WindowsDirectory = Path.Combine(_base, "Windows"), MsiexecPath = Msiexec,
            ReadRegistrations = () => RegisteredVersion == null ? [] : Enumerable.Repeat(new VatEfsMsiRegistration(Tables.Product, "VatEFS", "Martin Insulander", RegisteredVersion, Path.Combine(Root, "README.txt")), ExtraRegistration ? 2 : 1).ToArray(),
            CachedPackage = _ => BadCachePath ? Package : _cache,
            PackageMetadata = path => new Tables().Parse() with { Version = path == _cache ? RegisteredVersion! : BadIncomingVersion ? "0.0.14" : "0.0.15", ProductCode = path == _cache && BadCachedIdentity ? Guid.NewGuid().ToString("B") : Tables.Product },
            ComponentPath = (_, component) => Path.Combine(ExternalComponent ? _base : Root, new Tables().Parse().Components.Single(c => c.Id == component).KeyPath!),
            EuroScopeBlockingReason = () => { if (UnknownProcesses) throw new InvalidOperationException("fixture enumeration failure"); return EuroScope ? "EuroScope running (fixture)" : null; },
            BackendRunning = () => Backend, VerifyInstalledBinary = (_, _) => { }, VerifyMsiexec = path => { if (path != Msiexec) throw new Exception("Wrong system installer."); if (BadSystemTrust) throw new InvalidDataException("Untrusted fixture installer."); if (StartDuringTrust) EuroScope = true; },
            CreatePrivateDirectory = path => Directory.CreateDirectory(path),
            InstallationProgressInterval = TimeSpan.FromMilliseconds(10),
            RunAsync = async command => { Runs++; Command = command; if (NativeWait != null) await NativeWait; if (Exit is 0 or 3010) { RegisteredVersion = BadPostVersion ? "0.0.14" : "0.0.15"; Materialize(); } return Exit; }
        });
    }
    private void Materialize() { Directory.CreateDirectory(Root); foreach (string name in new[] { "efs.exe", "VatEFS.dll", "README.txt" }) File.WriteAllText(Path.Combine(Root, name), "abc"); }
    public void Dispose()
    {
        string full = Path.GetFullPath(_base), temp = Path.GetFullPath(Path.GetTempPath());
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("Launchpad-VatEfsInstaller-tests-", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe fixture cleanup root.");
        Directory.Delete(full, recursive: true);
    }
}
