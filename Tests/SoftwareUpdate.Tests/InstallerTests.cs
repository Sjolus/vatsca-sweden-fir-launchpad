using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

internal static class InstallerTests
{
    internal static async Task RunAsync()
    {
        var tests = new (string Name, Func<Task> Body)[]
        {
            ("registered VACS scopes and per-user TrackAudio are recognized", () =>
            {
                foreach (var app in new[] { SoftwareApp.Vacs, SoftwareApp.TrackAudio })
                foreach (var scope in new[] { "CurrentUser", "AllUsers" })
                {
                    using var f = new Fixture(app, scope);
                    var found = f.Installer.Inspect(app, f.Exe);
                    if (app == SoftwareApp.TrackAudio && scope == "AllUsers") Assert(!found.CanUpdate);
                    else Assert(found.CanUpdate && found.Scope == scope && found.RootPath == f.Root);
                    Assert(f.RunCount == 0);
                }
                return Task.CompletedTask;
            }),
            ("portable and ambiguous registrations fail closed", () =>
            {
                using var f = new Fixture(SoftwareApp.Vacs);
                f.Registrations.Clear(); Assert(!f.Inspect().CanUpdate);
                f.Registrations.Add(f.Registration); f.Registrations.Add(f.Registration with { Scope = "AllUsers" });
                Assert(!f.Inspect().CanUpdate); return Task.CompletedTask;
            }),
            ("installer restored destination must match executable", () =>
            {
                using var f = new Fixture(SoftwareApp.Vacs);
                f.Registrations[0] = f.Registration with { RestoreRootPath = f.TestRoot };
                Assert(!f.Inspect().CanUpdate); return Task.CompletedTask;
            }),
            ("TrackAudio four-part program version matches its three-part registration", () =>
            {
                using var f = new Fixture(SoftwareApp.TrackAudio);
                f.SetVersion("1.4.0");
                f.WriteBinary(f.Exe, "TrackAudio", "1.4.0.0");
                var found = f.Inspect();
                Assert(found.CanUpdate && found.Version == "1.4.0" && found.Reason == null && f.RunCount == 0);
                return Task.CompletedTask;
            }),
            ("registration mismatch explains both normalized versions and the remedy", () =>
            {
                using var f = new Fixture(SoftwareApp.TrackAudio);
                f.SetVersion("1.3.3");
                f.WriteBinary(f.Exe, "TrackAudio", "1.4.0.0");
                var found = f.Inspect();
                Assert(!found.CanUpdate && found.Reason == "TrackAudio is version 1.4.0, but Windows lists version 1.3.3. Repair or reinstall TrackAudio using its official installer before updating it in Launchpad.");
                Assert(f.RunCount == 0);
                return Task.CompletedTask;
            }),
            ("different product with the same version has a separate identity diagnostic", () =>
            {
                using var f = new Fixture(SoftwareApp.TrackAudio);
                f.WriteBinary(f.Exe, "Untrusted product text", f.Version);
                var found = f.Inspect();
                Assert(!found.CanUpdate && found.Reason == "The selected program is not identified as TrackAudio. Choose the installed trackaudio.exe file in Settings.");
                Assert(!found.Reason!.Contains("Untrusted product text") && f.RunCount == 0);
                return Task.CompletedTask;
            }),
            ("version mismatch diagnostics bound metadata length", () =>
            {
                using var f = new Fixture(SoftwareApp.TrackAudio);
                f.WriteBinary(f.Exe, "TrackAudio", new string('1', 200) + ".4.0");
                f.Registrations[0] = f.Registration with { Version = new string('2', 200) + ".3.3" };
                var found = f.Inspect();
                Assert(!found.CanUpdate && found.Reason!.Length < 300 && found.Reason.Contains(new string('1', 64) + "…") && found.Reason.Contains(new string('2', 64) + "…"));
                Assert(f.RunCount == 0);
                return Task.CompletedTask;
            }),
            ("unsupported product and VACS generation rejected", () =>
            {
                using var f = new Fixture(SoftwareApp.Vacs);
                f.WriteBinary(f.Exe, "other", f.Version); Assert(!f.Inspect().CanUpdate);
                f.SetVersion("1.0.0"); Assert(!f.Inspect().CanUpdate); return Task.CompletedTask;
            }),
            ("missing prerequisite prevents visible bootstrap", () =>
            {
                using var f = new Fixture(SoftwareApp.TrackAudio); f.Prerequisite = "Missing synthetic VC runtime";
                Assert(!f.Inspect().CanUpdate && f.Inspect().Reason!.Contains("runtime")); return Task.CompletedTask;
            }),
            ("vATIS root and signed helper recognized", () =>
            {
                using var f = new Fixture(SoftwareApp.Vatis); Assert(f.Inspect().CanUpdate && f.TrustChecks == 2);
                Assert(!f.Installer.Inspect(f.App, Path.Combine(f.TestRoot, "elsewhere.exe")).CanUpdate);
                return Task.CompletedTask;
            }),
            ("unsupported or untrusted vATIS helper rejected", () =>
            {
                using var f = new Fixture(SoftwareApp.Vatis);
                f.WriteBinary(Path.Combine(f.Root, "Update.exe"), "Velopack", "1.2.161"); Assert(!f.Inspect().CanUpdate);
                f.WriteBinary(Path.Combine(f.Root, "Update.exe"), "Velopack", "0.0.1251");
                f.RejectTrust = true; Assert(!f.Inspect().CanUpdate); return Task.CompletedTask;
            }),
            ("malformed and cross-product installed manifests rejected", () =>
            {
                using var f = new Fixture(SoftwareApp.Vatis);
                File.WriteAllText(f.ManifestPath, "<broken"); Assert(!f.Inspect().CanUpdate);
                File.WriteAllText(f.ManifestPath, Manifest(f.Version, id: "other")); Assert(!f.Inspect().CanUpdate);
                return Task.CompletedTask;
            }),
            ("silent command plans preserve scope and never run client", () =>
            {
                foreach (var app in new[] { SoftwareApp.Vacs, SoftwareApp.Vatis, SoftwareApp.TrackAudio })
                {
                    using var f = new Fixture(app);
                    var command = SoftwareInstaller.BuildCommand(f.Inspect(), f.PackagePath);
                    Assert(!command.Arguments.Contains("/R") && !command.Arguments.Contains("--force-run"));
                    Assert(command.Arguments.Contains(app == SoftwareApp.Vatis ? "--norestart" : "/S"));
                    if (app == SoftwareApp.Vacs) Assert(command.Arguments.SequenceEqual(new[] { "/S", "/UPDATE", "/CurrentUser" }));
                    if (app == SoftwareApp.Vatis) Assert(command.Executable == Path.Combine(f.Root, "Update.exe"));
                    Assert(f.RunCount == 0);
                }
                using var machine = new Fixture(SoftwareApp.Vacs, "AllUsers");
                var elevated = SoftwareInstaller.BuildCommand(machine.Inspect(), machine.PackagePath);
                Assert(elevated.Elevate && elevated.Arguments.Contains("/AllUsers"));
                return Task.CompletedTask;
            }),
            ("installer metadata and hash verification", async () =>
            {
                using var f = new Fixture(SoftwareApp.Vacs); var release = f.CreatePackage();
                await f.Installer.VerifyPackageAsync(release, f.PackagePath, default);
                File.AppendAllText(f.PackagePath, "changed");
                await Reject(() => f.Installer.VerifyPackageAsync(release, f.PackagePath, default));
                release = f.CreatePackage(); f.WriteBinary(f.PackagePath, "other", release.Version);
                release = f.ReleaseForCurrentFile();
                await Reject(() => f.Installer.VerifyPackageAsync(release, f.PackagePath, default));
            }),
            ("untrusted release origin and filename rejected", async () =>
            {
                using var f = new Fixture(SoftwareApp.TrackAudio); var release = f.CreatePackage();
                await Reject(() => f.Installer.VerifyPackageAsync(release with { DownloadUri = new Uri("https://evil.invalid/" + release.FileName) }, f.PackagePath, default));
                await Reject(() => f.Installer.VerifyPackageAsync(release with { FileName = "anything.exe" }, f.PackagePath, default));
                await Reject(() => f.Installer.VerifyPackageAsync(release with { DownloadUri = new Uri(release.DownloadUri + "?redirect=other") }, f.PackagePath, default));
            }),
            ("vATIS package validates both manifests and executable trust", async () =>
            {
                using var f = new Fixture(SoftwareApp.Vatis); var release = f.CreatePackage();
                await f.Installer.VerifyPackageAsync(release, f.PackagePath, default); Assert(f.TrustChecks == 2);
                f.RejectTrust = true; await Reject(() => f.Installer.VerifyPackageAsync(release, f.PackagePath, default));
            }),
            ("vATIS verification rejects linked cache parents before extracting files", async () =>
            {
                using var f = new Fixture(SoftwareApp.Vatis); var release = f.CreatePackage();
                var target = Path.Combine(f.TestRoot, "outside-cache");
                Directory.CreateDirectory(target);
                var link = Path.Combine(f.TestRoot, "VatscaUpdateChecker", "SoftwareUpdates", "Verification");
                SyntheticJunction.Create(link, target);
                try
                {
                    await Reject(() => f.Installer.VerifyPackageAsync(release, f.PackagePath, default));
                    Assert(!Directory.EnumerateFileSystemEntries(target).Any());
                    Assert(f.TrustChecks == 0 && f.RunCount == 0);
                }
                finally { Directory.Delete(link, recursive: false); }
            }),
            ("vATIS wrong package identity and channel rejected", async () =>
            {
                foreach (var variant in new[] { "wrong-id", "wrong-channel", "wrong-version", "wrong-helper", "wrong-main" })
                {
                    using var f = new Fixture(SoftwareApp.Vatis); var release = f.CreatePackage(variant);
                    await Reject(() => f.Installer.VerifyPackageAsync(release, f.PackagePath, default));
                }
            }),
            ("vATIS traversal duplicate and symlink entries rejected", async () =>
            {
                foreach (var variant in new[] { "traversal", "duplicate", "symlink", "ads" })
                {
                    using var f = new Fixture(SoftwareApp.Vatis); var release = f.CreatePackage(variant);
                    await Reject(() => f.Installer.VerifyPackageAsync(release, f.PackagePath, default));
                }
            }),
            ("vATIS backup preserves binaries and fake user data outside root", async () =>
            {
                using var f = new Fixture(SoftwareApp.Vatis); var before = f.Inspect();
                var folder = await f.Installer.BackupAsync(before, null, default) ?? throw new Exception("Missing backup");
                Assert(!folder.StartsWith(f.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                Assert(File.ReadAllText(Path.Combine(folder, "Files", "Profiles", "synthetic.json")) == "FAKE-PROFILE");
                Assert(File.ReadAllText(Path.Combine(folder, "Files", "AppConfig.json")) == "FAKE-SECRET-DO-NOT-LOG");
                Assert(File.Exists(Path.Combine(folder, "Files", "current", "vATIS.exe")) && File.Exists(Path.Combine(folder, "Files", "Update.exe")));
                Assert(!File.ReadAllText(Path.Combine(folder, "restore-manifest.json")).Contains("FAKE-SECRET"));
                Assert(File.Exists(Path.Combine(folder, "RESTORE.txt")) && f.RunCount == 0);
            }),
            ("vATIS cannot apply without completed backup", async () =>
            {
                using var f = new Fixture(SoftwareApp.Vatis); var release = f.CreatePackage();
                await Reject(() => f.Installer.InstallAsync(f.Inspect(), release, f.PackagePath, null)); Assert(f.RunCount == 0);
            }),
            ("post-backup data change prevents apply", async () =>
            {
                using var f = new Fixture(SoftwareApp.Vatis); var release = f.CreatePackage(); var before = f.Inspect();
                await f.Installer.BackupAsync(before, null, default);
                File.AppendAllText(Path.Combine(f.Root, "AppConfig.json"), "changed");
                await Reject(() => f.Installer.InstallAsync(before, release, f.PackagePath, null)); Assert(f.RunCount == 0);
            }),
            ("post-backup new file prevents apply", async () =>
            {
                using var f = new Fixture(SoftwareApp.Vatis); var release = f.CreatePackage(); var before = f.Inspect();
                await f.Installer.BackupAsync(before, null, default); File.WriteAllText(Path.Combine(f.Root, "new.txt"), "new");
                await Reject(() => f.Installer.InstallAsync(before, release, f.PackagePath, null)); Assert(f.RunCount == 0);
            }),
            ("backup tampering prevents apply", async () =>
            {
                using var f = new Fixture(SoftwareApp.Vatis); var release = f.CreatePackage(); var before = f.Inspect();
                var backup = await f.Installer.BackupAsync(before, null, default);
                File.WriteAllText(Path.Combine(backup!, "Files", "AppConfig.json"), "changed");
                await Reject(() => f.Installer.InstallAsync(before, release, f.PackagePath, null)); Assert(f.RunCount == 0);
            }),
            ("cancelled backup never authorizes apply", async () =>
            {
                using var f = new Fixture(SoftwareApp.Vatis); var release = f.CreatePackage(); var before = f.Inspect();
                await Reject(() => f.Installer.BackupAsync(before, null, new CancellationToken(true)));
                await Reject(() => f.Installer.InstallAsync(before, release, f.PackagePath, null)); Assert(f.RunCount == 0);
            }),
            ("running application and changed installation block execution", async () =>
            {
                using var f = new Fixture(SoftwareApp.Vacs); var release = f.CreatePackage(); var before = f.Inspect();
                f.Running = true; await Reject(() => f.Installer.InstallAsync(before, release, f.PackagePath, null));
                f.Running = false; f.SetVersion("2.6.0");
                await Reject(() => f.Installer.InstallAsync(before, release, f.PackagePath, null)); Assert(f.RunCount == 0);
            }),
            ("explicit install uses fake runner and verifies resulting identity", async () =>
            {
                foreach (var app in new[] { SoftwareApp.Vacs, SoftwareApp.Vatis, SoftwareApp.TrackAudio })
                {
                    using var f = new Fixture(app); var release = f.CreatePackage(); var before = f.Inspect();
                    await f.Installer.BackupAsync(before, null, default);
                    f.OnRun = () => f.SetVersion(release.Version);
                    var result = await f.Installer.InstallAsync(before, release, f.PackagePath, null);
                    Assert(result.ExecutablePath == f.Exe && f.RunCount == 1 && !f.Running);
                }
            }),
            ("installer successful exit without expected update is failure", async () =>
            {
                using var f = new Fixture(SoftwareApp.Vacs); var release = f.CreatePackage();
                await Reject(() => f.Installer.InstallAsync(f.Inspect(), release, f.PackagePath, null)); Assert(f.RunCount == 1);
            }),
            ("TrackAudio successful exit with stale registration cannot report success", async () =>
            {
                using var f = new Fixture(SoftwareApp.TrackAudio);
                f.SetVersion("1.3.3");
                var before = f.Inspect();
                var release = f.CreatePackage();
                Assert(before.CanUpdate && before.Version == "1.3.3");
                f.OnRun = () => f.WriteBinary(f.Exe, "TrackAudio", release.Version + ".0");
                await Reject(() => f.Installer.InstallAsync(before, release, f.PackagePath, null));
                Assert(f.RunCount == 1 && f.ExitCode == 0 && f.Registrations.Single().Version == "1.3.3");
                var after = f.Inspect();
                Assert(!after.CanUpdate && after.Reason == "TrackAudio is version 1.4.0, but Windows lists version 1.3.3. Repair or reinstall TrackAudio using its official installer before updating it in Launchpad.");
            }),
            ("UAC cancellation and installer errors are reported", async () =>
            {
                foreach (var code in new[] { 1223, 1603 })
                {
                    using var f = new Fixture(SoftwareApp.TrackAudio); var release = f.CreatePackage(); f.ExitCode = code;
                    await Reject(() => f.Installer.InstallAsync(f.Inspect(), release, f.PackagePath, null)); Assert(f.RunCount == 1);
                }
            }),
            ("unrecognized restart exit stays an error but latches and blocks native retry", async () =>
            {
                foreach (var app in new[] { SoftwareApp.Vacs, SoftwareApp.Vatis, SoftwareApp.TrackAudio })
                {
                    using var f = new Fixture(app); var release = f.CreatePackage(); var before = f.Inspect();
                    await f.Installer.BackupAsync(before, null, default);
                    f.ExitCode = 3010;
                    await Reject(() => f.Installer.InstallAsync(before, release, f.PackagePath, null));
                    Assert(f.Installer.RestartRequired && f.RunCount == 1);
                    await Reject(() => f.Installer.InstallAsync(before, release, f.PackagePath, null));
                    Assert(f.RunCount == 1);
                }
            }),
            ("unexpected normal app start is never killed or accepted", async () =>
            {
                using var f = new Fixture(SoftwareApp.TrackAudio); var release = f.CreatePackage();
                f.OnRun = () => { f.SetVersion(release.Version); f.Running = true; };
                await Reject(() => f.Installer.InstallAsync(f.Inspect(), release, f.PackagePath, null)); Assert(f.Running);
            }),
            ("bounded backup depth rejects unbounded traversal", () =>
            {
                using var f = new Fixture(SoftwareApp.Vatis); var path = f.Root;
                for (int i = 0; i < 26; i++) { path = Path.Combine(path, "deep"); Directory.CreateDirectory(path); }
                try { SoftwareInstaller.Inventory(f.Root); throw new Exception("Expected rejection"); } catch (IOException) { }
                return Task.CompletedTask;
            }),
            ("Windows argument quoting preserves spaces and trailing slashes", () =>
            {
                Assert(SoftwareInstallerNative.QuoteArgument(@"C:\folder name\") == "\"C:\\folder name\\\\\"");
                Assert(SoftwareInstallerNative.QuoteArgument("/S") == "/S");
                Assert(SoftwareInstallerNative.QuoteArgument("/UPDATE") == "/UPDATE");
                Assert(SoftwareInstallerNative.QuoteArgument("/CurrentUser") == "/CurrentUser");
                Assert(SoftwareInstallerNative.QuoteArgument("") == "\"\"");
                return Task.CompletedTask;
            })
        };
        foreach (var test in tests) { await test.Body(); Console.WriteLine("PASS installer: " + test.Name); }
        Console.WriteLine($"{tests.Length}/{tests.Length} installer tests passed; no installer processes executed.");
    }

    // Explicit, optional verification of a downloaded official artifact. No installed paths are inspected,
    // and native trust + PE metadata readers run against extracted files only; execution is forbidden.
    internal static async Task VerifyDownloadedVatisAsync(string packagePath)
    {
        var staging = Path.Combine(Path.GetTempPath(), "Launchpad-vATIS-package-verification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var installer = new SoftwareInstaller(new SoftwareInstallerEnvironment
        {
            LocalAppData = staging,
            ReadRegistrations = _ => throw new Exception("Real registry access forbidden"),
            IsRunning = _ => throw new Exception("Real process inspection forbidden"),
            RunAsync = _ => throw new Exception("Installer execution forbidden")
        });
        try
        {
            var release = new SoftwareRelease(SoftwareApp.Vatis, "4.1.0-beta.19",
                new Uri("https://vatis.app/updates/windows/org.vatsim.vatis-4.1.0-beta.19-full.nupkg"),
                "org.vatsim.vatis-4.1.0-beta.19-full.nupkg", "29903D495EA7CCD8C64DA60104EB84E2E23B547EC09E11A0037A2856E3BAD248", 33176430);
            await installer.VerifyPackageAsync(release, packagePath, default);
            Console.WriteLine("PASS official vATIS beta.19 package: SHA256, size, manifests, signed main/updater identity. No execution.");
        }
        finally { DeleteSyntheticRoot(staging, "Launchpad-vATIS-package-verification-"); }
    }

    private static string Manifest(string version, string id = "org.vatsim.vatis", string channel = "win") =>
        $"<package><metadata><id>{id}</id><version>{version}</version><channel>{channel}</channel><mainExe>vATIS.exe</mainExe><os>win</os></metadata></package>";
    private static void Assert(bool value) { if (!value) throw new Exception("Installer assertion failed."); }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException) { return; }
        throw new Exception("Expected installer rejection.");
    }
    private static void DeleteSyntheticRoot(string path, string prefix)
    {
        var root = Path.GetFullPath(path);
        if (!Path.GetFileName(root).StartsWith(prefix, StringComparison.Ordinal) || Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()))
            throw new Exception("Synthetic cleanup escaped its named temporary root.");
        Directory.Delete(root, recursive: true);
    }

