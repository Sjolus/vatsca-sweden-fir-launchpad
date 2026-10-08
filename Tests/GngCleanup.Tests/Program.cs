using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Text.Json.Nodes;
using VatscaUpdateChecker.Services;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("These checks require Windows path semantics.");
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
var filterIndex = Array.IndexOf(args, "--filter");
var suite = new Suite(Path.Combine(Path.GetTempPath(), "VatscaCleanupTests", Guid.NewGuid().ToString("N")),
    filterIndex >= 0 ? args[filterIndex + 1] : null);
suite.Run(args.Contains("--archive", StringComparer.OrdinalIgnoreCase));
var packageIndex = Array.IndexOf(args, "--packages");
if (packageIndex >= 0)
{
    if (packageIndex + 2 >= args.Length) throw new ArgumentException("--packages requires previous and current complete ZIP paths.");
    RealPackageCheck.Run(args[packageIndex + 1], args[packageIndex + 2]);
}

sealed class Suite(string fixturesDirectory, string? filter)
{
    private int passed;
    private readonly List<string> failures = [];

    public void Run(bool archive)
    {
        Directory.CreateDirectory(fixturesDirectory);
        Test("old full package provenance identifies four unchanged retired files", f =>
        {
            var p = f.Plan();
            foreach (var name in f.Retired) Assert(p.Candidates.Any(c => Equal(c.RelativePath, name) && c.IsEligible), "Missing eligible " + name);
            Assert(p.Candidates.Count(c => c.IsEligible) == 4, "Unexpected eligible candidate count");
            Assert(p.Candidates.All(c => !Equal(c.RelativePath, "ESAA/Plugins/Support.dll")), "Dependency DLL became candidate");
        });
        Test("unchanged current package files and custom data are preserved", f =>
        {
            f.Write("my-notes.txt", "fake local notes");
            f.Write("MyCustom.prf", "Fake custom profile");
            f.Write("ESAA/Plugins/Custom.dll", "fake local plugin");
            var p = f.Plan();
            foreach (var name in new[] { "my-notes.txt", "MyCustom.prf", "ESAA/Plugins/Custom.dll", Fixture.NewStem + ".sct", "ESAA.prf" })
                Assert(p.Candidates.All(c => !Equal(c.RelativePath, name)), "Unrelated/current file became candidate: " + name);
        });
        Test("modified retired SCT protects its entire generation", f =>
        {
            f.Write(Fixture.OldStem + ".sct", "fake local modification");
            foreach (var extension in new[] { ".sct", ".ese", ".rwy" }) AssertProtected(f, Fixture.OldStem + extension);
        });
        Test("modified retired ESE protects its entire generation", f =>
        {
            f.Write(Fixture.OldStem + ".ese", "fake local modification");
            foreach (var extension in new[] { ".sct", ".ese", ".rwy" }) AssertProtected(f, Fixture.OldStem + extension);
        });
        Test("modified retired DLL is protected", f =>
        {
            f.Write(Fixture.OldDll, "fake modified DLL");
            AssertProtected(f, Fixture.OldDll);
        });
        Test("selected removed package PRF is protected", f =>
        {
            f.Write("ESAA_OLD.prf", f.OldEntries["ESAA_OLD.prf"]);
            var p = GngCleanupService.BuildPlan(f.Root, f.OldZip, f.NewZip, f.Context, f.PathFor("ESAA_OLD.prf"));
            Assert(p.Candidates.All(c => !Equal(c.RelativePath, "ESAA_OLD.prf") || !c.IsEligible), "Selected profile was eligible");
        });
        Test("removed unchanged PRF can be proposed", f =>
        {
            f.Write("ESAA_OLD.prf", f.OldEntries["ESAA_OLD.prf"]);
            Assert(f.Plan().Candidates.Any(c => Equal(c.RelativePath, "ESAA_OLD.prf") && c.IsEligible), "Removed PRF was not eligible");
        });
        Test("modified removed PRF is protected", f =>
        {
            f.Write("ESAA_OLD.prf", "Fake locally edited profile Åke Öström");
            AssertProtected(f, "ESAA_OLD.prf");
        });
        Test("custom PRF references protect old plugin", f =>
        {
            f.Write("MyCustom.prf", "Plugins\tPlugin0\tESAA\\Plugins\\AFVBridge.dll\r\n");
            AssertProtected(f, Fixture.OldDll);
        });
        Test("nested custom PRF references protect old sector", f =>
        {
            f.Write("Custom/MyCustom.prf", "Settings\tsector\t" + f.PathFor(Fixture.OldStem + ".sct") + "\r\n");
            foreach (var extension in new[] { ".sct", ".ese", ".rwy" }) AssertProtected(f, Fixture.OldStem + extension);
        });
        Test("ASR reference protects old sector", f =>
        {
            f.Write("ASR/Custom.asr", "SECTORFILE:" + f.PathFor(Fixture.OldStem + ".sct") + "\r\n");
            foreach (var extension in new[] { ".sct", ".ese", ".rwy" }) AssertProtected(f, Fixture.OldStem + extension);
        });
        Test("case insensitive Windows references protect old plugin", f =>
        {
            f.Write("MyCustom.prf", "Plugins\tPlugin0\t" + f.PathFor(Fixture.OldDll).ToUpperInvariant() + "\r\n");
            AssertProtected(f, Fixture.OldDll);
        });
        Test("relative Windows references protect old sector", f =>
        {
            f.Write("MyCustom.prf", "Settings\tsector\t.\\" + Fixture.OldStem + ".sct\r\n");
            AssertProtected(f, Fixture.OldStem + ".sct");
        });
        Test("staged plugin folders remain outside cleanup", f =>
        {
            f.OldEntries["ESAA/Plugins/Updated Plugin DLLs/AFVBridge.dll"] = "fake staged DLL";
            f.OldEntries["ESAA/Plugins/Updated Plugin/AFVBridge.dll"] = "fake staged DLL";
            f.Write("ESAA/Plugins/Updated Plugin DLLs/AFVBridge.dll", "fake staged DLL");
            f.Write("ESAA/Plugins/Updated Plugin/AFVBridge.dll", "fake staged DLL");
            f.SaveZips();
            Assert(f.Plan().Candidates.All(c => !c.RelativePath.Contains("Updated Plugin", StringComparison.OrdinalIgnoreCase)), "Staged plugin became candidate");
        });
        Test("non-GNG sector filenames remain outside cleanup", f =>
        {
            f.OldEntries["EBBR_2609.sct"] = "fake foreign sector";
            f.OldEntries["Custom.sct"] = "fake custom sector";
            f.Write("EBBR_2609.sct", "fake foreign sector");
            f.Write("Custom.sct", "fake custom sector");
            f.SaveZips();
            Assert(f.Plan().Candidates.All(c => !Equal(c.RelativePath, "EBBR_2609.sct") && !Equal(c.RelativePath, "Custom.sct")), "Unrecognized/foreign sector became candidate");
        });
        Test("one common archive wrapper is accepted", f =>
        {
            f.SaveZips("GNG Package/");
            Assert(f.Plan().Candidates.Count(c => c.IsEligible) == 4, "Wrapper changed candidate matching");
        });
        Test("ZIP parent traversal is rejected", f =>
        {
            f.OldEntries["../escape.dll"] = "fake hostile archive member";
            f.SaveZips();
            MustReject(() => f.Plan());
        });
        Test("ZIP absolute path is rejected", f =>
        {
            f.OldEntries["C:/escape.dll"] = "fake hostile archive member";
            f.SaveZips();
            MustReject(() => f.Plan());
        });
        Test("ZIP case insensitive duplicate paths are rejected", f =>
        {
            f.OldEntries["esaa/PLUGINS/afvbridge.DLL"] = "fake duplicate archive member";
            f.SaveZips();
            MustReject(() => f.Plan());
        });
        Test("incomplete new generation is rejected", f =>
        {
            f.NewEntries.Remove(Fixture.NewStem + ".ese");
            f.SaveZips();
            MustReject(() => f.Plan());
        });
        Test("update-only archive without PRF is rejected", f =>
        {
            f.NewEntries.Remove("ESAA.prf");
            f.SaveZips();
            MustReject(() => f.Plan());
        });
        Test("new full package without plugins is rejected", f =>
        {
            f.NewEntries.Remove("ESAA/Plugins/RDF.dll");
            f.SaveZips();
            MustReject(() => f.Plan());
        });
        Test("new generation absent locally protects old generation", f =>
        {
            File.Delete(f.PathFor(Fixture.NewStem + ".ese"));
            try { foreach (var name in f.Retired.Where(n => n.EndsWith(".sct") || n.EndsWith(".ese") || n.EndsWith(".rwy"))) AssertProtected(f, name); }
            catch (InvalidOperationException) { }
        });
        Test("Windows-1252 fixture names are not modified by planning", f =>
        {
            f.Write("MyCustom.prf", "FakeName\tÅke Öström\r\nPlugins\tPlugin0\tESAA\\Plugins\\AFVBridge.dll\r\n");
            var before = File.ReadAllBytes(f.PathFor("MyCustom.prf"));
            f.Plan();
            Assert(before.SequenceEqual(File.ReadAllBytes(f.PathFor("MyCustom.prf"))), "Planning modified profile bytes");
        });
        Test("modified local replacement generation protects old generation", f =>
        {
            f.Write(Fixture.NewStem + ".sct", "fake locally modified replacement generation");
            try { foreach (var name in f.Retired.Where(n => n.EndsWith(".sct") || n.EndsWith(".ese") || n.EndsWith(".rwy"))) AssertProtected(f, name); }
            catch (InvalidOperationException) { }
        });
        Test("runtime RWY need not exist to verify replacement sector installation", f =>
        {
            File.Delete(f.PathFor(Fixture.NewStem + ".rwy"));
            Assert(f.Plan().Candidates.Any(c => Equal(c.RelativePath, Fixture.OldStem + ".rwy") && c.IsEligible), "Missing current runtime RWY prevented cleanup of an unchanged old RWY");
        });
        Test("reversed package generations do not retire newer data", f =>
        {
            f.Write("ESAA_OLD.prf", f.OldEntries["ESAA_OLD.prf"]);
            try
            {
                var p = GngCleanupService.BuildPlan(f.Root, f.NewZip, f.OldZip, f.Context);
                Assert(p.Candidates.All(c => !c.RelativePath.StartsWith(Fixture.NewStem, StringComparison.OrdinalIgnoreCase) || !c.IsEligible), "Newer generated file could be retired in favor of older data");
            }
            catch (InvalidOperationException) { }
        });
        Test("unknown or renamed package filenames are rejected", f =>
        {
            var renamed = Path.Combine(Path.GetDirectoryName(f.NewZip)!, "renamed.zip");
            File.Copy(f.NewZip, renamed);
            MustReject(() => GngCleanupService.BuildPlan(f.Root, f.OldZip, renamed, f.Context));
        });
        Test("Update Only package filenames are rejected", f =>
        {
            var renamed = Path.Combine(Path.GetDirectoryName(f.NewZip)!, "ESAA-Update-Only_20261001120000-261001-0003.zip");
            File.Copy(f.NewZip, renamed);
            MustReject(() => GngCleanupService.BuildPlan(f.Root, f.OldZip, renamed, f.Context));
        });
        Test("package filename generation must match contents", f =>
        {
            var renamed = Path.Combine(Path.GetDirectoryName(f.NewZip)!, "ESAA-Full-Package_20261029120000-261101-0001.zip");
            File.Copy(f.NewZip, renamed);
            MustReject(() => GngCleanupService.BuildPlan(f.Root, f.OldZip, renamed, f.Context));
        });
        Test("browser numbered download suffix is accepted", f =>
        {
            var renamed = Path.Combine(Path.GetDirectoryName(f.NewZip)!, Path.GetFileNameWithoutExtension(f.NewZip) + " (1).zip");
            File.Copy(f.NewZip, renamed);
            Assert(GngCleanupService.BuildPlan(f.Root, f.OldZip, renamed, f.Context).Candidates.Count(c => c.IsEligible) == 4, "Browser suffix prevented full-package identification");
        });
        Test("runtime RWY can be inferred from both unchanged retired companions", f =>
        {
            f.OldEntries.Remove(Fixture.OldStem + ".rwy");
            f.SaveZips();
            Assert(f.Plan().Candidates.Any(c => Equal(c.RelativePath, Fixture.OldStem + ".rwy") && c.IsEligible), "Runtime-created RWY was not inferred from unchanged old sector companions");
        });
        Test("locally modified current runtime RWY does not invalidate installed sectors", f =>
        {
            f.Write(Fixture.NewStem + ".rwy", "fake runtime change");
            Assert(f.Plan().Candidates.Count(c => c.IsEligible) == 4, "Runtime RWY change invalidated sector installation");
        });
        Test("ZIP symlink entries are rejected", f =>
        {
            using (var zip = ZipFile.Open(f.OldZip, ZipArchiveMode.Update))
                zip.GetEntry("ESAA/Plugins/Support.dll")!.ExternalAttributes = unchecked((int)0xA1FF0000);
            MustReject(() => f.Plan());
        });
        Test("ZIP file and directory collisions are rejected", f =>
        {
            f.OldEntries["ESAA/Plugins"] = "fake file colliding with directory";
            f.SaveZips();
            MustReject(() => f.Plan());
        });
        Test("inferred runtime RWY is protected when old SCT is missing", f =>
        {
            f.OldEntries.Remove(Fixture.OldStem + ".rwy");
            f.SaveZips();
            File.Delete(f.PathFor(Fixture.OldStem + ".sct"));
            AssertProtected(f, Fixture.OldStem + ".rwy");
        });
        Test("inferred runtime RWY is protected when old ESE is modified", f =>
        {
            f.OldEntries.Remove(Fixture.OldStem + ".rwy");
            f.SaveZips();
            f.Write(Fixture.OldStem + ".ese", "fake local modified old companion");
            AssertProtected(f, Fixture.OldStem + ".rwy");
        });
        Test("inferred runtime RWY is protected when old SCT is referenced", f =>
        {
            f.OldEntries.Remove(Fixture.OldStem + ".rwy");
            f.SaveZips();
            f.Write("MyCustom.prf", "Settings\tsector\t" + Fixture.OldStem + ".sct\r\n");
            AssertProtected(f, Fixture.OldStem + ".rwy");
        });
        Test("configured plugin and data-folder paths protect cleanup candidates", f =>
        {
            var p = f.Plan(f.PathFor(Fixture.OldDll), f.PathFor(Fixture.OldStem + ".rwy"));
            Assert(p.Candidates.Where(c => Equal(c.RelativePath, Fixture.OldDll) || c.RelativePath.EndsWith(".rwy")).All(c => !c.IsEligible), "Configured path became eligible");
            Assert(f.Plan(f.PathFor("ESAA/Plugins")).Candidates.Single(c => Equal(c.RelativePath, Fixture.OldDll)).IsEligible == false, "Configured folder did not protect plugin");
        });
        Test("external radar-screen references prevent incomplete cleanup planning", f =>
        {
            var screen = Path.Combine(Path.GetDirectoryName(f.Root)!, "external.asr");
            File.WriteAllText(screen, "SECTORFILE:" + f.PathFor(Fixture.OldStem + ".sct"));
            f.Write("MyCustom.prf", "Settings\tRadarScreen\t" + screen);
            MustReject(() => f.Plan());
        });
        Test("missing radar-screen references prevent incomplete cleanup planning", f =>
        {
            f.Write("MyCustom.prf", "Settings\tRadarScreen\tASR\\missing.asr");
            MustReject(() => f.Plan());
        });
        Test("reference errors identify source and target without exposing LastSession values", f =>
        {
            const string secret = "synthetic-private-password.asr";
            foreach (var target in new[] { @"ASR\missing.asr", Path.Combine(Path.GetDirectoryName(f.Root)!, "external.asr") })
            {
                f.Write("MyCustom.prf", $"LastSession\tpassword\t{secret}\r\nSettings\tRadarScreen\t{target}\r\n");
                string? diagnostic = null;
                try { f.Plan(); }
                catch (InvalidOperationException ex) { diagnostic = ex.Message; }
                Assert(diagnostic is not null, "Unverifiable screen reference was accepted");
                Assert(diagnostic!.Contains("MyCustom.prf") && diagnostic.Contains(target), "Reference error did not identify its source and target");
                Assert(!diagnostic.Contains(secret), "Reference error exposed a LastSession value");
            }
        });
        Test("external selected profile prevents incomplete cleanup planning", f =>
        {
            var profile = Path.Combine(Path.GetDirectoryName(f.Root)!, "external.prf");
            File.WriteAllText(profile, "Settings\tsector\t" + f.PathFor(Fixture.OldStem + ".sct"));
            MustReject(() => GngCleanupService.BuildPlan(f.Root, f.OldZip, f.NewZip, f.Context, profile));
        });
        Test("internal linked radar screens protect their sector references", f =>
        {
            f.Write("ASR/Custom.asr", "SECTORFILE:" + f.PathFor(Fixture.OldStem + ".sct"));
            f.Write("MyCustom.prf", "Settings\tRadarScreen\tASR\\Custom.asr");
            AssertProtected(f, Fixture.OldStem + ".sct");
        });
        Test("relative and ambiguous protected paths are rejected", f =>
        {
            MustReject(() => f.Plan("relative/application.exe"));
            MustReject(() => f.Plan(f.PathFor("ESAA/Plugins ")));
        });
        Test("forged ZIP uncompressed entry lengths are rejected", f =>
        {
            var bytes = File.ReadAllBytes(f.OldZip);
            int central = -1;
            for (int i = 0; i <= bytes.Length - 46; i++)
                if (BitConverter.ToUInt32(bytes, i) == 0x02014b50) { central = i; break; }
            Assert(central >= 0, "Fixture ZIP lacks a central directory");
            BitConverter.GetBytes(BitConverter.ToUInt32(bytes, central + 24) + 100u).CopyTo(bytes, central + 24);
            File.WriteAllBytes(f.OldZip, bytes);
            MustReject(() => f.Plan());
        });
        Test("junctions in the data tree prevent unsafe reference inspection", f =>
        {
            var outside = Path.Combine(Path.GetDirectoryName(f.Root)!, "external-data");
            Directory.CreateDirectory(outside);
            SyntheticJunction.Create(f.PathFor("linked-data"), outside);
            MustReject(() => f.Plan());
        });
        Test("a configured junction alias is not silently omitted from protection", f =>
        {
            var alias = Path.Combine(Path.GetDirectoryName(f.Root)!, "plugin-alias");
            SyntheticJunction.Create(alias, f.PathFor("ESAA/Plugins"));
            MustReject(() => f.Plan(Path.Combine(alias, "AFVBridge.dll")));
        });
        if (archive) RunArchiveTests();
        var ui = new Fixture(Path.Combine(fixturesDirectory, "ui-preview"));
        Console.WriteLine($"UI FIXTURE root={ui.Root}");
        Console.WriteLine($"UI FIXTURE oldZip={ui.OldZip}");
        Console.WriteLine($"UI FIXTURE newZip={ui.NewZip}");
        Console.WriteLine($"RESULT {passed} passed, {failures.Count} failed");
        foreach (var failure in failures) Console.WriteLine(failure);
        Environment.ExitCode = failures.Count == 0 ? 0 : 1;
    }

