using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

internal static class CatalogTests
{
    internal static async Task RunAsync()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("catalog represents every supported row without running vendors", () =>
            {
                using var f = new Fixture();
                var rows = f.Catalog.Discover(f.Settings);
                Check(rows.Count == 7 && rows.Select(x => x.Id).Distinct().Count() == 7 && f.RunCount == 0);
                Check(!f.Find(AtcRemovalApp.EuroScope).CanRemoveApplication);
                Check(!f.Find(AtcRemovalApp.Vatiris).CanRemoveApplication);
                return Task.CompletedTask;
            }),
            ("GNG removal is narrow and includes only package paths plus generated files", () =>
            {
                using var f = new Fixture();
                f.Settings.EuroscopeDataPath = f.Dir("Gng");
                f.File("Gng/ESAA/Plugins/Updated Plugin/new.dll");
                f.File("Gng/ESAA/Plugins/TopSkySettingsLocal.txt");
                f.File("Gng/ESAA TOPSKY.prf");
                f.File("Gng/ESAA-Sweden_20260223160904-260201-0003.sct");
                f.File("Gng/ESAA-Sweden_20260223160904-260201-0003.rwy");
                f.File("Gng/EKDK/keep.txt"); f.File("Gng/ESAA-not-a-package.txt"); f.File("Gng/custom.prf");
                var target = f.Find(AtcRemovalApp.Gng);
                Check(target.ProgramRoots.Count == 4 && target.CanRemoveApplication && target.CanRemoveData);
                Check(!target.ProgramRoots.Contains(f.Settings.EuroscopeDataPath));
                Check(target.ProgramRoots.All(p => !p.Contains("EKDK") && !p.Contains("custom")));
                return Task.CompletedTask;
            }),
            ("VatEFS deletion is exact DLL only and browser reset is isolated", () =>
            {
                using var f = new Fixture(); f.Settings.VatEfsPath = f.Dir("Plugin");
                var dll = f.File("Plugin/VatEFS.dll"); f.File("Plugin/other.dll");
                f.File("Roaming/VatscaUpdateChecker/VatEFSProfile/data");
                f.File("Roaming/VatscaUpdateChecker/VATIRISProfile/data");
                var plugin = f.Find(AtcRemovalApp.VatEfs); var web = f.Find(AtcRemovalApp.Vatiris);
                Check(plugin.ProgramRoots.SequenceEqual(new[] { dll }) && plugin.CanRemoveData);
                Check(web.CanRemoveData && web.DataRoots.Count == 1 && !web.DataRoots.SequenceEqual(plugin.DataRoots));
                return Task.CompletedTask;
            }),
            ("known leftover application data can reset without configured executables", () =>
            {
                using var f = new Fixture(); f.File("Roaming/app.vacs.vacs-client/client.toml");
                f.File("Local/app.vacs.vacs-client/logs/log"); f.File("Roaming/trackaudio/config.json");
                f.File("Local/org.vatsim.vatis/Profiles/fake.json"); f.File("Local/org.vatsim.vatis/AppConfig.json");
                f.File("Local/org.vatsim.vatis/current/vATIS.exe"); f.File("Local/org.vatsim.vatis/packages/keep.nupkg");
                Check(f.Find(AtcRemovalApp.Vacs).DataRoots.Count == 2);
                Check(f.Find(AtcRemovalApp.TrackAudio).CanRemoveData);
                var vatis = f.Find(AtcRemovalApp.Vatis);
                Check(vatis.CanRemoveData && vatis.DataRoots.Count == 2 && vatis.DataRoots.All(p => !p.Contains("current") && !p.Contains("packages")));
                return Task.CompletedTask;
            }),
            ("supported VACS and TrackAudio registration produces typed commands", () =>
            {
                using var f = new Fixture(); f.Install(SoftwareApp.Vacs); f.Install(SoftwareApp.TrackAudio);
                var vacs = f.Find(AtcRemovalApp.Vacs); var track = f.Find(AtcRemovalApp.TrackAudio);
                Check(vacs.CanRemoveApplication && track.CanRemoveApplication && !vacs.VendorSpec!.RemovesData);
                var a = AtcRemovalVendor.BuildCommand(vacs.VendorSpec!, "staged.exe");
                var b = AtcRemovalVendor.BuildCommand(track.VendorSpec!, "staged.exe");
                Check(a.Arguments.SequenceEqual(new[] { "/S", "/CurrentUser" }) && !a.Elevate);
                Check(b.Arguments.SequenceEqual(new[] { "/S", "/currentuser", "--updated" }) && !b.Elevate);
                Check(!a.Arguments.Contains("/R") && !a.Arguments.Contains("/UPDATE"));
                return Task.CompletedTask;
            }),
            ("machine VACS elevates; unsupported machine TrackAudio remains manual", () =>
            {
                using var f = new Fixture(); f.Install(SoftwareApp.Vacs, "AllUsers"); f.Install(SoftwareApp.TrackAudio, "AllUsers");
                var vacs = f.Find(AtcRemovalApp.Vacs);
                Check(AtcRemovalVendor.BuildCommand(vacs.VendorSpec!, "staged.exe").Elevate);
                Check(!f.Find(AtcRemovalApp.TrackAudio).CanRemoveApplication);
                return Task.CompletedTask;
            }),
            ("MSI path, publisher and numeric product version prove EuroScope identity", () =>
            {
                using var f = new Fixture(); f.InstallEuroScope();
                var sample = f.File("Roaming/EuroScope/sample.prf");
                var es = f.Find(AtcRemovalApp.EuroScope);
                Check(es.CanRemoveApplication && es.VendorSpec!.Kind == AtcRemovalVendorKind.Msi);
                Check(es.VendorSpec!.RemovesData && es.DataRoots.Contains(Path.GetDirectoryName(sample)!));
                var command = AtcRemovalVendor.BuildCommand(es.VendorSpec!, es.VendorSpec!.UninstallerPath);
                Check(command.Arguments.SequenceEqual(new[] { "/x", f.Msi[0].ProductCode, "/qn", "/norestart" }));
                Check(command.IsMsi);
                var start = AtcRemovalVendor.BuildStartInfo(command);
                Check(start.Arguments == "/x " + f.Msi[0].ProductCode + " /qn /norestart");
                Check(start.WindowStyle == System.Diagnostics.ProcessWindowStyle.Normal && !start.CreateNoWindow);
                f.Msi[0] = f.Msi[0] with { Publisher = "wrong" }; Check(!f.Find(AtcRemovalApp.EuroScope).CanRemoveApplication);
                return Task.CompletedTask;
            }),
            ("MSI unrelated paths, invalid GUID and ambiguous registrations fail closed", () =>
            {
                using var f = new Fixture(); f.InstallEuroScope(); var original = f.Msi[0];
                f.Msi[0] = original with { InstallLocation = f.Dir("Elsewhere") }; Check(!f.Find(AtcRemovalApp.EuroScope).CanRemoveApplication);
                f.Msi[0] = original with { ProductCode = "not-guid" }; Check(!f.Find(AtcRemovalApp.EuroScope).CanRemoveApplication);
                f.Msi[0] = original; f.Msi.Add(original with { ProductCode = "{11111111-1111-1111-1111-111111111111}" });
                Check(!f.Find(AtcRemovalApp.EuroScope).CanRemoveApplication); return Task.CompletedTask;
            }),
            ("MSI command preserves the reviewed per-user scope without runas", () =>
            {
                using var f = new Fixture(); f.InstallEuroScope();
                Check(AtcRemovalVendor.BuildCommand(f.Find(AtcRemovalApp.EuroScope).VendorSpec!, "msiexec.exe").Elevate);
                f.Msi[0] = f.Msi[0] with { Scope = "CurrentUser" };
                Check(!AtcRemovalVendor.BuildCommand(f.Find(AtcRemovalApp.EuroScope).VendorSpec!, "msiexec.exe").Elevate);
                return Task.CompletedTask;
            }),
            ("known EuroScope MSI component path enables recognition without ARP path fields", () =>
            {
                using var f = new Fixture(); f.InstallEuroScope(); f.Msi[0] = f.Msi[0] with { InstallLocation = "", DisplayIcon = "" };
                f.ComponentPath = f.Settings.EuroscopeExePath;
                Check(f.Find(AtcRemovalApp.EuroScope).CanRemoveApplication);
                Check(new InstallationDiscoveryService(f.Environment).Discover().Any(c => c.Id == AtcRemovalApp.EuroScope && c.CanManage));
                f.ComponentPath = f.PathOf("Other/EuroScope.exe"); Check(!f.Find(AtcRemovalApp.EuroScope).CanRemoveApplication);
                return Task.CompletedTask;
            }),
            ("MSI footprint changes require a new review before vendor removal", async () =>
            {
                using var f = new Fixture(); f.InstallEuroScope(); var target = f.Find(AtcRemovalApp.EuroScope);
                f.FootprintFingerprint = "changed-component-paths";
                await ThrowsAsync(() => f.Vendor.UninstallAsync(target)); Check(f.RunCount == 0);
            }),
            ("EuroScope component route rejects cross-scope ambiguity and unknown product IDs", () =>
            {
                using var f = new Fixture(); f.InstallEuroScope(); f.Msi[0] = f.Msi[0] with { InstallLocation = "" };
                f.ComponentPath = f.Settings.EuroscopeExePath; f.Msi.Add(f.Msi[0] with { Scope = "CurrentUser" });
                Check(!f.Find(AtcRemovalApp.EuroScope).CanRemoveApplication); f.Msi.RemoveAt(1);
                f.Msi[0] = f.Msi[0] with { ProductCode = "{11111111-1111-1111-1111-111111111111}" };
                Check(!f.Find(AtcRemovalApp.EuroScope).CanRemoveApplication); return Task.CompletedTask;
            }),
            ("revalidation rejects changes in GNG removal roots", () =>
            {
                using var f = new Fixture(); f.Settings.EuroscopeDataPath = f.Dir("Gng"); f.File("Gng/ESAA TOPSKY.prf");
                var before = f.Find(AtcRemovalApp.Gng); f.File("Gng/ESAA TWR.prf");
                Throws(() => f.Catalog.Revalidate(before, f.Settings)); return Task.CompletedTask;
            }),
            ("vendor rejects changed uninstaller before running anything", async () =>
            {
                using var f = new Fixture(); f.Install(SoftwareApp.Vacs); var before = f.Find(AtcRemovalApp.Vacs);
                System.IO.File.AppendAllText(before.VendorSpec!.UninstallerPath, "changed");
                await ThrowsAsync(() => f.Vendor.UninstallAsync(before)); Check(f.RunCount == 0);
            }),
            ("running application blocks vendor execution across sessions", async () =>
            {
                using var f = new Fixture(); f.Install(SoftwareApp.TrackAudio); var before = f.Find(AtcRemovalApp.TrackAudio);
                f.Running = true; await ThrowsAsync(() => f.Vendor.UninstallAsync(before)); Check(f.RunCount == 0);
            }),
            ("NSIS stages verified copy and monitors the actual reviewed directory", async () =>
            {
                using var f = new Fixture(); f.Install(SoftwareApp.Vacs); var before = f.Find(AtcRemovalApp.Vacs);
                var original = before.VendorSpec!.UninstallerPath;
                f.OnRun = command =>
                {
                    Check(command.Executable != original && System.IO.File.Exists(command.Executable));
                    Check(command.NsisInstallDirectory == before.VendorSpec.Installation!.RootPath);
                    Check(AtcRemovalCatalog.Hash(command.Executable) == before.VendorSpec.UninstallerSha256);
                    System.IO.File.Delete(f.Settings.VacsExePath); return 0;
                };
                await f.Vendor.UninstallAsync(before); Check(f.RunCount == 1 && System.IO.File.Exists(original));
            }),
            ("successful vendor exit is not sufficient when executable remains", async () =>
            {
                using var f = new Fixture(); f.Install(SoftwareApp.Vacs);
                await ThrowsAsync(() => f.Vendor.UninstallAsync(f.Find(AtcRemovalApp.Vacs))); Check(f.RunCount == 1);
            }),
            ("UAC cancellation is reported without fallback execution", async () =>
            {
                using var f = new Fixture(); f.Install(SoftwareApp.Vacs, "AllUsers"); f.OnRun = _ => 1223;
                await ThrowsAsync(() => f.Vendor.UninstallAsync(f.Find(AtcRemovalApp.Vacs))); Check(f.RunCount == 1);
            }),
            ("MSI completion requires removal of the reviewed registration", async () =>
            {
                using var f = new Fixture(); f.InstallEuroScope(); var before = f.Find(AtcRemovalApp.EuroScope);
                f.OnRun = _ => { f.Msi.Clear(); return 0; }; await f.Vendor.UninstallAsync(before); Check(f.RunCount == 1);
            }),
            ("vATIS typed uninstall never requests a normal launch or Setup", () =>
            {
                using var f = new Fixture();
                var installed = new SoftwareInstallation(SoftwareApp.Vatis, f.PathOf("vatis/current/vATIS.exe"), "4.1.0-beta.19", f.PathOf("vatis"), "CurrentUser", f.PathOf("vatis/Update.exe"), true, null);
                var spec = new AtcRemovalVendorSpec(AtcRemovalVendorKind.Velopack, installed, installed.UpdaterPath!, "fake", true);
                var command = AtcRemovalVendor.BuildCommand(spec, spec.UninstallerPath);
                Check(command.Arguments.SequenceEqual(new[] { "--silent", "uninstall" }) && !command.Elevate); return Task.CompletedTask;
            }),
            ("browser resets block conservatively when any Edge is running", () =>
            {
                using var f = new Fixture(); f.Running = true; Check(f.Catalog.IsRunning(f.Find(AtcRemovalApp.Vatiris))); return Task.CompletedTask;
            }),
            ("discovery finds known registered products and GNG markers without modifying settings", () =>
            {
                using var f = new Fixture(); f.Install(SoftwareApp.Vacs); f.Install(SoftwareApp.TrackAudio); f.InstallEuroScope();
                f.Dir("Roaming/EuroScope/ESAA"); f.Dir("Documents/EuroScope/ESAA"); f.File("ProgramFiles/VatEFS/VatEFS.dll");
                var empty = new AppSettings(); var found = new InstallationDiscoveryService(f.Environment).Discover();
                Check(found.Any(c => c.Id == AtcRemovalApp.Vacs && c.CanManage));
                Check(found.Any(c => c.Id == AtcRemovalApp.TrackAudio && c.CanManage));
                Check(found.Any(c => c.Id == AtcRemovalApp.EuroScope && c.CanManage));
                Check(found.Count(c => c.Id == AtcRemovalApp.Gng) == 2 && found.Any(c => c.Id == AtcRemovalApp.VatEfs));
                Check(empty.VacsExePath == "" && f.RunCount == 0); return Task.CompletedTask;
            }),
            ("discovery reports recognized executable with unsupported management scope", () =>
            {
                using var f = new Fixture(); f.Install(SoftwareApp.TrackAudio, "AllUsers");
                var found = new InstallationDiscoveryService(f.Environment).Discover();
                Check(found.Single(c => c.Id == AtcRemovalApp.TrackAudio).CanManage == false); return Task.CompletedTask;
            }),
            ("discovery keeps later VACS and TrackAudio copies after a malformed registration", () =>
            {
                foreach (var app in new[] { SoftwareApp.Vacs, SoftwareApp.TrackAudio })
                {
                    using var f = new Fixture(); f.Install(app);
                    var valid = f.Registrations[app][0];
                    f.Registrations[app].Insert(0, valid with { RootPath = f.PathOf("Invalid\0Registration") });
                    var found = new InstallationDiscoveryService(f.Environment).Discover();
                    var candidate = found.Single(c => c.Id == (app == SoftwareApp.Vacs ? AtcRemovalApp.Vacs : AtcRemovalApp.TrackAudio));
                    Check(candidate.ExecutablePath == Path.Combine(valid.RootPath, valid.MainBinaryName));
                    Check(!candidate.CanManage && f.RunCount == 0);
                }
                return Task.CompletedTask;
            }),
            ("discovery keeps later VACS and TrackAudio copies after unreadable program metadata", () =>
            {
                foreach (var app in new[] { SoftwareApp.Vacs, SoftwareApp.TrackAudio })
                {
                    using var f = new Fixture(); f.Install(app);
                    var valid = f.Registrations[app][0];
                    var unreadable = f.File("Unreadable/" + valid.MainBinaryName);
                    f.UnreadableBinaries.Add(unreadable);
                    f.Registrations[app].Insert(0, valid with { RootPath = Path.GetDirectoryName(unreadable)! });
                    var found = new InstallationDiscoveryService(f.Environment).Discover();
                    var candidate = found.Single(c => c.Id == (app == SoftwareApp.Vacs ? AtcRemovalApp.Vacs : AtcRemovalApp.TrackAudio));
                    Check(candidate.ExecutablePath == Path.Combine(valid.RootPath, valid.MainBinaryName));
                    Check(!candidate.CanManage && f.RunCount == 0);
                }
                return Task.CompletedTask;
            }),
            ("discovery keeps a later EuroScope registration after an unreadable MSI component", () =>
            {
                using var f = new Fixture(); f.InstallEuroScope();
                var original = f.Msi[0];
                f.Msi[0] = original with { ProductCode = "{11111111-1111-1111-1111-111111111111}" };
                f.Msi.Insert(0, original with { InstallLocation = f.PathOf("UnreadableMsi") });
                f.ComponentReadFails = true;
                var found = new InstallationDiscoveryService(f.Environment).Discover();
                var candidate = found.Single(c => c.Id == AtcRemovalApp.EuroScope);
                Check(candidate.ExecutablePath == f.Settings.EuroscopeExePath);
                Check(!candidate.CanManage && f.RunCount == 0);
                return Task.CompletedTask;
            }),
            ("discovery does not scan arbitrary folders or accept wrong executable products", () =>
            {
                using var f = new Fixture(); f.Install(SoftwareApp.Vacs);
                f.Binaries[f.Settings.VacsExePath] = new("2.8.0", "unrelated"); f.Dir("Unrelated/EuroScope/ESAA");
                var found = new InstallationDiscoveryService(f.Environment).Discover();
                Check(!found.Any(c => c.Id == AtcRemovalApp.Vacs || c.Id == AtcRemovalApp.Gng)); return Task.CompletedTask;
            })
        };
        foreach (var test in tests) { await test.Run(); Console.WriteLine("PASS catalog: " + test.Name); }
        Console.WriteLine($"{tests.Length} catalog/vendor checks passed.");
    }

    private static void Check(bool condition) { if (!condition) throw new Exception("Catalog assertion failed."); }
    private static void Throws(Action action) { try { action(); } catch (Exception e) when (e is IOException or InvalidOperationException or InvalidDataException) { return; } throw new Exception("Expected rejection."); }
    private static async Task ThrowsAsync(Func<Task> action) { try { await action(); } catch (Exception e) when (e is IOException or InvalidOperationException or InvalidDataException) { return; } throw new Exception("Expected rejection."); }

    internal sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "Launchpad-RemovalCatalog-tests-" + Guid.NewGuid().ToString("N"));
        internal AppSettings Settings { get; } = new();
        internal Dictionary<string, SoftwareBinary> Binaries { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal HashSet<string> UnreadableBinaries { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<SoftwareApp, List<SoftwareRegistration>> Registrations { get; } = new();
        internal List<AtcMsiRegistration> Msi { get; } = new();
        internal string? ComponentPath;
        internal bool ComponentReadFails;
        internal string FootprintFingerprint = "synthetic-footprint";
        internal bool Running; internal int RunCount; internal Func<AtcRemovalCommand, int> OnRun = _ => 0;
        internal AtcRemovalEnvironment Environment { get; }
        internal AtcRemovalCatalog Catalog { get; }
        internal AtcRemovalVendor Vendor { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Environment = new()
            {
                LocalAppData = Dir("Local"), RoamingAppData = Dir("Roaming"), WindowsDirectory = Dir("Windows"),
                DocumentsDirectory = Dir("Documents"), ProgramFilesDirectory = Dir("ProgramFiles"), ProgramFilesX86Directory = Dir("ProgramFilesX86"),
                ReadMsiRegistrations = () => Msi.ToArray(), IsProcessRunning = _ => Running,
                ReadMsiFootprint = (registration, root, roaming, windows) => new(registration.ProductCode, registration.Scope, root,
                    Path.Combine(roaming, "EuroScope"), [Path.Combine(root, "EuroScope.exe")], FootprintFingerprint),
                GetMsiComponentPath = (product, component) =>
                {
                    Check(product == AtcRemovalCatalog.EuroScope3232Product && component == AtcRemovalCatalog.EuroScope3232Component);
                    if (ComponentReadFails) throw new IOException("Synthetic unreadable MSI component.");
                    return ComponentPath;
                },
                RunAsync = command => { RunCount++; return Task.FromResult(OnRun(command)); },
                Software = new()
                {
                    LocalAppData = PathOf("Local"), ReadRegistrations = app => Registrations.GetValueOrDefault(app, new()).ToArray(),
                    ReadBinary = path => UnreadableBinaries.Contains(path) ? throw new IOException("Synthetic unreadable program metadata.") : Binaries[path],
                    VerifyPublisher = (_, _) => { },
                    IsRunning = _ => Running, PrerequisiteProblem = _ => null,
                    RunAsync = _ => throw new Exception("The update runner must not be called.")
                }
            };
            Catalog = new(Environment); Vendor = new(Environment);
        }
        internal AtcRemovalTarget Find(AtcRemovalApp id) => Catalog.Discover(Settings).Single(x => x.Id == id);
        internal string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        internal string Dir(string relative) { var path = PathOf(relative); Directory.CreateDirectory(path); return path; }
        internal string File(string relative, string content = "synthetic") { var path = PathOf(relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); System.IO.File.WriteAllText(path, content); return path; }
        internal void Install(SoftwareApp app, string scope = "CurrentUser")
        {
            var main = app == SoftwareApp.Vacs ? "vacs-client.exe" : "trackaudio.exe";
            var root = Dir("Apps/" + app); var path = File("Apps/" + app + "/" + main);
            File("Apps/" + app + "/" + (app == SoftwareApp.Vacs ? "uninstall.exe" : "Uninstall TrackAudio.exe"));
            var version = app == SoftwareApp.Vacs ? "2.8.0" : "1.4.0";
            Binaries[path] = new(version, app == SoftwareApp.Vacs ? "vacs" : "TrackAudio");
            Registrations[app] = new() { new(root, root, scope, version, main) };
            if (app == SoftwareApp.Vacs) Settings.VacsExePath = path; else Settings.TrackAudioExePath = path;
        }
        internal void InstallEuroScope()
        {
            Settings.EuroscopeExePath = File("Apps/EuroScope/EuroScope.exe");
            Binaries[Settings.EuroscopeExePath] = new("3.2.3.2", "EuroScope Application"); File("Windows/System32/msiexec.exe");
            Msi.Add(new("{8A06FB62-717E-460D-A285-C67226EE7B59}", "EuroScope", "Gergely Csernák", "3.2.3", PathOf("Apps/EuroScope"), "", true, "AllUsers"));
        }
        public void Dispose()
        {
            var temp = Path.GetFullPath(Path.GetTempPath()); var root = Path.GetFullPath(Root);
            if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("Launchpad-RemovalCatalog-tests-", StringComparison.Ordinal))
                throw new Exception("Fixture cleanup refused.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