    private sealed class Fixture : IDisposable
    {
        public string TestRoot { get; } = Path.Combine(Path.GetTempPath(), "Launchpad-SoftwareInstaller-tests-" + Guid.NewGuid().ToString("N"));
        public SoftwareApp App { get; }
        public string Root { get; }
        public string Exe { get; }
        public string PackagePath => Path.Combine(TestRoot, "download.bin");
        public string ManifestPath => Path.Combine(Root, "current", "sq.version");
        public string Version { get; private set; }
        public string TargetVersion => App switch { SoftwareApp.Vacs => "2.8.0", SoftwareApp.Vatis => "4.1.0-beta.19", _ => "1.4.0" };
        public SoftwareRegistration Registration { get; private set; }
        public List<SoftwareRegistration> Registrations { get; } = [];
        public SoftwareInstaller Installer { get; }
        public bool Running, RejectTrust;
        public string? Prerequisite;
        public int RunCount, TrustChecks, ExitCode;
        public Action? OnRun;
        private string ProductName => App switch { SoftwareApp.Vacs => "vacs", SoftwareApp.Vatis => "vATIS", _ => "TrackAudio" };
        public Fixture(SoftwareApp app, string scope = "CurrentUser")
        {
            App = app;
            Root = Path.Combine(TestRoot, app == SoftwareApp.Vatis ? "org.vatsim.vatis" : ProductName);
            Directory.CreateDirectory(Root);
            Exe = Path.Combine(Root, app == SoftwareApp.Vatis ? "current\\vATIS.exe" : app == SoftwareApp.Vacs ? "vacs-client.exe" : "trackaudio.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(Exe)!);
            Version = app switch { SoftwareApp.Vacs => "2.7.0", SoftwareApp.Vatis => "4.1.0-beta.18", _ => "1.3.0" };
            Registration = new(Root, Root, scope, Version, Path.GetFileName(Exe));
            Registrations.Add(Registration);
            SetVersion(Version);
            if (app == SoftwareApp.Vatis)
            {
                WriteBinary(Path.Combine(Root, "Update.exe"), "Velopack", "0.0.1251");
                Directory.CreateDirectory(Path.Combine(Root, "Profiles"));
                File.WriteAllText(Path.Combine(Root, "Profiles", "synthetic.json"), "FAKE-PROFILE");
                File.WriteAllText(Path.Combine(Root, "AppConfig.json"), "FAKE-SECRET-DO-NOT-LOG");
            }
            Installer = new SoftwareInstaller(new SoftwareInstallerEnvironment
            {
                LocalAppData = TestRoot, ReadRegistrations = _ => Registrations,
                ReadBinary = path => { var fields = File.ReadAllText(path).Split('|'); return new(fields[1], fields[0]); },
                VerifyPublisher = (_, publisher) => { TrustChecks++; Assert(publisher == "Justin Shannon"); if (RejectTrust) throw new InvalidDataException("Synthetic bad signature"); },
                IsRunning = _ => Running, PrerequisiteProblem = _ => Prerequisite,
                RunAsync = _ => { RunCount++; OnRun?.Invoke(); return Task.FromResult(ExitCode); }
            });
        }
        public SoftwareInstallation Inspect() => Installer.Inspect(App, Exe);
        public void WriteBinary(string path, string name, string version) => File.WriteAllText(path, name + "|" + version);
        public void SetVersion(string version)
        {
            Version = version; WriteBinary(Exe, ProductName, version);
            Registration = Registration with { Version = version };
            if (Registrations.Count > 0) Registrations[0] = Registration;
            if (App == SoftwareApp.Vatis) File.WriteAllText(ManifestPath, Manifest(version));
        }
        public SoftwareRelease CreatePackage(string? variant = null)
        {
            if (App != SoftwareApp.Vatis) WriteBinary(PackagePath, ProductName, TargetVersion);
            else
            {
                using var output = new FileStream(PackagePath, FileMode.Create, FileAccess.Write);
                using var zip = new ZipArchive(output, ZipArchiveMode.Create);
                void Add(string name, string text, int attributes = 0)
                {
                    var entry = zip.CreateEntry(name); entry.ExternalAttributes = attributes;
                    using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)); writer.Write(text);
                }
                var manifest = Manifest(variant == "wrong-version" ? "4.1.0-beta.18" : TargetVersion,
                    variant == "wrong-id" ? "other" : "org.vatsim.vatis", variant == "wrong-channel" ? "osx" : "win");
                Add("org.vatsim.vatis.nuspec", manifest); Add("lib/app/sq.version", manifest);
                Add("lib/app/vATIS.exe", "vATIS|" + (variant == "wrong-main" ? "4.1.0-beta.17" : TargetVersion));
                Add("lib/app/Squirrel.exe", "Velopack|" + (variant == "wrong-helper" ? "1.2.161" : "0.0.1251"));
                if (variant == "traversal") Add("../outside.txt", "bad");
                if (variant == "duplicate") Add("LIB/APP/VATIS.EXE", "duplicate");
                if (variant == "symlink") Add("link", "target", unchecked((int)0xA0000000));
                if (variant == "ads") Add("lib/app/vATIS.exe:stream", "bad");
            }
            return ReleaseForCurrentFile();
        }
        public SoftwareRelease ReleaseForCurrentFile()
        {
            var name = App switch { SoftwareApp.Vacs => $"vacs_{TargetVersion}_x64-setup.exe", SoftwareApp.Vatis => $"org.vatsim.vatis-{TargetVersion}-full.nupkg", _ => $"trackaudio-{TargetVersion}-x64-setup.exe" };
            var prefix = App switch { SoftwareApp.Vacs => "https://github.com/vacs-project/vacs/releases/download/vacs-client-v" + TargetVersion + "/", SoftwareApp.Vatis => "https://vatis.app/updates/windows/", _ => "https://github.com/pierr3/TrackAudio/releases/download/" + TargetVersion + "/" };
            return new(App, TargetVersion, new Uri(prefix + name), name, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(PackagePath))), new FileInfo(PackagePath).Length);
        }
        public void Dispose() => DeleteSyntheticRoot(TestRoot, "Launchpad-SoftwareInstaller-tests-");
    }
}