    private void RunArchiveTests()
    {
        Test("archive and restore preserve bytes and exclude unrelated files", f =>
        {
            var expected = File.ReadAllBytes(f.PathFor(Fixture.OldStem + ".sct"));
            var result = f.Archive(f.Plan(), new[] { Fixture.OldStem + ".sct" });
            if (result.BackupFolder is not null) Console.WriteLine("SYNTHETIC BACKUP " + result.BackupFolder);
            Assert(!File.Exists(f.PathFor(Fixture.OldStem + ".sct")), "Archive left selected source in place: " + string.Join(" | ", result.Skipped));
            Assert(File.Exists(f.PathFor(Fixture.OldDll)), "Archive moved unselected file");
            f.Restore(result.BackupFolder ?? throw new Exception("No backup was created"));
            Assert(expected.SequenceEqual(File.ReadAllBytes(f.PathFor(Fixture.OldStem + ".sct"))), "Restore changed file bytes");
        });
        Test("archive skips changed-since-preview files", f =>
        {
            var p = f.Plan();
            f.Write(Fixture.OldStem + ".sct", "fake change after preview");
            var result = f.Archive(p, new[] { Fixture.OldStem + ".sct" });
            if (result.BackupFolder is not null) Console.WriteLine("SYNTHETIC BACKUP " + result.BackupFolder);
            Assert(File.ReadAllText(f.PathFor(Fixture.OldStem + ".sct")).Contains("after preview"), "Changed candidate was moved");
            Assert(result.Skipped.Any(), "Changed candidate did not report skip");
        });
        Test("archive rechecks references added after preview", f =>
        {
            var p = f.Plan();
            f.Write("MyCustom.prf", "Plugins\tPlugin0\tESAA\\Plugins\\AFVBridge.dll\r\n");
            var result = f.Archive(p, new[] { Fixture.OldDll });
            if (result.BackupFolder is not null) Console.WriteLine("SYNTHETIC BACKUP " + result.BackupFolder);
            Assert(File.Exists(f.PathFor(Fixture.OldDll)), "Newly referenced DLL was archived");
            Assert(result.Skipped.Any(), "New reference did not report skip");
        });
        Test("restore does not overwrite an existing file", f =>
        {
            var result = f.Archive(f.Plan(), new[] { Fixture.OldStem + ".sct" });
            if (result.BackupFolder is not null) Console.WriteLine("SYNTHETIC BACKUP " + result.BackupFolder);
            Assert(result.Files.Count == 1, "Archive failed: " + string.Join(" | ", result.Skipped));
            f.Write(Fixture.OldStem + ".sct", "fake restore collision");
            var restored = f.Restore(result.BackupFolder ?? throw new Exception("No backup was created"));
            Assert(File.ReadAllText(f.PathFor(Fixture.OldStem + ".sct")).Contains("restore collision"), "Restore overwrote an existing file");
            Assert(restored.Skipped.Any(), "Restore collision did not report skip");
        });
        Test("archive refuses package changes after preview", f =>
        {
            var p = f.Plan();
            f.OldEntries["extra-notes.txt"] = "fake archive modification";
            f.SaveZips();
            MustReject(() => f.Archive(p, new[] { Fixture.OldStem + ".sct" }));
            Assert(File.Exists(f.PathFor(Fixture.OldStem + ".sct")), "Changed archive authorization moved candidate");
        });
        Test("restore rejects modified backup bytes", f =>
        {
            var result = f.Archive(f.Plan(), new[] { Fixture.OldStem + ".sct" });
            Assert(result.Files.Count == 1, "Archive failed: " + string.Join(" | ", result.Skipped));
            var backup = result.BackupFolder ?? throw new Exception("No backup was created");
            Console.WriteLine("SYNTHETIC BACKUP " + backup);
            File.WriteAllText(Path.Combine(backup, Fixture.OldStem + ".sct"), "fake tampered backup content");
            var restored = f.Restore(backup);
            Assert(!File.Exists(f.PathFor(Fixture.OldStem + ".sct")), "Modified backup was restored");
            Assert(restored.Skipped.Any(), "Modified backup did not report skip");
        });
        Test("restore rejects traversal in backup manifest", f =>
        {
            var result = f.Archive(f.Plan(), new[] { Fixture.OldStem + ".sct" });
            Assert(result.Files.Count == 1, "Archive failed: " + string.Join(" | ", result.Skipped));
            var backup = result.BackupFolder ?? throw new Exception("No backup was created");
            Console.WriteLine("SYNTHETIC BACKUP " + backup);
            var manifestPath = Path.Combine(backup, "cleanup-manifest.json");
            var manifest = File.ReadAllText(manifestPath);
            File.WriteAllText(manifestPath, manifest.Replace(Fixture.OldStem + ".sct", "../outside.sct"));
            MustReject(() => f.Restore(backup));
            Assert(!File.Exists(f.PathFor(Fixture.OldStem + ".sct")), "Invalid backup manifest moved data");
        });
        Test("inferred runtime RWY preserves its original bytes through archive and restore", f =>
        {
            f.OldEntries.Remove(Fixture.OldStem + ".rwy");
            f.SaveZips();
            f.Write(Fixture.OldStem + ".rwy", "fake runtime RWY settings ÅÄÖ");
            var expected = File.ReadAllBytes(f.PathFor(Fixture.OldStem + ".rwy"));
            var result = f.Archive(f.Plan(), new[] { Fixture.OldStem + ".rwy" });
            Assert(result.Files.Count == 1, "Archive failed: " + string.Join(" | ", result.Skipped));
            var backup = result.BackupFolder ?? throw new Exception("No backup was created");
            Console.WriteLine("SYNTHETIC BACKUP " + backup);
            Assert(!File.Exists(f.PathFor(Fixture.OldStem + ".rwy")), "Inferred runtime RWY was not archived");
            f.Restore(backup);
            Assert(expected.SequenceEqual(File.ReadAllBytes(f.PathFor(Fixture.OldStem + ".rwy"))), "Runtime RWY bytes changed");
        });
        Test("inferred runtime RWY changes after preview are skipped", f =>
        {
            f.OldEntries.Remove(Fixture.OldStem + ".rwy");
            f.SaveZips();
            var p = f.Plan();
            f.Write(Fixture.OldStem + ".rwy", "fake runtime change after preview");
            var result = f.Archive(p, new[] { Fixture.OldStem + ".rwy" });
            if (result.BackupFolder is not null) Console.WriteLine("SYNTHETIC BACKUP " + result.BackupFolder);
            Assert(File.ReadAllText(f.PathFor(Fixture.OldStem + ".rwy")).Contains("after preview"), "Changed runtime RWY was archived");
            Assert(result.Skipped.Any(), "Changed runtime RWY did not report skip");
        });
        Test("archive rejects paths outside reviewed candidates", f =>
        {
            MustReject(() => f.Archive(f.Plan(), new[] { "../outside.dll" }));
        });
        Test("archive refuses when synthetic EuroScope is running", f =>
        {
            var p = f.Plan(); f.EuroScopeRunning = true;
            MustReject(() => f.Archive(p, [Fixture.OldDll]));
            Assert(File.Exists(f.PathFor(Fixture.OldDll)), "Running-client guard moved data");
        });
        Test("pending GNG recovery blocks cleanup preview and archive before any move", f =>
        {
            var plan = f.Plan();
            f.MarkPendingUpdate();
            MustReject(() => f.Plan());
            var checks = 0;
            f.OnPendingProbe = () =>
            {
                checks++;
                Assert(MaintenanceLock.IsHeldByCurrentProcess, "Archive checked pending recovery before taking the shared maintenance lease");
            };
            MustReject(() => f.Archive(plan, [Fixture.OldDll]));
            Assert(checks > 0, "Archive never checked pending recovery");
            Assert(File.Exists(f.PathFor(Fixture.OldDll)) && !Directory.Exists(f.BackupRoot), "Pending update allowed cleanup changes");
        });
        Test("cleanup restore remains available while a GNG update needs recovery", f =>
        {
            var bytes = File.ReadAllBytes(f.PathFor(Fixture.OldDll));
            var archived = f.Archive(f.Plan(), [Fixture.OldDll]);
            f.MarkPendingUpdate();
            var restored = f.Restore(archived.BackupFolder!);
            Assert(restored.Files.Count == 1 && bytes.SequenceEqual(File.ReadAllBytes(f.PathFor(Fixture.OldDll))), "Pending update prevented restoring an archived dependency");
            MustReject(() => f.Plan());
        });
        Test("archive and restore refuse an occupied maintenance lock", f =>
        {
            var p = f.Plan();
            Assert(MaintenanceLock.TryAcquire(f.LockName, out var lease), "Fixture failed to acquire gate");
            using (lease) MustReject(() => f.Archive(p, [Fixture.OldDll]));
            var archived = f.Archive(p, [Fixture.OldDll]);
            Assert(archived.Files.Count == 1, "Gate was not released after refusal");
            Assert(MaintenanceLock.TryAcquire(f.LockName, out lease), "Fixture failed to reacquire gate");
            using (lease) MustReject(() => f.Restore(archived.BackupFolder!));
            Assert(f.Restore(archived.BackupFolder!).Files.Count == 1, "Restore gate was not released");
        });
        Test("restore refuses when synthetic EuroScope is running", f =>
        {
            var archived = f.Archive(f.Plan(), [Fixture.OldDll]);
            f.EuroScopeRunning = true;
            MustReject(() => f.Restore(archived.BackupFolder!));
            Assert(!File.Exists(f.PathFor(Fixture.OldDll)), "Running-client guard restored data");
        });
        Test("archive preserves explicitly configured paths", f =>
        {
            var result = f.Archive(f.Plan(f.PathFor(Fixture.OldDll)), [Fixture.OldDll]);
            Assert(result.Files.Count == 0 && File.Exists(f.PathFor(Fixture.OldDll)), "Configured plugin was archived");
        });
        Test("restore preserves configured paths even when absent", f =>
        {
            var result = f.Archive(f.Plan(), [Fixture.OldDll]);
            var restored = f.Restore(result.BackupFolder!, f.PathFor(Fixture.OldDll));
            Assert(restored.Files.Count == 0 && restored.Skipped.Count == 1, "Missing configured path was restored");
        });
        Test("restore refuses manifest redirected to another data folder", f =>
        {
            var result = f.Archive(f.Plan(), [Fixture.OldDll]);
            var outside = Path.Combine(Path.GetDirectoryName(f.Root)!, "other-data");
            Directory.CreateDirectory(outside);
            var manifestPath = Path.Combine(result.BackupFolder!, "cleanup-manifest.json");
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
            manifest["OriginalRoot"] = outside;
            File.WriteAllText(manifestPath, manifest.ToJsonString());
            MustReject(() => f.Restore(result.BackupFolder!));
            Assert(!Directory.EnumerateFileSystemEntries(outside).Any(), "Redirected manifest wrote outside reviewed root");
        });
        Test("archive rechecks external radar-screen references added after preview", f =>
        {
            var p = f.Plan();
            var outside = Path.Combine(Path.GetDirectoryName(f.Root)!, "external.asr");
            File.WriteAllText(outside, "SECTORFILE:" + f.PathFor(Fixture.OldStem + ".sct"));
            f.Write("MyCustom.prf", "Settings\tRadarScreen\t" + outside);
            MustReject(() => f.Archive(p, [Fixture.OldStem + ".sct"]));
            Assert(File.Exists(f.PathFor(Fixture.OldStem + ".sct")), "New external reference was missed");
        });
        Test("archive pins retired companions and current sectors against concurrent edits", f =>
        {
            int attempts = 0;
            f.OnProcessProbe = count =>
            {
                if (count != 3) return;
                foreach (var name in new[] { Fixture.OldStem + ".sct", Fixture.NewStem + ".sct" })
                {
                    bool refused = false;
                    try { f.Write(name, "fake concurrent modification"); }
                    catch (IOException) { refused = true; }
                    Assert(refused, "Pinned sector remained writable: " + name);
                    attempts++;
                }
            };
            var result = f.Archive(f.Plan(), [Fixture.OldStem + ".ese"]);
            Assert(result.Files.Count == 1 && attempts == 2, "Pinned generation check did not execute");
        });
        Test("inferred runtime RWY requires both companions until their identities are pinned", f =>
        {
            f.OldEntries.Remove(Fixture.OldStem + ".rwy");
            f.SaveZips();
            var p = f.Plan();
            f.OnProcessProbe = count =>
            {
                if (count == 2) File.Delete(f.PathFor(Fixture.OldStem + ".sct"));
            };
            MustReject(() => f.Archive(p, [Fixture.OldStem + ".rwy"]));
            Assert(File.Exists(f.PathFor(Fixture.OldStem + ".rwy")), "RWY lost its provenance during preparation but was archived");
            Assert(!Directory.Exists(f.BackupRoot), "Incomplete provenance created a recovery operation");
        });
        Test("retired profile and sector group can be archived with pinned handles", f =>
        {
            f.Write("ESAA_OLD.prf", f.OldEntries["ESAA_OLD.prf"]);
            var selected = new[] { "ESAA_OLD.prf", Fixture.OldStem + ".sct", Fixture.OldStem + ".ese", Fixture.OldStem + ".rwy" };
            var result = f.Archive(f.Plan(), selected);
            Assert(result.Files.Count == selected.Length, "Pinned handles prevented own archive: " + string.Join(" | ", result.Skipped));
            Assert(f.Restore(result.BackupFolder!).Files.Count == selected.Length, "Pinned group did not restore");
        });
        Test("retired profile cleanup releases dependencies only after a second comparison and restores file identities", f =>
        {
            const string profile = "ESAA_OLD.prf";
            f.OldEntries[profile] += "Settings\tsector\t" + Fixture.OldStem + ".sct\r\n";
            f.OldEntries.Remove(Fixture.OldStem + ".rwy");
            f.Write(profile, f.OldEntries[profile]);
            f.SaveZips();
            var selected = f.Retired.Prepend(profile).ToArray();
            var before = selected.ToDictionary(path => path, path =>
                (Bytes: File.ReadAllBytes(f.PathFor(path)), Identity: ReadFileIdentity(f.PathFor(path))));
            var currentSector = File.ReadAllBytes(f.PathFor(Fixture.NewStem + ".sct"));

            var firstPlan = f.Plan();
            Assert(firstPlan.Candidates.Single(c => Equal(c.RelativePath, profile)).IsEligible, "Obsolete profile was not eligible");
            foreach (var dependency in f.Retired)
                Assert(!firstPlan.Candidates.Single(c => Equal(c.RelativePath, dependency)).IsEligible,
                    "Profile dependency was eligible before its profile was removed: " + dependency);
            var first = f.Archive(firstPlan, selected);
            Assert(first.Files.Count == 1 && Equal(first.Files[0], profile), "First pass moved a referenced dependency");
            Assert(first.Skipped.Count == f.Retired.Length, "First pass did not report the protected dependencies");
            Assert(!File.Exists(f.PathFor(profile)), "First pass retained its selected profile");
            Assert(ReadFileIdentity(Path.Combine(first.BackupFolder!, profile)) == before[profile].Identity,
                "Archiving changed the profile's file identity");
            foreach (var dependency in f.Retired)
                Assert(before[dependency].Bytes.SequenceEqual(File.ReadAllBytes(f.PathFor(dependency))) &&
                    ReadFileIdentity(f.PathFor(dependency)) == before[dependency].Identity,
                    "First pass changed a protected dependency: " + dependency);

            var secondPlan = f.Plan();
            foreach (var dependency in f.Retired)
                Assert(secondPlan.Candidates.Single(c => Equal(c.RelativePath, dependency)).IsEligible,
                    "Fresh comparison did not release an unreferenced dependency: " + dependency);
            var second = f.Archive(secondPlan, f.Retired);
            Assert(second.Files.Count == f.Retired.Length && second.Skipped.Count == 0, "Second pass did not move all reviewed dependencies");
            foreach (var dependency in f.Retired)
            {
                var archived = Path.Combine(second.BackupFolder!, dependency.Replace('/', Path.DirectorySeparatorChar));
                Assert(!File.Exists(f.PathFor(dependency)), "Second pass left a selected dependency in place");
                Assert(before[dependency].Bytes.SequenceEqual(File.ReadAllBytes(archived)) &&
                    ReadFileIdentity(archived) == before[dependency].Identity,
                    "Archiving changed dependency bytes or identity: " + dependency);
            }

            Assert(f.Restore(second.BackupFolder!).Files.Count == f.Retired.Length, "Dependency backup did not restore completely");
            Assert(f.Restore(first.BackupFolder!).Files.Count == 1, "Profile backup did not restore completely");
            foreach (var path in selected)
                Assert(before[path].Bytes.SequenceEqual(File.ReadAllBytes(f.PathFor(path))) &&
                    ReadFileIdentity(f.PathFor(path)) == before[path].Identity,
                    "Restore changed original bytes or identity: " + path);
            Assert(currentSector.SequenceEqual(File.ReadAllBytes(f.PathFor(Fixture.NewStem + ".sct"))), "Cleanup or restore changed the current sector");
        });
        Test("archive rejects a junction substituted for the backup base", f =>
        {
            var outside = Path.Combine(Path.GetDirectoryName(f.Root)!, "redirected-backups");
            Directory.CreateDirectory(outside);
            SyntheticJunction.Create(f.BackupRoot, outside);
            MustReject(() => f.Archive(f.Plan(), [Fixture.OldDll]));
            Assert(File.Exists(f.PathFor(Fixture.OldDll)) && !Directory.EnumerateFileSystemEntries(outside).Any(), "Archive traversed backup junction");
        });
        Test("restore rejects a junction substituted for a destination parent", f =>
        {
            var result = f.Archive(f.Plan(), [Fixture.OldDll]);
            var outside = Path.Combine(Path.GetDirectoryName(f.Root)!, "saved-plugins");
            var original = Path.GetFullPath(f.PathFor("ESAA/Plugins"));
            var destination = Path.GetFullPath(outside);
            var boundary = Path.GetFullPath(Path.GetDirectoryName(f.Root)!) + Path.DirectorySeparatorChar;
            Assert(original.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) && destination.StartsWith(boundary, StringComparison.OrdinalIgnoreCase), "Fixture move escaped its root");
            Directory.Move(original, destination);
            SyntheticJunction.Create(original, destination);
            var restored = f.Restore(result.BackupFolder!);
            Assert(restored.Files.Count == 0 && restored.Skipped.Count == 1, "Restore followed destination junction");
            Assert(!File.Exists(Path.Combine(outside, "AFVBridge.dll")), "Restore wrote beyond destination junction");
        });
    }

    private void Test(string name, Action<Fixture> test)
    {
        if (filter is not null && !name.Equals(filter, StringComparison.Ordinal)) return;
        var f = new Fixture(Path.Combine(fixturesDirectory, (passed + failures.Count + 1).ToString("00")));
        try { test(f); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures.Add("FAIL " + name + ": " + ex.GetType().Name + ": " + ex.Message); Console.WriteLine(failures[^1]); }
    }
    private static void AssertProtected(Fixture f, string name) => Assert(f.Plan().Candidates.All(c => !Equal(c.RelativePath, name) || !c.IsEligible), "Unsafe eligible candidate " + name);
    private static bool Equal(string a, string b) => string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static (ulong Volume, Guid File) ReadFileIdentity(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandleEx(handle, 18, out var identity, (uint)Marshal.SizeOf<FileIdentity>()))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not read synthetic file identity");
        return (identity.Volume, identity.File);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity
    {
        public ulong Volume;
        public Guid File;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
        out FileIdentity information, uint size);
    private static void MustReject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException) { return; }
        throw new Exception("Expected operation rejection");
    }
}

