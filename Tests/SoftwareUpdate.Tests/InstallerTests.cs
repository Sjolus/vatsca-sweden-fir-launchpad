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
            ("VACS 3 uses its renamed executable in either registered scope", () =>
            {
                foreach (var scope in new[] { "CurrentUser", "AllUsers" })
                foreach (var version in new[] { "3.0.0", "3.0.1" })
                {
                    using var f = new Fixture(SoftwareApp.Vacs, scope);
                    f.SetVacsVersion(version);
                    var found = f.Inspect();
                    Assert(found.CanUpdate && found.Version == version && found.Scope == scope &&
                        found.ExePath == Path.Combine(f.Root, "vacs.exe") && f.RunCount == 0);
                }
                return Task.CompletedTask;
            }),
            ("VACS registry lookup follows the 3.0.1 restored destination key", () =>
            {
                var keys = new Dictionary<string, string>
                {
                    [@"Software\vacs\vacs"] = "restored-location",
                    [@"Software\The vacs Project\vacs"] = "temporary-3.0.0-location"
                };
                foreach (var version in new[] { "2.8.0", "3.0.1", "3.0.1.0", "3.1.0" })
                    Assert(keys[SoftwareInstallerEnvironment.VacsDestinationKey(version)] == "restored-location");
                foreach (var version in new[] { "3.0.0", "3.0.0.0" })
                    Assert(keys[SoftwareInstallerEnvironment.VacsDestinationKey(version)] == "temporary-3.0.0-location");
                keys.Remove(@"Software\The vacs Project\vacs");
                Assert(keys.ContainsKey(SoftwareInstallerEnvironment.VacsDestinationKey("3.0.1")));
                return Task.CompletedTask;
            }),
            ("VACS generation and executable must agree", () =>
            {
                foreach (var (version, name) in new[]
                {
                    ("2.8.0", "vacs.exe"), ("3.0.0", "vacs-client.exe"),
                    ("3.0.0", "other.exe"), ("4.0.0", "vacs.exe")
                })
                {
                    using var f = new Fixture(SoftwareApp.Vacs);
                    f.SetVacsVersion(version, name);
                    Assert(!f.Inspect().CanUpdate && f.RunCount == 0);
                }
                return Task.CompletedTask;
            }),
            ("VACS 3 retains product and registration checks", () =>
            {
                foreach (var variant in new[] { "product", "version", "binary-name", "ambiguous", "destination" })
                {
                    using var f = new Fixture(SoftwareApp.Vacs);
                    f.SetVacsVersion("3.0.0");
                    if (variant == "product") f.WriteBinary(f.Exe, "other", f.Version);
                    if (variant == "version") f.Registrations[0] = f.Registration with { Version = "2.8.0" };
                    if (variant == "binary-name") f.Registrations[0] = f.Registration with { MainBinaryName = "vacs-client.exe" };
                    if (variant == "ambiguous") f.Registrations.Add(f.Registration with { Scope = "AllUsers" });
                    if (variant == "destination") f.Registrations[0] = f.Registration with { RestoreRootPath = f.TestRoot };
                    Assert(!f.Inspect().CanUpdate && f.RunCount == 0);
                }
                return Task.CompletedTask;
            }),
            ("missing prerequisite prevents visible bootstrap", () =>
            {
                using var f = new Fixture(SoftwareApp.TrackAudio); f.Prerequisite = "Missing synthetic VC runtime";
                Assert(!f.Inspect().CanUpdate && f.Inspect().Reason!.Contains("runtime")); return Task.CompletedTask;
            }),
            ("vATIS root and both reviewed signed helpers recognized", () =>
            {
                foreach (var version in new[] { "0.0.1251", "1.2.161" })
                {
                    using var f = new Fixture(SoftwareApp.Vatis);
                    f.WriteBinary(Path.Combine(f.Root, "Update.exe"), "Velopack", version);
                    Assert(f.Inspect().CanUpdate && f.TrustChecks == 2);
                    Assert(!f.Installer.Inspect(f.App, Path.Combine(f.TestRoot, "elsewhere.exe")).CanUpdate);
                }
                return Task.CompletedTask;
            }),
            ("missing unsupported or untrusted vATIS helper retains verified client version without update permission", async () =>
            {
                foreach (var variant in new[] { "missing", "unsupported", "wrong-product", "untrusted" })
                {
                    using var f = new Fixture(SoftwareApp.Vatis);
                    var helper = Path.Combine(f.Root, "Update.exe");
                    if (variant == "missing") File.Delete(helper);
                    if (variant == "unsupported") f.WriteBinary(helper, "Velopack", "9.9.9");
                    if (variant == "wrong-product") f.WriteBinary(helper, "Other product", "1.2.161");
                    if (variant == "untrusted") f.RejectHelperTrust = true;
                    var detected = f.Inspect();
                    Assert(!detected.CanUpdate && detected.Version == f.Version && detected.RootPath == f.Root &&
                        detected.UpdaterPath is null && detected.Reason!.Contains("manually"));
                    var release = f.CreatePackage();
                    await Reject(() => f.Installer.InstallAsync(detected, release, f.PackagePath, null));
                    Assert(f.RunCount == 0);
                }
            }),
            ("invalid or untrusted vATIS client never reports a verified version", () =>
            {
                foreach (var variant in new[] { "missing", "product", "version-mismatch", "invalid-version", "signature" })
                {
                    using var f = new Fixture(SoftwareApp.Vatis);
                    if (variant == "missing") File.Delete(f.Exe);
                    if (variant == "product") f.WriteBinary(f.Exe, "Other product", f.Version);
                    if (variant == "version-mismatch") f.WriteBinary(f.Exe, "vATIS", "4.1.0-beta.17");
                    if (variant == "invalid-version") f.SetVersion("4.1.0-beta.018");
                    if (variant == "signature") f.RejectTrust = true;
                    var detected = f.Inspect();
                    Assert(!detected.CanUpdate && detected.Version == "");
                }
                return Task.CompletedTask;
            }),
            ("malformed and cross-product installed manifests rejected", () =>
            {
                using var f = new Fixture(SoftwareApp.Vatis);
                File.WriteAllText(f.ManifestPath, "<broken"); Assert(!f.Inspect().CanUpdate && f.Inspect().Version == "");
                File.WriteAllText(f.ManifestPath, Manifest(f.Version, id: "other")); Assert(!f.Inspect().CanUpdate && f.Inspect().Version == "");
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
            ("VACS 3 installer verifies identity and hash while unknown generations stay blocked", async () =>
            {
                using var f = new Fixture(SoftwareApp.Vacs) { VacsTargetVersion = "3.0.0" };
                var release = f.CreatePackage();
                await f.Installer.VerifyPackageAsync(release, f.PackagePath, default);
                f.WriteBinary(f.PackagePath, "other", release.Version);
                await Reject(() => f.Installer.VerifyPackageAsync(f.ReleaseForCurrentFile(), f.PackagePath, default));
                release = f.CreatePackage();
                File.AppendAllText(f.PackagePath, "changed");
                await Reject(() => f.Installer.VerifyPackageAsync(release, f.PackagePath, default));
                foreach (var version in new[] { "4.0.0", "3.0.0-rc.1" })
                {
                    using var future = new Fixture(SoftwareApp.Vacs) { VacsTargetVersion = version };
                    var unsupported = future.CreatePackage();
                    await Reject(() => future.Installer.VerifyPackageAsync(unsupported, future.PackagePath, default));
                    Assert(future.RunCount == 0);
                }
                Assert(f.RunCount == 0);
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
                foreach (var helperVersion in new[] { "0.0.1251", "1.2.161" })
                {
                    using var f = new Fixture(SoftwareApp.Vatis) { PackageHelperVersion = helperVersion };
                    var release = f.CreatePackage();
                    await f.Installer.VerifyPackageAsync(release, f.PackagePath, default); Assert(f.TrustChecks == 2);
                    f.RejectHelperTrust = true;
                    await Reject(() => f.Installer.VerifyPackageAsync(release, f.PackagePath, default));
                    f.RejectHelperTrust = false;
                    f.RejectTrust = true;
                    await Reject(() => f.Installer.VerifyPackageAsync(release, f.PackagePath, default));
                }
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
                foreach (var variant in new[] { "wrong-id", "wrong-channel", "wrong-version", "wrong-helper", "wrong-helper-product", "wrong-main", "wrong-main-product" })
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
            ("VACS upgrade follows the verified renamed executable in the original root", async () =>
            {
                foreach (var (scope, from, to) in new[]
                {
                    ("AllUsers", "2.8.0", "3.0.0"),
                    ("AllUsers", "2.8.0", "3.0.1"),
                    ("CurrentUser", "2.8.0", "3.0.1"),
                    ("AllUsers", "3.0.0", "3.0.1"),
                    ("CurrentUser", "3.0.0", "3.0.1"),
                    ("AllUsers", "3.0.1", "3.0.2"),
                    ("CurrentUser", "3.0.1", "3.0.2")
                })
                {
                    using var f = new Fixture(SoftwareApp.Vacs, scope) { VacsTargetVersion = to };
                    f.SetVacsVersion(from);
                    var before = f.Inspect();
                    Assert(before.CanUpdate);
                    var release = f.CreatePackage();
                    Assert(f.Installer.GetUpdateProblem(before, release) == null);
                    f.OnRun = () => f.SetVacsVersion(to);
                    var result = await f.Installer.InstallAsync(before, release, f.PackagePath, null);
                    Assert(result.ExecutablePath == Path.Combine(f.Root, "vacs.exe") && f.RunCount == 1 && !f.Running);
                    Assert(f.Inspect().CanUpdate && f.Inspect().Version == to && f.Inspect().Scope == scope);
                    if (from.StartsWith("2.", StringComparison.Ordinal)) Assert(!File.Exists(before.ExePath));
                }
            }),
            ("VACS 3.0.1 preserves a single custom destination in either scope", async () =>
            {
                foreach (var scope in new[] { "CurrentUser", "AllUsers" })
                foreach (var from in new[] { "2.8.0", "3.0.0" })
                {
                    using var f = new Fixture(SoftwareApp.Vacs, scope, "custom-vacs") { VacsTargetVersion = "3.0.1" };
                    f.SetVacsVersion(from);
                    if (from == "3.0.0") f.VacsLegacyDestination = null;
                    var before = f.Inspect(); var release = f.CreatePackage();
                    Assert(f.Installer.GetUpdateProblem(before, release) == null && f.LastVacsDestinationScope == scope);
                    f.OnRun = () => f.SetVacsVersion(release.Version);
                    var result = await f.Installer.InstallAsync(before, release, f.PackagePath, null);
                    Assert(result.ExecutablePath == Path.Combine(f.Root, "vacs.exe") && f.RunCount == 1);
                    Assert(f.VacsLegacyDestination == f.Root && f.Vacs3Destination == null);
                }
            }),
            ("VACS 3.0.1 refuses conflicting or missing migration locations before execution", async () =>
            {
                foreach (var scope in new[] { "CurrentUser", "AllUsers" })
                foreach (var variant in new[] { "legacy-conflict", "300-conflict", "missing-source" })
                {
                    using var f = new Fixture(SoftwareApp.Vacs, scope) { VacsTargetVersion = "3.0.1" };
                    f.SetVacsVersion("3.0.0");
                    var before = f.Inspect(); var release = f.CreatePackage();
                    string other = Path.Combine(f.TestRoot, "other-vacs"); Directory.CreateDirectory(other);
                    string otherExe = Path.Combine(other, "vacs.exe"); File.WriteAllText(otherExe, "synthetic copy must stay");
                    if (variant == "legacy-conflict") f.VacsLegacyDestination = other;
                    if (variant == "300-conflict") f.Vacs3Destination = other;
                    if (variant == "missing-source") f.Vacs3Destination = null;
                    Assert(f.Installer.GetUpdateProblem(before, release) is not null && f.LastVacsDestinationScope == scope);
                    await Reject(() => f.Installer.InstallAsync(before, release, f.PackagePath, null));
                    Assert(f.RunCount == 0 && File.Exists(before.ExePath) && File.ReadAllText(otherExe) == "synthetic copy must stay");
                }
            }),
            ("VACS 3.0.1 rechecks both location keys at the execution boundary", async () =>
            {
                using var f = new Fixture(SoftwareApp.Vacs, "AllUsers") { VacsTargetVersion = "3.0.1" };
                f.SetVacsVersion("3.0.0");
                var before = f.Inspect(); var release = f.CreatePackage();
                f.OnReadVacsDestinations = () =>
                {
                    if (f.VacsDestinationReads == 2) f.VacsLegacyDestination = Path.Combine(f.TestRoot, "changed-after-review");
                };
                await Reject(() => f.Installer.InstallAsync(before, release, f.PackagePath, null));
                Assert(f.VacsDestinationReads == 2 && f.RunCount == 0 && File.Exists(before.ExePath));
            }),
            ("VACS 3.0.1 rejects linked primary and secondary installer locations", async () =>
            {
                foreach (var primary in new[] { true, false })
                {
                    using var f = new Fixture(SoftwareApp.Vacs, "AllUsers") { VacsTargetVersion = "3.0.1" };
                    f.SetVacsVersion("3.0.0");
                    var before = f.Inspect(); var release = f.CreatePackage();
                    string link = Path.Combine(f.TestRoot, "linked-location"); SyntheticJunction.Create(link, f.Root);
                    try
                    {
                        if (primary) f.Vacs3Destination = link; else f.VacsLegacyDestination = link;
                        await Reject(() => { f.Installer.GetUpdateProblem(before, release); return Task.CompletedTask; });
                        await Reject(() => f.Installer.InstallAsync(before, release, f.PackagePath, null));
                        Assert(f.RunCount == 0 && File.Exists(before.ExePath));
                    }
                    finally { Directory.Delete(link, recursive: false); }
                }
            }),
            ("VACS 3.0.1 rejects an unknown scope before reading installer locations", async () =>
            {
                using var f = new Fixture(SoftwareApp.Vacs) { VacsTargetVersion = "3.0.1" };
                f.SetVacsVersion("3.0.0"); var before = f.Inspect() with { Scope = "Unknown" }; var release = f.CreatePackage();
                await Reject(() => { f.Installer.GetUpdateProblem(before, release); return Task.CompletedTask; });
                Assert(f.VacsDestinationReads == 0 && f.RunCount == 0);
            }),
            ("VACS 2 to 3 blocks destinations its installer cannot preserve", async () =>
            {
                foreach (var variant in new[] { "current-user", "custom-root", "conflicting-destination" })
                {
                    using var f = new Fixture(SoftwareApp.Vacs,
                        variant == "current-user" ? "CurrentUser" : "AllUsers",
                        variant == "custom-root" ? "custom-vacs" : null) { VacsTargetVersion = "3.0.0" };
                    var before = f.Inspect();
                    Assert(before.CanUpdate); // The old installation remains recognized for launch/removal.
                    var release = f.CreatePackage();
                    if (variant == "conflicting-destination") f.Vacs3Destination = Path.Combine(f.TestRoot, "other-vacs");
                    Assert(!string.IsNullOrWhiteSpace(f.Installer.GetUpdateProblem(before, release)));
                    await Reject(() => f.Installer.InstallAsync(before, release, f.PackagePath, null));
                    Assert(f.RunCount == 0 && File.Exists(before.ExePath));
                }
            }),
            ("VACS 2 to 3 rechecks the new installer destination before execution", async () =>
            {
                using var f = new Fixture(SoftwareApp.Vacs, "AllUsers") { VacsTargetVersion = "3.0.0" };
                var before = f.Inspect();
                var release = f.CreatePackage();
                f.Vacs3Destination = f.Root;
                Assert(f.Installer.GetUpdateProblem(before, release) == null);
                f.Vacs3Destination = Path.Combine(f.TestRoot, "changed-after-review");
                await Reject(() => f.Installer.InstallAsync(before, release, f.PackagePath, null));
                Assert(f.RunCount == 0 && File.Exists(before.ExePath));
            }),
            ("VACS process guard includes old and renamed clients", () =>
            {
                Assert(SoftwareInstallerEnvironment.ProductProcessNames(SoftwareApp.Vacs)
                    .SequenceEqual(new[] { "vacs-client", "vacs" }));
                return Task.CompletedTask;
            }),
            ("VACS rename cannot hide a failed or redirected upgrade", async () =>
            {
                foreach (var variant in new[] { "old-name", "missing", "stale-registration", "wrong-version", "scope", "root", "product" })
                {
                    using var f = new Fixture(SoftwareApp.Vacs, "AllUsers") { VacsTargetVersion = "3.0.0" };
                    var before = f.Inspect();
                    var release = f.CreatePackage();
                    f.OnRun = () =>
                    {
                        if (variant == "old-name") { f.SetVersion(release.Version); return; }
                        f.SetVacsVersion(release.Version);
                        if (variant == "missing") File.Delete(f.Exe);
                        if (variant == "stale-registration") f.Registrations[0] = f.Registration with { Version = before.Version };
                        if (variant == "wrong-version") f.SetVacsVersion("3.0.1");
                        if (variant == "scope") f.Registrations[0] = f.Registration with { Scope = "CurrentUser" };
                        if (variant == "root")
                        {
                            var other = Path.Combine(f.TestRoot, "other-installation");
                            Directory.CreateDirectory(other);
                            File.Move(f.Exe, Path.Combine(other, "vacs.exe"));
                            f.Registrations[0] = f.Registration with { RootPath = other, RestoreRootPath = other };
                        }
                        if (variant == "product") f.WriteBinary(f.Exe, "other", release.Version);
                    };
                    await Reject(() => f.Installer.InstallAsync(before, release, f.PackagePath, null));
                    Assert(f.RunCount == 1);
                }
            }),
            ("vATIS beta.20 migration and subsequent update retain root helper and user data", async () =>
            {
                foreach (var (helperVersion, fromVersion, toVersion) in new[]
                {
                    ("0.0.1251", "4.1.0-beta.19", "4.1.0-beta.20"),
                    ("1.2.161", "4.1.0-beta.20", "4.1.0-beta.21")
                })
                {
                    using var f = new Fixture(SoftwareApp.Vatis)
                    {
                        VatisTargetVersion = toVersion,
                        PackageHelperVersion = "1.2.161"
                    };
                    f.SetVersion(fromVersion);
                    var helperPath = Path.Combine(f.Root, "Update.exe");
                    f.WriteBinary(helperPath, "Velopack", helperVersion);
                    var originalHelper = File.ReadAllBytes(helperPath);
                    var before = f.Inspect();
                    var release = f.CreatePackage();
                    var backup = await f.Installer.BackupAsync(before, null, default) ?? throw new Exception("Missing backup");
                    var locked = false;
                    f.OnRun = () =>
                    {
                        try { using var denied = new FileStream(helperPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }
                        catch (IOException) { locked = true; }
                        f.SetVersion(release.Version);
                    };
                    var result = await f.Installer.InstallAsync(before, release, f.PackagePath, null);
                    Assert(result.ExecutablePath == f.Exe && f.RunCount == 1 && locked && !f.Running);
                    Assert(f.LastCommand is { Elevate: false } command && command.Executable == helperPath &&
                        command.Arguments.SequenceEqual(new[] { "--silent", "apply", "--norestart", "--package", f.PackagePath }));
                    Assert(f.Inspect().Version == toVersion && f.Inspect().CanUpdate);
                    Assert(File.ReadAllBytes(helperPath).SequenceEqual(originalHelper));
                    Assert(File.ReadAllBytes(Path.Combine(backup, "Files", "Update.exe")).SequenceEqual(originalHelper));
                    foreach (var relative in new[] { "AppConfig.json", "Profiles/synthetic.json" })
                        Assert(File.ReadAllBytes(Path.Combine(backup, "Files", relative)).SequenceEqual(File.ReadAllBytes(Path.Combine(f.Root, relative))));
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
            var fileName = Path.GetFileName(packagePath);
            var reviewed = fileName switch
            {
                "org.vatsim.vatis-4.1.0-beta.19-full.nupkg" => (Version: "4.1.0-beta.19", Sha256: "29903D495EA7CCD8C64DA60104EB84E2E23B547EC09E11A0037A2856E3BAD248", Size: 33176430L),
                "org.vatsim.vatis-4.1.0-beta.20-full.nupkg" => (Version: "4.1.0-beta.20", Sha256: "656EA06BAFC93C2EFD7E8F82F9A28C7174CC7E2ECF2DC262EE18CFC59F4ED828", Size: 52926856L),
                _ => throw new InvalidDataException("Choose the original beta.19 or beta.20 full package filename.")
            };
            var release = new SoftwareRelease(SoftwareApp.Vatis, reviewed.Version,
                new Uri("https://vatis.app/downloads/windows/" + fileName), fileName, reviewed.Sha256, reviewed.Size);
            await installer.VerifyPackageAsync(release, packagePath, default);
            Console.WriteLine($"PASS official vATIS {reviewed.Version} package: SHA256, size, manifests, signed main/updater identity. No execution.");
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
        public string Exe { get; private set; }
        public string PackagePath => Path.Combine(TestRoot, "download.bin");
        public string ManifestPath => Path.Combine(Root, "current", "sq.version");
        public string Version { get; private set; }
        public string VatisTargetVersion { get; init; } = "4.1.0-beta.19";
        public string VacsTargetVersion { get; init; } = "2.8.0";
        public string PackageHelperVersion { get; init; } = "0.0.1251";
        public string TargetVersion => App switch { SoftwareApp.Vacs => VacsTargetVersion, SoftwareApp.Vatis => VatisTargetVersion, _ => "1.4.0" };
        public SoftwareRegistration Registration { get; private set; }
        public List<SoftwareRegistration> Registrations { get; } = [];
        public SoftwareInstaller Installer { get; }
        public bool Running, RejectTrust, RejectHelperTrust;
        public string? Prerequisite;
        public string? VacsLegacyDestination;
        public string? Vacs3Destination;
        public string? LastVacsDestinationScope;
        public int VacsDestinationReads;
        public Action? OnReadVacsDestinations;
        public int RunCount, TrustChecks, ExitCode;
        public Action? OnRun;
        public SoftwareInstallCommand? LastCommand;
        private string ProductName => App switch { SoftwareApp.Vacs => "vacs", SoftwareApp.Vatis => "vATIS", _ => "TrackAudio" };
        public Fixture(SoftwareApp app, string scope = "CurrentUser", string? installDirectoryName = null)
        {
            App = app;
            Root = Path.Combine(TestRoot, installDirectoryName ?? (app == SoftwareApp.Vatis ? "org.vatsim.vatis" : ProductName));
            Directory.CreateDirectory(Root);
            Exe = Path.Combine(Root, app == SoftwareApp.Vatis ? "current\\vATIS.exe" : app == SoftwareApp.Vacs ? "vacs-client.exe" : "trackaudio.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(Exe)!);
            Version = app switch { SoftwareApp.Vacs => "2.7.0", SoftwareApp.Vatis => "4.1.0-beta.18", _ => "1.3.0" };
            Registration = new(Root, Root, scope, Version, Path.GetFileName(Exe));
            Registrations.Add(Registration);
            SetVersion(Version);
            if (app == SoftwareApp.Vacs) VacsLegacyDestination = Root;
            if (app == SoftwareApp.Vatis)
            {
                WriteBinary(Path.Combine(Root, "Update.exe"), "Velopack", "0.0.1251");
                Directory.CreateDirectory(Path.Combine(Root, "Profiles"));
                File.WriteAllText(Path.Combine(Root, "Profiles", "synthetic.json"), "FAKE-PROFILE");
                File.WriteAllText(Path.Combine(Root, "AppConfig.json"), "FAKE-SECRET-DO-NOT-LOG");
            }
            Installer = new SoftwareInstaller(new SoftwareInstallerEnvironment
            {
                LocalAppData = TestRoot, ProgramFiles = TestRoot, Vacs3MachineDestination = () => Vacs3Destination,
                ReadVacsDestinations = scope =>
                {
                    LastVacsDestinationScope = scope; VacsDestinationReads++; OnReadVacsDestinations?.Invoke();
                    return new(VacsLegacyDestination, Vacs3Destination);
                },
                ReadRegistrations = _ => Registrations,
                ReadBinary = path => { var fields = File.ReadAllText(path).Split('|'); return new(fields[1], fields[0]); },
                VerifyPublisher = (path, publisher) => { TrustChecks++; Assert(publisher == "Justin Shannon"); if (RejectTrust || RejectHelperTrust && Path.GetFileName(path) is "Update.exe" or "Squirrel.exe") throw new InvalidDataException("Synthetic bad signature"); },
                IsRunning = _ => Running, PrerequisiteProblem = _ => Prerequisite,
                RunAsync = command => { LastCommand = command; RunCount++; OnRun?.Invoke(); return Task.FromResult(ExitCode); }
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
        public void SetVacsVersion(string version, string? executableName = null)
        {
            Assert(App == SoftwareApp.Vacs);
            var name = executableName ?? (version.StartsWith("2.", StringComparison.Ordinal) ? "vacs-client.exe" : "vacs.exe");
            var next = Path.Combine(Root, name);
            if (!string.Equals(Exe, next, StringComparison.OrdinalIgnoreCase)) File.Delete(Exe);
            Exe = next;
            Registration = Registration with { MainBinaryName = name };
            SetVersion(version);
            if (version == "3.0.0") Vacs3Destination = Root;
            else { VacsLegacyDestination = Root; Vacs3Destination = null; }
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
                Add("lib/app/vATIS.exe", (variant == "wrong-main-product" ? "Other product|" : "vATIS|") + (variant == "wrong-main" ? "4.1.0-beta.17" : TargetVersion));
                Add("lib/app/Squirrel.exe", (variant == "wrong-helper-product" ? "Other product|" : "Velopack|") + (variant == "wrong-helper" ? "9.9.9" : PackageHelperVersion));
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
            var prefix = App switch { SoftwareApp.Vacs => "https://github.com/vacs-project/vacs/releases/download/vacs-client-v" + TargetVersion + "/", SoftwareApp.Vatis => "https://vatis.app/downloads/windows/", _ => "https://github.com/pierr3/TrackAudio/releases/download/" + TargetVersion + "/" };
            return new(App, TargetVersion, new Uri(prefix + name), name, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(PackagePath))), new FileInfo(PackagePath).Length);
        }
        public void Dispose() => DeleteSyntheticRoot(TestRoot, "Launchpad-SoftwareInstaller-tests-");
    }
}
