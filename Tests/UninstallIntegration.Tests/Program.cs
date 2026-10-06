using System.Diagnostics;
using System.Security.Cryptography;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;
using Service = VatscaUpdateChecker.Services.LaunchpadUninstallService;

internal static class Program
{
    private static int _passed;

    private static async Task Main()
    {
        Run("only the exact uninstall request enters maintenance", () =>
        {
            Assert(Service.IsUninstallRequested([Service.UninstallArgument]));
            Assert(!Service.IsUninstallRequested([]));
            Assert(!Service.IsUninstallRequested([Service.UninstallArgument, "--silent"]));
            Assert(!Service.IsUninstallRequested([Service.UninstallArgument.ToUpperInvariant()]));
            Assert(Service.IsFinalizerRequested([Service.FinalizerArgument, "ticket"]));
            Assert(!Service.IsFinalizerRequested([Service.FinalizerArgument]));
        });
        WithFixture("fresh native registration becomes interactive wrapper", f =>
        {
            Assert(Service.TryRegister(f.Environment(), out var error), error);
            Equal(1, f.Writes);
            Equal(Service.WrapperCommand(f.Main), f.Registration!.UninstallString);
            Equal(Service.NativeCommand(f.Updater, true), f.Registration.QuietUninstallString);
            Equal(0, f.Starts.Count);
        });
        WithFixture("registration repair is idempotent", f =>
        {
            Assert(Service.TryRegister(f.Environment(), out _));
            Assert(Service.TryRegister(f.Environment(), out _));
            Equal(2, f.Writes);
            Assert(Service.CanUninstall(f.Environment(), out _));
            Equal(0, f.Starts.Count);
        });
        WithFixture("updater rewriting native registration can be repaired", f =>
        {
            Assert(Service.TryRegister(f.Environment(), out _));
            f.Registration = f.Registration! with { UninstallString = Service.NativeCommand(f.Updater) };
            Assert(Service.TryRegister(f.Environment(), out _));
            Equal(Service.WrapperCommand(f.Main), f.Registration!.UninstallString);
        });
        WithFixture("portable copy cannot register or uninstall", f => { f.Installation = f.Installation! with { Portable = true }; Rejected(f); });
        WithFixture("development copy cannot register or uninstall", f => { f.Installation = null; Rejected(f); });
        WithFixture("different package identity is rejected", f => { f.Installation = f.Installation! with { AppId = "other" }; Rejected(f); });
        WithFixture("executable outside current is rejected", f => { f.Installation = f.Installation! with { Executable = f.Updater }; Rejected(f); });
        WithFixture("different updater path is rejected", f => { f.Installation = f.Installation! with { Updater = f.Main }; Rejected(f); });
        WithFixture("missing updater is rejected", f => { File.Delete(f.Updater); Rejected(f); });
        WithFixture("missing executable is rejected", f => { File.Delete(f.Main); Rejected(f); });
        WithFixture("framework-dependent copies cannot use independent helper", f => { File.WriteAllText(Path.ChangeExtension(f.Main, ".runtimeconfig.json"), "{}"); Rejected(f); });
        WithFixture("missing Windows registration is rejected", f => { f.Registration = null; Rejected(f); });
        WithFixture("registration pointing at another root is rejected", f => { f.Registration = f.Registration! with { InstallLocation = f.Storage }; Rejected(f); });
        WithFixture("wrong registered product title is rejected", f => { f.Registration = f.Registration! with { DisplayName = "Other" }; Rejected(f); });
        WithFixture("changed interactive command is not overwritten", f => { f.Registration = f.Registration! with { UninstallString = "malicious.exe" }; Rejected(f); });
        WithFixture("changed quiet command is not overwritten", f => { f.Registration = f.Registration! with { QuietUninstallString = Service.NativeCommand(f.Updater) }; Rejected(f); });
        WithFixture("manifest must match exact package identity", f => { f.Manifest("other", "1.5.0"); Rejected(f); });
        WithFixture("manifest must match exact installed version", f => { f.Manifest(Service.PackageId, "1.5.1"); Rejected(f); });
        WithFixture("manifest duplicate identity is rejected", f => { File.WriteAllText(f.ManifestPath, "<package><metadata><id>SwedenFirLaunchpad</id><id>SwedenFirLaunchpad</id><version>1.5.0</version></metadata></package>"); Rejected(f); });
        WithFixture("manifest external entities are prohibited", f => { File.WriteAllText(f.ManifestPath, "<!DOCTYPE package [<!ENTITY x SYSTEM 'file:///C:/not-read'>]><package><metadata><id>&x;</id><version>1.5.0</version></metadata></package>"); Rejected(f); });
        WithFixture("oversized manifest is rejected", f => { File.WriteAllText(f.ManifestPath, new string('x', 65537)); Rejected(f); });
        WithFixture("namespace-bearing valid manifest works", f =>
        {
            File.WriteAllText(f.ManifestPath, "<package xmlns='http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd'><metadata><id>SwedenFirLaunchpad</id><version>1.5.0</version></metadata></package>");
            Assert(Service.TryRegister(f.Environment(), out _));
        });
        WithFixture("registry access errors fail closed", f =>
        {
            Assert(!Service.TryRegister(f.Environment(write: (_, _) => throw new UnauthorizedAccessException("denied")), out var error));
            Assert(error == "denied");
            Equal(0, f.Starts.Count);
        });
        WithFixture("uninstall requires a held maintenance lease", f =>
        {
            Throws(() => Service.Uninstall(f.Environment(), false));
            Equal(0, f.Starts.Count);
        });
        WithFixture("explicit uninstall prepares a validated handoff without starting native removal", f =>
        {
            Service.Uninstall(f.Environment(), true);
            Equal(1, f.Handoffs.Count);
            Equal(f.Installation, f.Handoffs.Single());
            Equal(0, f.Starts.Count);
        });
        var configuredFields = new (string Name, Action<AppSettings, string> Set)[]
        {
            ("EuroScope executable", (settings, path) => settings.EuroscopeExePath = path),
            ("EuroScope/GNG data", (settings, path) => settings.EuroscopeDataPath = path),
            ("last EuroScope profile", (settings, path) => settings.LastEuroscopeProfile = path),
            ("TrackAudio executable", (settings, path) => settings.TrackAudioExePath = path),
            ("VACS executable", (settings, path) => settings.VacsExePath = path),
            ("vATIS executable", (settings, path) => settings.VatisExePath = path),
            ("VatEFS path", (settings, path) => settings.VatEfsPath = path)
        };
        foreach (var field in configuredFields)
            WithFixture("managed-root preservation blocks configured " + field.Name, f =>
            {
                var settings = new AppSettings(); field.Set(settings, Path.Combine(f.Root, "portable", "missing.exe"));
                Throws(() => Service.ValidateConfiguredPaths(settings, f.Environment()));
                Throws(() => Service.Uninstall(f.Environment(), true, settings));
                Equal(0, f.Handoffs.Count); Equal(0, f.Starts.Count);
            });
        WithFixture("missing executable with remaining portable settings cannot be removed implicitly", f =>
        {
            var folder = Path.Combine(f.Root, "portable-client"); Directory.CreateDirectory(folder);
            var data = Path.Combine(folder, "settings.json"); File.WriteAllText(data, "synthetic settings");
            var settings = new AppSettings { VacsExePath = Path.Combine(folder, "missing.exe") };
            Throws(() => Service.Uninstall(f.Environment(), true, settings));
            Equal("synthetic settings", File.ReadAllText(data)); Equal(0, f.Handoffs.Count);
        });
        WithFixture("managed root equality is blocked", f =>
        {
            Throws(() => Service.ValidateConfiguredPaths(new() { EuroscopeDataPath = f.Root }, f.Environment()));
        });
        WithFixture("case and relative segments cannot hide an in-root configured path", f =>
        {
            var path = Path.Combine(f.Root.ToUpperInvariant(), "current", "..", "profiles");
            Throws(() => Service.ValidateConfiguredPaths(new() { EuroscopeDataPath = path }, f.Environment()));
        });
        WithFixture("path-prefix sibling is preserved and does not block uninstall", f =>
        {
            var sibling = f.Root + "-other-client"; Directory.CreateDirectory(sibling);
            var file = Path.Combine(sibling, "client.exe"); File.WriteAllText(file, "synthetic client");
            Service.Uninstall(f.Environment(), true, new() { TrackAudioExePath = file });
            Equal(1, f.Handoffs.Count); Equal("synthetic client", File.ReadAllText(file));
        });
        WithFixture("outside stale configured path can remain without removing outside data", f =>
        {
            var path = Path.Combine(f.DirectoryPath, "outside", "not-created", "missing.exe");
            Service.Uninstall(f.Environment(), true, new() { VacsExePath = path });
            Equal(1, f.Handoffs.Count);
        });
        WithFixture("uninstall revalidates settings changed after review", f =>
        {
            var settings = new AppSettings(); Service.ValidateConfiguredPaths(settings, f.Environment());
            settings.EuroscopeDataPath = Path.Combine(f.Root, "GNG");
            Throws(() => Service.Uninstall(f.Environment(), true, settings)); Equal(0, f.Handoffs.Count);
        });
        WithFixture("existing recovery export inside root blocks native handoff", f =>
        {
            var export = Path.Combine(f.Root, "recovery"); Directory.CreateDirectory(export);
            File.WriteAllText(Path.Combine(export, "snapshot.txt"), "exported synthetic data");
            Throws(() => Service.ValidateConfiguredPaths(new(), f.Environment(), export));
            Throws(() => Service.Uninstall(f.Environment(), true, new(), export));
            Equal(0, f.Handoffs.Count); Assert(File.Exists(Path.Combine(export, "snapshot.txt")));
        });
        WithFixture("not-yet-created intended recovery folder inside root is blocked", f =>
        {
            var export = Path.Combine(f.Root, "not-created", "future-export");
            Throws(() => Service.ValidateConfiguredPaths(new(), f.Environment(), export));
            Equal(0, f.Handoffs.Count);
        });
        WithFixture("every selected and completed recovery path is checked", f =>
        {
            Throws(() => Service.ValidateConfiguredPaths(new(), f.Environment(), f.Storage, null, Path.Combine(f.Root, "export")));
        });
        WithFixture("outside recovery export remains available through handoff", f =>
        {
            var export = Path.Combine(f.Storage, "recovery"); Directory.CreateDirectory(export);
            var snapshot = Path.Combine(export, "snapshot.txt"); File.WriteAllText(snapshot, "kept");
            Service.ValidateConfiguredPaths(new(), f.Environment(), export);
            Service.Uninstall(f.Environment(), true, new(), export);
            Equal(1, f.Handoffs.Count); Equal("kept", File.ReadAllText(snapshot));
        });
        WithFixture("outside junction alias into managed root blocks configured files", f =>
        {
            var alias = Path.Combine(f.DirectoryPath, "linked-client"); SyntheticJunction.Create(alias, f.Root);
            try { Throws(() => Service.ValidateConfiguredPaths(new() { VacsExePath = Path.Combine(alias, "current", Service.MainExeName) }, f.Environment())); }
            finally { Directory.Delete(alias, false); }
        });
        WithFixture("outside junction alias blocks a future export inside managed root", f =>
        {
            var alias = Path.Combine(f.DirectoryPath, "linked-recovery"); SyntheticJunction.Create(alias, f.Root);
            try { Throws(() => Service.ValidateConfiguredPaths(new(), f.Environment(), Path.Combine(alias, "not-created", "export"))); }
            finally { Directory.Delete(alias, false); }
        });
        WithFixture("dangling directory aliases fail closed", f =>
        {
            var alias = Path.Combine(f.DirectoryPath, "dangling"); SyntheticJunction.Create(alias, Path.Combine(f.DirectoryPath, "missing"));
            try { Throws(() => Service.ValidateConfiguredPaths(new(), f.Environment(), Path.Combine(alias, "export"))); }
            finally { Directory.Delete(alias, false); }
        });
        WithFixture("ambiguous or nonlocal configured paths require manual review", f =>
        {
            foreach (var path in new[] { @"relative\client.exe", @"\\server\share\client.exe", f.Root + ".\\client.exe", f.Root + "\\client.exe:stream" })
                Throws(() => Service.ValidateConfiguredPaths(new() { VacsExePath = path }, f.Environment()));
        });
        WithFixture("completed handoff starts only the validated native helper", f =>
        {
            Service.CompleteUninstall(f.Ticket(), f.Environment());
            Equal(1, f.Starts.Count);
            var start = f.Starts.Single();
            Equal(f.Updater, start.FileName);
            Equal(f.Root, start.WorkingDirectory);
            Equal("--uninstall", start.ArgumentList.Single());
            Assert(!start.UseShellExecute);
            Assert(File.Exists(f.Main));
            Equal(0, f.Writes);
        });
        WithFixture("native launch failure is returned without deletion", f =>
        {
            Throws(() => Service.CompleteUninstall(f.Ticket(), f.Environment(start: _ => throw new IOException("failed"))));
            Assert(File.Exists(f.Main));
        });
        WithFixture("commands quote paths with spaces", f =>
        {
            Equal('"' + f.Main + "\" --uninstall-launchpad", Service.WrapperCommand(f.Main));
            Equal('"' + f.Updater + "\" --uninstall --silent", Service.NativeCommand(f.Updater, true));
            Throws(() => Service.WrapperCommand(f.Main + "\" --bad"));
        });
        Run("unsafe installation roots are refused", () =>
        {
            foreach (string path in new[] { "", @"relative\path", @"C:\", @"\\server\share\app", @"\\?\C:\app" }) Throws(() => Service.LocalPath(path));
        });
        WithFixture("valid finalizer ticket accepts completed exact installation", f =>
        {
            Service.CompleteRegistration(f.Ticket(), f.Environment());
            Equal(1, f.Writes);
            Equal(0, f.Starts.Count);
        });
        WithFixture("finalizer refuses executable changed after preparation", f =>
        {
            var ticket = f.Ticket(); File.WriteAllText(f.Main, "replaced");
            Throws(() => Service.CompleteRegistration(ticket, f.Environment()));
            Equal(0, f.Writes);
        });
        WithFixture("uninstall handoff refuses changed executable", f =>
        {
            var ticket = f.Ticket(); File.WriteAllText(f.Main, "replaced");
            Throws(() => Service.CompleteUninstall(ticket, f.Environment()));
            Equal(0, f.Starts.Count);
        });
        WithFixture("finalizer refuses newer installation manifest", f =>
        {
            var ticket = f.Ticket(); f.Manifest(Service.PackageId, "1.5.1");
            Throws(() => Service.CompleteRegistration(ticket, f.Environment()));
            Equal(0, f.Writes);
        });
        WithFixture("finalizer refuses missing or replaced registration", f =>
        {
            var ticket = f.Ticket(); f.Registration = null;
            Throws(() => Service.CompleteRegistration(ticket, f.Environment()));
            Equal(0, f.Writes);
        });
        WithFixture("invalid and expired finalizer tickets fail closed", f =>
        {
            var valid = f.Ticket(); var now = DateTime.UtcNow;
            Service.ValidateTicket(valid, now);
            foreach (var ticket in new[] {
                valid with { ExpiresUtc = now.AddSeconds(-1) }, valid with { ExpiresUtc = now.AddMinutes(7) },
                valid with { ParentId = 0 }, valid with { ParentStartedUtcTicks = 0 },
                valid with { ReadyEvent = "other" }, valid with { ReadyEvent = null! },
                valid with { ExecutableHash = new string('x', 64) }, valid with { ExecutableHash = null! },
                valid with { Version = "bad" }, valid with { Root = @"C:\" }
            }) Throws(() => Service.ValidateTicket(ticket, now));
            Equal(0, f.Writes);
        });
        WithFixture("finalizer keeps executable locked through registry write", f =>
        {
            Service.CompleteRegistration(f.Ticket(), f.Environment(write: (_, _) => Throws(() => File.WriteAllText(f.Main, "race"))));
            Equal("synthetic executable", File.ReadAllText(f.Main));
        });
        WithFixture("completed helper cleanup leaves pending and unknown files", f =>
        {
            string completed = f.HelperFolder(true), pending = f.HelperFolder(false), unexpected = f.HelperFolder(true);
            File.WriteAllText(Path.Combine(unexpected, "keep.txt"), "user data");
            string named = Path.Combine(f.Storage, "not-owned"); Directory.CreateDirectory(named); File.WriteAllText(Path.Combine(named, "completed"), "done");
            Service.CleanupCompletedFinalizers(f.Storage);
            Assert(!Directory.Exists(completed));
            Assert(Directory.Exists(pending)); Assert(Directory.Exists(unexpected)); Assert(Directory.Exists(named));
        });
        WithFixture("busy completed helper is retained for later cleanup", f =>
        {
            string folder = f.HelperFolder(true);
            using (var handle = new FileStream(Path.Combine(folder, Service.MainExeName), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Service.CleanupCompletedFinalizers(f.Storage);
                Assert(File.Exists(Path.Combine(folder, "registration.json")));
            }
            Service.CleanupCompletedFinalizers(f.Storage);
            Assert(!Directory.Exists(folder));
        });

        await RunAsync("lease excludes another operation and releases after await", async () =>
        {
            string name = LockName();
            Assert(MaintenanceLock.TryAcquire(name, out var first));
            Assert(MaintenanceLock.IsHeldByCurrentProcess);
            Assert(!MaintenanceLock.TryAcquire(name, out var second)); Assert(second is null);
            await Task.Run(() => { first!.Dispose(); first.Dispose(); });
            Assert(!MaintenanceLock.IsHeldByCurrentProcess);
            Assert(MaintenanceLock.TryAcquire(name, out var next)); next!.Dispose();
        });
        Run("named OS mutex contention fails without queuing", () =>
        {
            string name = LockName();
            using var mutex = new Mutex(true, name);
            Assert(!MaintenanceLock.TryAcquire(name, out var lease)); Assert(lease is null);
            mutex.ReleaseMutex();
            Assert(MaintenanceLock.TryAcquire(name, out lease)); lease!.Dispose();
        });
        Run("abandoned OS mutex can be recovered", () =>
        {
            string name = LockName();
            using var retained = new Mutex(false, name);
            var thread = new Thread(() => retained.WaitOne()); thread.Start(); thread.Join();
            Assert(MaintenanceLock.TryAcquire(name, out var lease)); lease!.Dispose();
            Assert(!MaintenanceLock.IsHeldByCurrentProcess);
        });
        Run("invalid mutex name fails closed", () => Assert(!MaintenanceLock.TryAcquire("bad\\name\\bad", out _)));
        Run("uninstall handoff remains active for its holder lifetime", () =>
        {
            string name = LockName();
            Assert(!MaintenanceLock.HandoffActive(name));
            using (var handoff = MaintenanceLock.BeginUninstallHandoff(name))
            {
                Assert(MaintenanceLock.HandoffActive(name));
                Assert(!MaintenanceLock.TryAcquire(name, out var lease)); Assert(lease is null);
            }
            Assert(!MaintenanceLock.HandoffActive(name));
            Assert(MaintenanceLock.TryAcquire(name, out var resumed)); resumed!.Dispose();
        });
        Console.WriteLine($"Passed {_passed} uninstall integration checks. No network, real registry, installer or application execution.");
    }

    private static string LockName() => @"Local\Launchpad.UninstallIntegration.Tests." + Guid.NewGuid().ToString("N");
    private static void Rejected(Fixture f)
    {
        Assert(!Service.CanUninstall(f.Environment(), out var reason)); Assert(!string.IsNullOrWhiteSpace(reason));
        Assert(!Service.TryRegister(f.Environment(), out _));
        Throws(() => Service.Uninstall(f.Environment(), true));
        Equal(0, f.Writes); Equal(0, f.Starts.Count); Equal(0, f.Handoffs.Count);
    }
    private static void WithFixture(string name, Action<Fixture> test) => Run(name, () => { using var fixture = new Fixture(); test(fixture); });
    private static void Run(string name, Action test) { test(); _passed++; Console.WriteLine("PASS " + name); }
    private static async Task RunAsync(string name, Func<Task> test) { await test(); _passed++; Console.WriteLine("PASS " + name); }
    private static void Assert(bool value, string? message = null) { if (!value) throw new Exception(message ?? "Assertion failed."); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
    private static void Throws(Action action)
    {
        try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or System.Xml.XmlException) { return; }
        throw new Exception("Expected a validation or I/O failure.");
    }

    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "Launchpad-uninstall-tests", Guid.NewGuid().ToString("N"));
        public string Root => Path.Combine(DirectoryPath, "installation with spaces");
        public string Main => Path.Combine(Root, "current", Service.MainExeName);
        public string Updater => Path.Combine(Root, "Update.exe");
        public string ManifestPath => Path.Combine(Root, "current", "sq.version");
        public string Storage => Path.Combine(DirectoryPath, "registration-cache");
        public Service.ManagedInstallation? Installation;
        public Service.Registration? Registration;
        public int Writes;
        public List<ProcessStartInfo> Starts { get; } = [];
        public List<Service.ManagedInstallation> Handoffs { get; } = [];
        public Fixture()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Main)!); Directory.CreateDirectory(Storage);
            File.WriteAllText(Main, "synthetic executable"); File.WriteAllText(Updater, "synthetic updater");
            Manifest(Service.PackageId, "1.5.0");
            Installation = new(Service.PackageId, Root, Main, Updater, "1.5.0", false);
            Registration = new(Root, "Sweden FIR Launchpad", Service.NativeCommand(Updater), Service.NativeCommand(Updater, true));
        }
        public void Manifest(string id, string version) => File.WriteAllText(ManifestPath, $"<package><metadata><id>{id}</id><version>{version}</version></metadata></package>");
        public Service.UninstallEnvironment Environment(Action<string, string>? write = null, Action<ProcessStartInfo>? start = null) => new()
        {
            StorageRoot = Storage, Locate = () => Installation, ReadRegistration = () => Registration,
            WriteCommands = write ?? ((interactive, quiet) => { Writes++; Registration = Registration! with { UninstallString = interactive, QuietUninstallString = quiet }; }),
            PrepareHandoff = installation => Handoffs.Add(installation),
            RunNativeUninstall = start ?? (info => Starts.Add(info))
        };
        public Service.RegistrationTicket Ticket() => new(Root, "1.5.0", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Main))), 1234, 5678,
            @"Local\SwedenFirLaunchpad.Finalizer." + Guid.NewGuid().ToString("N"), DateTime.UtcNow.AddMinutes(5));
        public string HelperFolder(bool completed)
        {
            string folder = Path.Combine(Storage, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, Service.MainExeName), "synthetic helper"); File.WriteAllText(Path.Combine(folder, "registration.json"), "{}");
            if (completed) File.WriteAllText(Path.Combine(folder, "completed"), "done");
            return folder;
        }
        public void Dispose()
        {
            string root = Path.GetFullPath(DirectoryPath);
            string expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Launchpad-uninstall-tests")) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture cleanup path.");
            Directory.Delete(root, true);
        }
    }
}
