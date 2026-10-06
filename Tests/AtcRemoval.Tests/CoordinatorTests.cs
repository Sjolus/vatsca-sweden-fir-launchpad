using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

internal static class CoordinatorTests
{
    internal static async Task<int> Run()
    {
        var tests = new (string, Func<Task>)[]
        {
            ("preview never mutates files or credentials", async () =>
            {
                using var f = new Case(); var source = f.File("roaming/VatscaUpdateChecker/settings.json", "FAKE");
                var plan = f.Service.Preview([], new(true, false, true, false, false), f.Recovery);
                Assert(plan.BackupFiles.Files.Count == 1 && File.Exists(source) && f.DeletedCredentials.Count == 0);
                Assert(!Directory.EnumerateFileSystemEntries(f.Recovery).Any()); await Task.CompletedTask;
            }),
            ("selected settings removed after a verified private recovery export", async () =>
            {
                using var f = new Case(); var source = f.File("roaming/VatscaUpdateChecker/settings.json", "FAKE-PRIVATE");
                var other = f.File("roaming/VatscaUpdateChecker/VATIRISProfile/keep", "session");
                var backup = await f.Service.ApplyAsync(f.Service.Preview([], new(true, false, false, false, false), f.Recovery), null, default);
                Assert(!File.Exists(source) && File.Exists(other) && f.DeletedCredentials.Count == 0);
                Assert(File.ReadAllText(Path.Combine(backup!, "Files", "0000", "settings.json")) == "FAKE-PRIVATE");
            }),
            ("only the two explicit Credential Manager targets are deleted", async () =>
            {
                using var f = new Case();
                await f.Service.ApplyAsync(f.Service.Preview([], new(false, false, true, false, false), null), null, default);
                Assert(f.DeletedCredentials.SequenceEqual(new[] { CredentialManagerService.TargetVatsim, CredentialManagerService.TargetHoppie }));
            }),
            ("changed reviewed file prevents every destructive operation", async () =>
            {
                using var f = new Case(); var file = f.File("roaming/VatscaUpdateChecker/settings.json", "old");
                var plan = f.Service.Preview([], new(true, false, true, false, false), f.Recovery);
                File.WriteAllText(file, "new");
                await Reject(() => f.Service.ApplyAsync(plan, null, default));
                Assert(File.ReadAllText(file) == "new" && f.DeletedCredentials.Count == 0);
            }),
            ("cancelled preparation leaves sources and credentials intact", async () =>
            {
                using var f = new Case(); var file = f.File("roaming/VatscaUpdateChecker/settings.json", "keep");
                var plan = f.Service.Preview([], new(true, false, true, false, false), f.Recovery);
                await Reject(() => f.Service.ApplyAsync(plan, null, new CancellationToken(true)));
                Assert(File.Exists(file) && f.DeletedCredentials.Count == 0);
            }),
            ("disposable downloads do not include recovery backups", async () =>
            {
                using var f = new Case(); var folder = "local/VatscaUpdateChecker/SoftwareUpdates/";
                var download = f.File(folder + Guid.NewGuid().ToString("N") + "/package.exe", "download");
                var verification = f.File(folder + "Verification/sample", "verify");
                var backup = f.File(folder + "Backups/vATIS/Files/settings", "keep-backup");
                var unknown = f.File(folder + "unknown/keep", "unknown");
                await f.Service.ApplyAsync(f.Service.Preview([], new(false, false, false, true, false), null), null, default);
                Assert(!File.Exists(download) && !File.Exists(verification) && File.Exists(backup) && File.Exists(unknown));
            }),
            ("existing backups require their own explicit selection", async () =>
            {
                using var f = new Case(); var backup = f.File("local/VatscaUpdateChecker/SoftwareUpdates/Backups/vATIS/Files/settings", "old-backup");
                await f.Service.ApplyAsync(f.Service.Preview([], new(false, false, false, false, true), null), null, default);
                Assert(!File.Exists(backup));
            }),
            ("fresh installer and runtime staging cleanup preserves unknown folders", async () =>
            {
                using var f = new Case();
                var caches = new[] { "EuroScopeInstall", "EuroScope/Prerequisites", "FreshInstalls", "SoftwareUpdates/Prerequisites", "SoftwareUpdates/FreshInstall/vATIS" };
                var staged = caches.Select(cache => f.File("local/VatscaUpdateChecker/" + cache + "/" + Guid.NewGuid().ToString("N") + "/package.exe", "staging")).ToArray();
                var retained = caches.Select(cache => f.File("local/VatscaUpdateChecker/" + cache + "/unknown/keep", "keep")).ToArray();
                await f.Service.ApplyAsync(f.Service.Preview([], new(false, false, false, true, false), null), null, default);
                Assert(staged.All(path => !File.Exists(path)) && retained.All(File.Exists));
            }),
            ("browser sessions cannot be reset while Edge is running", async () =>
            {
                using var f = new Case(); f.File("roaming/VatscaUpdateChecker/VATIRISProfile/session", "session"); f.BrowserRunning = true;
                await Reject(() => Task.FromResult(f.Service.Preview([], new(false, true, false, false, false), null)));
            }),
            ("browser starting after review prevents reset", async () =>
            {
                using var f = new Case(); var file = f.File("roaming/VatscaUpdateChecker/VATIRISProfile/session", "session");
                var plan = f.Service.Preview([], new(false, true, false, false, false), null); f.BrowserRunning = true;
                await Reject(() => f.Service.ApplyAsync(plan, null, default)); Assert(File.Exists(file));
            }),
            ("recovery export cannot overlap a selected folder", async () =>
            {
                using var f = new Case(); f.File("roaming/VatscaUpdateChecker/VATIRISProfile/session", "session");
                await Reject(() => Task.FromResult(f.Service.Preview([], new(false, true, false, false, false), f.PathOf("roaming/VatscaUpdateChecker/VATIRISProfile"))));
            }),
            ("GNG keep-settings removal requires an external export", async () =>
            {
                using var f = new Case(); f.File("gng/ESAA/Settings/personal", "FAKE");
                var target = f.Service.Discover().Single(t => t.Id == AtcRemovalApp.Gng);
                await Reject(() => Task.FromResult(f.Service.Preview([new(target, true, false)], new(false, false, false, false, false), null)));
            }),
            ("GNG full reset affects only reviewed ESAA package paths", async () =>
            {
                using var f = new Case(); var file = f.File("gng/ESAA/Settings/personal", "FAKE");
                var other = f.File("gng/OTHER_FIR/keep", "unrelated"); var rootOther = f.File("gng/my-profile.prf", "keep");
                var target = f.Service.Discover().Single(t => t.Id == AtcRemovalApp.Gng);
                var plan = f.Service.Preview([new(target, true, true)], new(false, false, false, false, false), null);
                await f.Service.ApplyAsync(plan, null, default);
                Assert(!File.Exists(file) && File.Exists(other) && File.Exists(rootOther));
            }),
            ("duplicate application selections are rejected", async () =>
            {
                using var f = new Case(); f.File("gng/ESAA/Settings/personal", "FAKE");
                var target = f.Service.Discover().Single(t => t.Id == AtcRemovalApp.Gng);
                await Reject(() => Task.FromResult(f.Service.Preview([new(target, true, true), new(target, true, true)], new(false, false, false, false, false), null)));
            }),
            ("unsupported application removal cannot be forced through preview", async () =>
            {
                using var f = new Case(); var target = f.Service.Discover().Single(t => t.Id == AtcRemovalApp.EuroScope);
                await Reject(() => Task.FromResult(f.Service.Preview([new(target, true, false)], new(false, false, false, false, false), f.Recovery)));
            }),
            ("GNG settings-only reset is rejected without touching the package", async () =>
            {
                using var f = new Case(); var personal = f.File("gng/ESAA/Settings/personal", "FAKE-PERSONAL");
                var target = f.Service.Discover().Single(t => t.Id == AtcRemovalApp.Gng);
                await Reject(() => Task.FromResult(f.Service.Preview([new(target, false, true)], None, f.Recovery)));
                Assert(File.ReadAllText(personal) == "FAKE-PERSONAL" && !Directory.EnumerateFileSystemEntries(f.Recovery).Any());
            }),
            ("GNG removal cannot silently remove an unselected configured plugin", async () =>
            {
                using var f = new CatalogTests.Fixture();
                f.Settings.EuroscopeDataPath = f.Dir("Gng"); f.Settings.VatEfsPath = f.Dir("Gng/ESAA/Plugins");
                var plugin = f.File("Gng/ESAA/Plugins/VatEFS.dll"); var service = NewService(f);
                await Reject(() => Task.FromResult(service.Preview([new(f.Find(AtcRemovalApp.Gng), true, true)], None, null)));
                Assert(File.Exists(plugin) && f.RunCount == 0);
            }),
            ("GNG removal protects an unselected client reached through an outside junction", async () =>
            {
                using var f = new Case(); var client = f.File("gng/ESAA/client/trackaudio.exe", "FAKE-CLIENT");
                SyntheticNative.Junction(f.PathOf("client-alias"), f.PathOf("gng/ESAA/client"));
                f.Settings.TrackAudioExePath = f.PathOf("client-alias/trackaudio.exe");
                var target = f.Service.Discover().Single(t => t.Id == AtcRemovalApp.Gng);
                await Reject(() => Task.FromResult(f.Service.Preview([new(target, true, true)], None, null)));
                Assert(File.Exists(client) && f.DeletedCredentials.Count == 0);
            }),
            ("browser reset protects missing configured descendants of an outside junction", async () =>
            {
                using var f = new Case(); var retained = f.File("roaming/VatscaUpdateChecker/VATIRISProfile/settings", "FAKE-KEEP");
                SyntheticNative.Junction(f.PathOf("client-alias"), f.PathOf("roaming/VatscaUpdateChecker/VATIRISProfile"));
                f.Settings.VacsExePath = f.PathOf("client-alias/missing/folder/vacs-client.exe");
                await Reject(() => Task.FromResult(f.Service.Preview([], new(false, true, true, false, false), null)));
                Assert(File.Exists(retained) && f.DeletedCredentials.Count == 0);
            }),
            ("apply rechecks a configured junction retargeted after preview", async () =>
            {
                using var f = new Case(); var retained = f.File("roaming/VatscaUpdateChecker/VATIRISProfile/settings", "FAKE-KEEP");
                f.File("outside/keep", "FAKE-OUTSIDE");
                var alias = f.PathOf("client-alias"); SyntheticNative.Junction(alias, f.PathOf("outside"));
                f.Settings.VacsExePath = f.PathOf("client-alias/missing/vacs-client.exe");
                var plan = f.Service.Preview([], new(false, true, true, false, false), null);
                Directory.Delete(alias, recursive: false);
                SyntheticNative.Junction(alias, f.PathOf("roaming/VatscaUpdateChecker/VATIRISProfile"));
                await Reject(() => f.Service.ApplyAsync(plan, null, default));
                Assert(File.Exists(retained) && f.DeletedCredentials.Count == 0);
            }),
            ("unrelated configured aliases and missing custom paths permit reviewed reset", async () =>
            {
                using var f = new Case(); var removed = f.File("roaming/VatscaUpdateChecker/VATIRISProfile/settings", "FAKE-REMOVE");
                var retained = f.File("outside/keep", "FAKE-KEEP");
                SyntheticNative.Junction(f.PathOf("client-alias"), f.PathOf("outside"));
                f.Settings.VacsExePath = f.PathOf("client-alias/missing/vacs-client.exe");
                f.Settings.TrackAudioExePath = f.PathOf("custom/missing/trackaudio.exe");
                await f.Service.ApplyAsync(f.Service.Preview([], new(false, true, false, false, false), null), null, default);
                Assert(!File.Exists(removed) && File.Exists(retained));
            }),
            ("dangling configured junctions stop removal before credentials or files change", async () =>
            {
                using var f = new Case(); var retained = f.File("roaming/VatscaUpdateChecker/VATIRISProfile/settings", "FAKE-KEEP");
                SyntheticNative.Junction(f.PathOf("client-alias"), f.PathOf("missing"));
                f.Settings.VacsExePath = f.PathOf("client-alias/vacs-client.exe");
                await Reject(() => Task.FromResult(f.Service.Preview([], new(false, true, true, false, false), null)));
                Assert(File.Exists(retained) && f.DeletedCredentials.Count == 0);
            }),
            ("Launchpad browser reset cannot remove an unselected configured application", async () =>
            {
                using var f = new CatalogTests.Fixture();
                InstallNsisAt(f, SoftwareApp.Vacs, "Roaming/VatscaUpdateChecker/VATIRISProfile/client");
                var service = NewService(f);
                await Reject(() => Task.FromResult(service.Preview([], new(false, true, false, false, false), null)));
                Assert(File.Exists(f.Settings.VacsExePath) && f.RunCount == 0);
            }),
            ("vATIS removal protects an unselected portable EuroScope inside its root", async () =>
            {
                using var f = new CatalogTests.Fixture(); InstallVatis(f);
                f.Settings.EuroscopeExePath = f.File("Local/org.vatsim.vatis/portable/EuroScope.exe");
                f.Binaries[f.Settings.EuroscopeExePath] = new("3.2.3.2", "EuroScope Application");
                var service = NewService(f);
                Assert(!f.Find(AtcRemovalApp.EuroScope).CanRemoveApplication);
                await Reject(() => Task.FromResult(service.Preview([new(f.Find(AtcRemovalApp.Vatis), true, true)], None, null)));
                Assert(File.Exists(f.Settings.EuroscopeExePath) && f.RunCount == 0);
            }),
            ("GNG reset protects a missing configured client path within its package", async () =>
            {
                using var f = new CatalogTests.Fixture(); f.Settings.EuroscopeDataPath = f.Dir("Gng");
                var retained = f.File("Gng/ESAA/custom-client/settings.json", "FAKE-RETAIN");
                f.Settings.TrackAudioExePath = f.PathOf("Gng/ESAA/custom-client/missing.exe");
                var service = NewService(f);
                await Reject(() => Task.FromResult(service.Preview([new(f.Find(AtcRemovalApp.Gng), true, true)], None, null)));
                Assert(File.Exists(retained) && f.RunCount == 0);
            }),
            ("browser reset protects an unsupported configured executable identity", async () =>
            {
                using var f = new CatalogTests.Fixture();
                f.Settings.VacsExePath = f.File("Roaming/VatscaUpdateChecker/VATIRISProfile/custom.exe");
                f.Binaries[f.Settings.VacsExePath] = new("0.0.0", "unknown product");
                var service = NewService(f);
                await Reject(() => Task.FromResult(service.Preview([], new(false, true, false, false, false), null)));
                Assert(File.Exists(f.Settings.VacsExePath) && f.RunCount == 0);
            }),
            ("changed configured path is protected again before apply", async () =>
            {
                using var f = new CatalogTests.Fixture(); InstallVatis(f); var service = NewService(f);
                var plan = service.Preview([new(f.Find(AtcRemovalApp.Vatis), true, true)], None, null);
                f.Settings.EuroscopeExePath = f.PathOf("Local/org.vatsim.vatis/custom/EuroScope.exe");
                await Reject(() => service.ApplyAsync(plan, null, default));
                Assert(f.RunCount == 0 && File.Exists(f.Settings.VatisExePath));
            }),
            ("Launchpad browser reset cannot overlap even a selected vendor installation", async () =>
            {
                using var f = new CatalogTests.Fixture();
                InstallNsisAt(f, SoftwareApp.Vacs, "Roaming/VatscaUpdateChecker/VATIRISProfile/client");
                var service = NewService(f);
                await Reject(() => Task.FromResult(service.Preview([new(f.Find(AtcRemovalApp.Vacs), true, false)],
                    new(false, true, false, false, false), null)));
                Assert(File.Exists(f.Settings.VacsExePath) && f.RunCount == 0);
            }),
            ("selected nested vendor installations are rejected before either uninstaller", async () =>
            {
                using var f = new CatalogTests.Fixture(); f.Install(SoftwareApp.Vacs);
                InstallNsisAt(f, SoftwareApp.TrackAudio, "Apps/Vacs/TrackAudio"); var service = NewService(f);
                await Reject(() => Task.FromResult(service.Preview([
                    new(f.Find(AtcRemovalApp.Vacs), true, false), new(f.Find(AtcRemovalApp.TrackAudio), true, false)], None, null)));
                Assert(f.RunCount == 0 && File.Exists(f.Settings.VacsExePath) && File.Exists(f.Settings.TrackAudioExePath));
            }),
            ("normal vATIS removal marker does not block the next independent vendor", async () =>
            {
                using var f = new CatalogTests.Fixture(); InstallVatis(f); f.Install(SoftwareApp.Vacs);
                var service = NewService(f); var recovery = f.Dir("Recovery");
                var plan = service.Preview(VatisThenVacs(f), None, recovery);
                Assert(plan.VendorSnapshots.Count == 2);
                f.OnRun = command => { SimulateSuccessfulVendor(f, command); return 0; };
                var backup = await service.ApplyAsync(plan, null, default);
                Assert(f.RunCount == 2 && File.Exists(f.PathOf("Local/org.vatsim.vatis/.dead")));
                Assert(!File.Exists(f.Settings.VatisExePath) && !File.Exists(f.Settings.VacsExePath));
                Assert(backup == service.LastBackupFolder && backup == Directory.GetDirectories(recovery).Single());
                RemovalFileService.VerifyBackup(plan.BackupFiles, backup!);
            }),
            ("changed next-vendor settings stop its uninstaller after another vendor finishes", async () =>
            {
                using var f = new CatalogTests.Fixture(); InstallVatis(f); f.Install(SoftwareApp.Vacs);
                var settings = f.File("Roaming/app.vacs.vacs-client/client.toml", "FAKE-OLD");
                var service = NewService(f); var plan = service.Preview(VatisThenVacs(f), None, f.Dir("Recovery"));
                f.OnRun = command =>
                {
                    SimulateSuccessfulVendor(f, command); File.WriteAllText(settings, "FAKE-NEW"); return 0;
                };
                await Reject(() => service.ApplyAsync(plan, null, default));
                Assert(f.RunCount == 1 && File.Exists(f.Settings.VacsExePath) && File.ReadAllText(settings) == "FAKE-NEW");
                Assert(File.Exists(f.PathOf("Local/org.vatsim.vatis/.dead")) && service.LastBackupFolder != null);
                RemovalFileService.VerifyBackup(plan.BackupFiles, service.LastBackupFolder!);
            }),
            ("corrupted completed recovery export blocks the next vendor and direct deletion", () => CorruptExportBlocksRemainingWork(true)),
            ("corrupted completed recovery export blocks direct deletion after the final vendor", () => CorruptExportBlocksRemainingWork(false)),
            ("MSI restart stops later vendors, data deletion and credential reset", () => RestartStopsRemainingWork(true)),
            ("MSI restart survives removal verification failure and prevents retry", () => RestartStopsRemainingWork(false)),
            ("failed vendor retains the exact completed recovery location", async () =>
            {
                using var f = new CatalogTests.Fixture(); f.Install(SoftwareApp.Vacs);
                f.File("Roaming/app.vacs.vacs-client/client.toml", "FAKE-RECOVERABLE");
                var service = NewService(f); var recovery = f.Dir("Recovery");
                var plan = service.Preview([new(f.Find(AtcRemovalApp.Vacs), true, false)], None, recovery);
                f.OnRun = _ => 7;
                await Reject(() => service.ApplyAsync(plan, null, default));
                var actualBackup = Directory.GetDirectories(recovery).Single();
                Assert(service.LastBackupFolder == actualBackup && f.RunCount == 1 && File.Exists(f.Settings.VacsExePath));
                Assert(File.Exists(Path.Combine(actualBackup, "removal-backup.json")) && File.Exists(Path.Combine(actualBackup, "RESTORE.txt")));
                RemovalFileService.VerifyBackup(plan.BackupFiles, actualBackup);
            })
        };
        int failed = 0;
        foreach (var (name, test) in tests)
        {
            try { await test(); Console.WriteLine("PASS coordinator: " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL coordinator: " + name + ": " + ex); }
        }
        Console.WriteLine($"Coordinator: {tests.Length - failed}/{tests.Length} passed.");
        return failed;
    }
    private static void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or OperationCanceledException) { return; }
        throw new Exception("Expected rejection before mutation");
    }
    private static readonly LaunchpadDataSelection None = new(false, false, false, false, false);
    private static async Task RestartStopsRemainingWork(bool completeRemoval)
    {
        using var f = new CatalogTests.Fixture(); f.InstallEuroScope(); f.Install(SoftwareApp.TrackAudio);
        var settings = f.File("Roaming/VatscaUpdateChecker/settings.json", "FAKE-KEEP");
        var credentials = new List<string>(); var service = NewService(f, credentials.Add);
        var selected = new AtcRemovalSelection[]
        {
            new(f.Find(AtcRemovalApp.EuroScope), true, false),
            new(f.Find(AtcRemovalApp.TrackAudio), true, false)
        };
        var plan = service.Preview(selected, new(true, false, true, false, false), f.Dir("Recovery"));
        f.OnRun = command =>
        {
            Assert(command.Arguments[0] == "/x");
            if (completeRemoval) { f.Msi.Clear(); File.Delete(f.Settings.EuroscopeExePath); }
            return 3010;
        };
        await Reject(() => service.ApplyAsync(plan, null, default));
        Assert(service.RestartRequired && f.RunCount == 1 && File.Exists(f.Settings.TrackAudioExePath));
        Assert(File.ReadAllText(settings) == "FAKE-KEEP" && credentials.Count == 0);
        var backup = service.LastBackupFolder;
        RemovalFileService.VerifyBackup(plan.BackupFiles, backup!);
        await Reject(() => service.ApplyAsync(plan, null, default));
        await Reject(() => Task.Run(() => service.Preview(selected, None, f.PathOf("Recovery"))));
        Assert(f.RunCount == 1 && service.LastBackupFolder == backup);
    }
    private static AtcMaintenanceService NewService(CatalogTests.Fixture fixture, Action<string>? deleteCredential = null) =>
        new(fixture.Settings, fixture.Environment, deleteCredential ?? (_ => throw new Exception("Unexpected credential deletion")));
    private static AtcRemovalSelection[] VatisThenVacs(CatalogTests.Fixture f) =>
        [new(f.Find(AtcRemovalApp.Vatis), true, false), new(f.Find(AtcRemovalApp.Vacs), true, false)];
    private static void InstallVatis(CatalogTests.Fixture f)
    {
        const string root = "Local/org.vatsim.vatis/";
        f.Settings.VatisExePath = f.File(root + "current/vATIS.exe");
        var updater = f.File(root + "Update.exe");
        f.File(root + "current/sq.version", "<package><metadata><id>org.vatsim.vatis</id><version>4.1.0-beta.19</version><channel>win</channel><mainExe>vATIS.exe</mainExe><os>win</os></metadata></package>");
        f.File(root + "Profiles/fake.json", "FAKE-PRIVATE-PROFILE");
        f.Binaries[f.Settings.VatisExePath] = new("4.1.0-beta.19", "vATIS");
        f.Binaries[updater] = new("0.0.1251", "Velopack");
    }
    private static void InstallNsisAt(CatalogTests.Fixture f, SoftwareApp app, string relativeRoot)
    {
        var vacs = app == SoftwareApp.Vacs;
        var main = vacs ? "vacs-client.exe" : "trackaudio.exe";
        var version = vacs ? "2.8.0" : "1.4.0";
        var root = f.Dir(relativeRoot); var exe = f.File(relativeRoot + "/" + main);
        f.File(relativeRoot + "/" + (vacs ? "uninstall.exe" : "Uninstall TrackAudio.exe"));
        f.Binaries[exe] = new(version, vacs ? "vacs" : "TrackAudio");
        f.Registrations[app] = [new(root, root, "CurrentUser", version, main)];
        if (vacs) f.Settings.VacsExePath = exe; else f.Settings.TrackAudioExePath = exe;
    }
    private static void SimulateSuccessfulVendor(CatalogTests.Fixture f, AtcRemovalCommand command)
    {
        // Simulate only the observed uninstall result; no process or real installation is used.
        if (command.Executable == f.PathOf("Local/org.vatsim.vatis/Update.exe"))
        {
            Assert(command.Arguments.SequenceEqual(new[] { "--silent", "uninstall" }));
            File.Delete(f.Settings.VatisExePath); f.File("Local/org.vatsim.vatis/.dead", "synthetic vendor marker");
        }
        else
        {
            Assert(command.NsisInstallDirectory == Path.GetDirectoryName(f.Settings.VacsExePath));
            File.Delete(f.Settings.VacsExePath);
        }
    }
    private static async Task CorruptExportBlocksRemainingWork(bool secondVendor)
    {
        using var f = new CatalogTests.Fixture(); InstallVatis(f);
        if (secondVendor) f.Install(SoftwareApp.Vacs);
        var source = f.File("Roaming/VatscaUpdateChecker/settings.json", "FAKE-KEEP-ON-FAILURE");
        var credentials = new List<string>(); var service = NewService(f, credentials.Add); var recovery = f.Dir("Recovery");
        var selected = secondVendor ? VatisThenVacs(f) : new AtcRemovalSelection[] { new(f.Find(AtcRemovalApp.Vatis), true, false) };
        var plan = service.Preview(selected, new(true, false, true, false, false), recovery);
        f.OnRun = command =>
        {
            SimulateSuccessfulVendor(f, command);
            var exported = Directory.EnumerateFiles(Path.Combine(service.LastBackupFolder!, "Files"), "settings.json", SearchOption.AllDirectories).Single();
            File.WriteAllText(exported, "CORRUPTED-SYNTHETIC-EXPORT"); return 0;
        };
        await Reject(() => service.ApplyAsync(plan, null, default));
        Assert(f.RunCount == 1 && File.ReadAllText(source) == "FAKE-KEEP-ON-FAILURE" && credentials.Count == 0);
        Assert(!secondVendor || File.Exists(f.Settings.VacsExePath));
        Assert(service.LastBackupFolder == Directory.GetDirectories(recovery).Single());
        await Reject(() => Task.Run(() => RemovalFileService.VerifyBackup(plan.BackupFiles, service.LastBackupFolder!)));
    }
    private sealed class Case : IDisposable
    {
        private readonly Fixture _files = new();
        public bool BrowserRunning { get; set; }
        public List<string> DeletedCredentials { get; } = [];
        public string Recovery => _files.Backups;
        public AppSettings Settings { get; }
        public AtcMaintenanceService Service { get; }
        public Case()
        {
            _files.Dir("roaming"); _files.Dir("local"); _files.Dir("gng");
            var software = new SoftwareInstallerEnvironment
            {
                LocalAppData = PathOf("local"), ReadRegistrations = _ => [], IsRunning = _ => false,
                ReadBinary = _ => new("0.0.0", "synthetic"), VerifyPublisher = (_, _) => throw new Exception("Unexpected trust check"),
                PrerequisiteProblem = _ => null, RunAsync = _ => throw new Exception("Unexpected process execution")
            };
            var environment = new AtcRemovalEnvironment
            {
                RoamingAppData = PathOf("roaming"), LocalAppData = PathOf("local"), WindowsDirectory = PathOf("fake-windows"),
                Software = software, ReadMsiRegistrations = () => [], IsProcessRunning = name => name == "msedge" && BrowserRunning,
                ReadMsiFootprint = (_, _, _, _) => throw new Exception("Unexpected installed MSI query"),
                RunAsync = _ => throw new Exception("Unexpected uninstaller execution")
            };
            Settings = new() { EuroscopeDataPath = PathOf("gng") };
            Service = new(Settings, environment, DeletedCredentials.Add);
        }
        public string PathOf(string relative) => _files.PathOf(relative);
        public string File(string relative, string contents) => _files.File(relative, contents);
        public void Dispose() => _files.Dispose();
    }
}