sealed class Fixture
{
    public const string OldStem = "ESAA-Sweden_20260903120000-260901-0001";
    public const string NewStem = "ESAA-Sweden_20261001120000-261001-0003";
    public const string OldDll = "ESAA/Plugins/AFVBridge.dll";
    public string Root { get; }
    public string OldZip { get; }
    public string NewZip { get; }
    public string BackupRoot { get; }
    public string LockName { get; } = @"Local\Launchpad.GngCleanup.Tests." + Guid.NewGuid().ToString("N");
    public bool EuroScopeRunning { get; set; }
    public Action<int>? OnProcessProbe { get; set; }
    public Action? OnPendingProbe { get; set; }
    private int _processProbes;
    public GngCleanupService.OperationContext Context => new(BackupRoot, () =>
    {
        OnProcessProbe?.Invoke(++_processProbes);
        if (EuroScopeRunning) throw new InvalidOperationException("Synthetic EuroScope is running.");
    }, () =>
    {
        if (!MaintenanceLock.TryAcquire(LockName, out var lease)) throw new InvalidOperationException("Synthetic maintenance lock is busy.");
        return lease!;
    }, root =>
    {
        OnPendingProbe?.Invoke();
        GngUpdateService.RequireNoPendingUpdate(root, UpdateContext);
    });
    private GngUpdateService.OperationContext UpdateContext => new(Path.Combine(Path.GetDirectoryName(Root)!, "GngUpdateStorage-synthetic"), () => { }, () =>
    {
        if (!MaintenanceLock.TryAcquire(LockName, out var lease)) throw new InvalidOperationException("Synthetic maintenance lock is busy.");
        return lease!;
    });
    public Dictionary<string, string> OldEntries { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> NewEntries { get; } = new(StringComparer.Ordinal);
    public string[] Retired => [OldStem + ".sct", OldStem + ".ese", OldStem + ".rwy", OldDll];
    private static Encoding PrfEncoding => Encoding.GetEncoding(1252);

    public Fixture(string directory)
    {
        Root = Path.Combine(directory, "EuroScope-synthetic");
        BackupRoot = Path.Combine(directory, "CleanupBackups-synthetic");
        OldZip = Path.Combine(directory, "ESAA-Full-Package_20260903120000-260901-0001.zip");
        NewZip = Path.Combine(directory, "ESAA-Full-Package_20261001120000-261001-0003.zip");
        Directory.CreateDirectory(Root);
        foreach (var ext in new[] { ".sct", ".ese", ".rwy" })
        {
            OldEntries[OldStem + ext] = "fake old generated " + ext;
            NewEntries[NewStem + ext] = "fake new generated " + ext;
        }
        OldEntries[OldDll] = "fake old plugin DLL";
        OldEntries["ESAA/Plugins/Support.dll"] = "fake dependency DLL";
        OldEntries["ESAA_OLD.prf"] = "FakeName\tÅke Öström\r\nPlugins\tPlugin0\tESAA\\Plugins\\AFVBridge.dll\r\n";
        NewEntries["ESAA.prf"] = "FakeName\tÅke Öström\r\nSettings\tsector\t" + NewStem + ".sct\r\nPlugins\tPlugin0\tESAA\\Plugins\\RDF.dll\r\n";
        NewEntries["ESAA/Plugins/RDF.dll"] = "fake new plugin DLL";
        foreach (var item in OldEntries.Where(i => !i.Key.EndsWith(".prf"))) Write(item.Key, item.Value);
        foreach (var item in NewEntries) Write(item.Key, item.Value);
        SaveZips();
    }
    public string PathFor(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
    public void Write(string relative, string text)
    {
        var path = PathFor(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, PrfEncoding);
    }
    public void SaveZips(string wrapper = "")
    {
        SaveZip(OldZip, OldEntries, wrapper);
        SaveZip(NewZip, NewEntries, wrapper);
    }
    private static void SaveZip(string path, Dictionary<string, string> items, string wrapper)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var item in items)
        {
            var entry = zip.CreateEntry(wrapper + item.Key);
            using var writer = new StreamWriter(entry.Open(), PrfEncoding);
            writer.Write(item.Value);
        }
    }
    public VatscaUpdateChecker.Models.GngCleanupPlan Plan(params string[] protectedPaths)
        => GngCleanupService.BuildPlan(Root, OldZip, NewZip, Context, protectedPaths: protectedPaths);
    public void MarkPendingUpdate()
    {
        var update = GngUpdateService.BuildPlan(NewZip, Root, null, UpdateContext);
        var result = GngUpdateService.Install(update, null, UpdateContext);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(result.BackupFolder)!, "pending-update.json"),
            System.Text.Json.JsonSerializer.Serialize(new { result.BackupFolder }));
    }
    public VatscaUpdateChecker.Models.GngCleanupResult Archive(VatscaUpdateChecker.Models.GngCleanupPlan plan,
        IEnumerable<string> selected) => GngCleanupService.ArchiveSelected(plan, selected, Context);
    public VatscaUpdateChecker.Models.GngCleanupResult Restore(string backup, params string[] protectedPaths)
        => GngCleanupService.Restore(backup, Root, protectedPaths, Context);
}





static class RealPackageCheck
{
    public static void Run(string oldZip, string newZip)
    {
        var root = Path.Combine(Path.GetTempPath(), "VatscaCleanupTests", Guid.NewGuid().ToString("N"), "real-package-structure");
        Directory.CreateDirectory(root);
        try
        {
            var oldNames = CopyGeneratedFiles(oldZip, root);
            var newNames = CopyGeneratedFiles(newZip, root);
            var plan = GngCleanupService.BuildPlan(root, oldZip, newZip);
            var removed = oldNames.Except(newNames, StringComparer.OrdinalIgnoreCase).ToList();
            if (removed.Count == 0 || removed.Any(name => !plan.Candidates.Any(c => c.RelativePath == name && c.IsEligible)))
                throw new Exception("Expected removed old generated files to be eligible after installing current package SCT/ESE files into the isolated fixture");
            Console.WriteLine($"PASS real complete-package structure ({plan.Candidates.Count(c => c.IsEligible)} old generated files eligible)");
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL real complete-package structure: " + ex.GetType().Name + ": " + ex.Message);
            Environment.ExitCode = 1;
        }
        Console.WriteLine("REAL PACKAGE FIXTURE " + root);
    }

    private static List<string> CopyGeneratedFiles(string zipPath, string root)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var names = new List<string>();
        foreach (var entry in zip.Entries)
        {
            // Deliberately extract only root generated data. No PRFs, credentials or DLLs are copied.
            if (!System.Text.RegularExpressions.Regex.IsMatch(entry.FullName,
                @"\AESAA-Sweden_\d{14}-\d{6}-\d{4}\.(sct|ese)\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
            using var input = entry.Open();
            using var output = new FileStream(Path.Combine(root, entry.FullName), FileMode.Create, FileAccess.Write);
            input.CopyTo(output);
            names.Add(entry.FullName);
        }
        if (names.Count == 0) throw new Exception("No supported root SCT/ESE files were found in " + Path.GetFileName(zipPath));
        return names;
    }
}
