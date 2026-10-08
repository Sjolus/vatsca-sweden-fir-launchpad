using System.Text;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("These checks require Windows path semantics.");
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
var previewIndex = Array.IndexOf(args, "--preview");
if (previewIndex >= 0)
{
    if (previewIndex + 2 >= args.Length) throw new ArgumentException("--preview requires a ZIP and an existing isolated data folder.");
    var previewContext = new GngUpdateService.OperationContext(Path.Combine(Path.GetTempPath(), "VatscaUpdateTests", Guid.NewGuid().ToString("N"), "preview-storage"),
        () => { }, () => throw new InvalidOperationException("Preview mode cannot acquire an installation lease."));
    var plan = GngUpdateService.BuildPlan(args[previewIndex + 1], args[previewIndex + 2], null, previewContext);
    Console.WriteLine("PACKAGE " + plan.PackageName);
    foreach (var group in plan.Files.GroupBy(f => f.Action)) Console.WriteLine($"{group.Key}: {group.Count()}");
    foreach (var warning in plan.Warnings) Console.WriteLine("WARNING " + warning);
    return;
}
if (args.Contains("--ui-fixture", StringComparer.OrdinalIgnoreCase))
{
    var fixture = new Fixture(Path.Combine(Path.GetTempPath(), "VatscaUpdateTests", Guid.NewGuid().ToString("N"), "ui-preview"));
    Console.WriteLine("SYNTHETIC UI DATA " + fixture.Root);
    Console.WriteLine("SYNTHETIC UI ZIP " + fixture.ZipPath);
    return;
}
var filterIndex = Array.IndexOf(args, "--filter");
if (filterIndex >= 0 && filterIndex + 1 >= args.Length) throw new ArgumentException("--filter requires a test-name substring.");
var suite = new Suite(Path.Combine(Path.GetTempPath(), "VatscaUpdateTests", Guid.NewGuid().ToString("N")),
    filterIndex >= 0 ? args[filterIndex + 1] : null);
suite.Run();

sealed class Suite(string fixturesDirectory, string? filter)
{
    private int passed;
    private readonly List<string> failures = [];

    public void Run()
    {
        Directory.CreateDirectory(fixturesDirectory);
        TextDiffTests.Run(Test);
        FilePreviewTests.Run(Test);
        EuroScopeProcessGuardTests.Run(Test);
    ListLayoutTests.Run(Test);
    BackendListLayoutTests.Run(Test);
    PackageCacheTests.Run(Test);
        Test("download validation does not require access to installed profiles", f =>
        {
            using var locked = new FileStream(Path.Combine(f.Root, Fixture.ProfilePath), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert(GngUpdateService.ValidatePackage(f.ZipPath) == "2610/01 rev.3", "Archive version was not validated independently of the destination");
            MustReject(() => f.Plan());
        });
        Test("download validation rejects incomplete archives before requesting a reference", f =>
        {
            f.Entries.Remove(Fixture.NewStem + ".ese");
            f.SaveZip();
            MustReject(() => GngUpdateService.ValidatePackage(f.ZipPath));
        });
        Test("update-only package produces a nonmutating review", f =>
        {
            var before = f.Snapshot();
            var plan = f.Plan();
            Assert(plan.Files.Any(x => Equal(x.RelativePath, Fixture.NewStem + ".sct")), "Sector file missing from review");
            Assert(plan.Files.Any(x => Equal(x.RelativePath, Fixture.ProfilePath)), "Profile missing from review");
            AssertSnapshot(before, f.Snapshot());
            var review = JsonSerializer.Serialize(plan);
            Assert(!review.Contains(Fixture.FakePassword, StringComparison.Ordinal), "Review exposes profile password");
            Assert(!review.Contains(Fixture.FakeHoppie, StringComparison.Ordinal), "Review exposes Hoppie code");
        });
        Test("complete package is accepted", f =>
        {
            f.ZipPath = Path.Combine(f.DirectoryPath, "ESAA-Full-Package_" + Fixture.NewIdentity + ".zip");
            f.SaveZip();
            Assert(f.Plan().Files.Count > 0, "Empty complete-package plan");
        });
        Test("browser duplicate filename suffix is accepted", f =>
        {
            f.ZipPath = Path.Combine(f.DirectoryPath, "ESAA-Update-Only_" + Fixture.NewIdentity + " (2).zip");
            f.SaveZip();
            Assert(f.Plan().Files.Count > 0, "Browser-named package rejected");
        });
        Test("one common archive wrapper is accepted", f =>
        {
            f.SaveZip("GNG Package/");
            Assert(f.Plan().Files.Any(x => Equal(x.RelativePath, Fixture.NewStem + ".sct")), "Archive wrapper leaked into installed path");
        });
        Test("renamed unknown ZIP is rejected", f =>
        {
            f.ZipPath = Path.Combine(f.DirectoryPath, "unknown.zip");
            f.SaveZip();
            MustReject(() => f.Plan());
        });
        Test("non-ZIP content is rejected", f =>
        {
            File.WriteAllText(f.ZipPath, "synthetic HTML login response");
            MustReject(() => f.Plan());
        });
        Test("missing matching ESE is rejected", f =>
        {
            f.Entries.Remove(Fixture.NewStem + ".ese");
            f.SaveZip();
            MustReject(() => f.Plan());
        });
        Test("archive generation mismatching its filename is rejected", f =>
        {
            f.ZipPath = Path.Combine(f.DirectoryPath, "ESAA-Update-Only_20261001120001-261001-0003.zip");
            f.SaveZip();
            MustReject(() => f.Plan());
        });
        Test("missing package profile is rejected", f =>
        {
            f.Entries.Remove(Fixture.ProfilePath);
            f.SaveZip();
            MustReject(() => f.Plan());
        });
        Test("missing package plugins are rejected", f =>
        {
            foreach (var key in f.Entries.Keys.Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToArray()) f.Entries.Remove(key);
            f.SaveZip();
            MustReject(() => f.Plan());
        });
        Test("final validation rejects a stale packaged profile sector", f =>
        {
            f.Entries[Fixture.ProfilePath] = Fixture.PackageProfile.Replace(Fixture.NewStem + ".sct", Fixture.OldStem + ".sct", StringComparison.Ordinal);
            f.SaveZip();
            MustReject(() => f.Plan());
        });
        Test("final validation rejects duplicate packaged profile keys", f =>
        {
            f.Entries[Fixture.ProfilePath] += "LastSession\trealname\tDuplicate synthetic name\r\n";
            f.SaveZip();
            MustReject(() => f.Plan());
        });
        Test("final validation rejects staged promotion colliding with an archive directory", f =>
        {
            f.Entries.Remove("ESAA/Plugins/TopSky.dll");
            f.Entries["ESAA/Plugins/TopSky.dll/child.txt"] = "synthetic directory beneath would-be promoted DLL";
            f.Entries["ESAA/Plugins/Updated Plugin/TopSky.dll"] = "synthetic staged DLL";
            f.SaveZip();
            MustReject(() => f.Plan());
        });
        Test("final validation rejects update-only package for an empty installation", f =>
        {
            var empty = Path.Combine(f.DirectoryPath, "Empty-EuroScope-synthetic");
            Directory.CreateDirectory(empty);
            MustReject(() => GngUpdateService.BuildPlan(f.ZipPath, empty, null, f.Context));
            Assert(!Directory.EnumerateFileSystemEntries(empty).Any(), "Rejected new installation wrote files");
        });
        Test("final validation allows full package into empty installation with restore", f =>
        {
            var empty = Path.Combine(f.DirectoryPath, "Empty-EuroScope-synthetic");
            Directory.CreateDirectory(empty);
            f.ZipPath = Path.Combine(f.DirectoryPath, "ESAA-Full-Package_" + Fixture.NewIdentity + ".zip");
            f.SaveZip();
            var result = f.Install(GngUpdateService.BuildPlan(f.ZipPath, empty, null, f.Context));
            Assert(File.Exists(Path.Combine(empty, Fixture.NewStem + ".sct")), "Full package did not install a sector");
            Assert(File.Exists(Path.Combine(empty, Fixture.ProfilePath)), "Full package did not install a profile");
            GngUpdateService.Restore(result.BackupFolder, empty, null, null, f.Context);
            Assert(!Directory.EnumerateFiles(empty, "*", SearchOption.AllDirectories).Any(), "Restore left package files in the originally empty installation");
        });
        foreach (var unsafePath in new[] { "../escape.dll", "ESAA/../../escape.dll", "C:/escape.dll", "/escape.dll", "ESAA/Plugins/plugin.dll:stream", "ESAA/Plugins/CON.txt", "ESAA/Plugins/space .dll ", "ESAA/Plugins/../escape.dll" })
            Test("unsafe ZIP path rejected: " + unsafePath, f =>
            {
                f.AddRawEntry(unsafePath, "synthetic unsafe entry");
                MustReject(() => f.Plan());
            });
        Test("case-insensitive ZIP path collision is rejected", f =>
        {
            f.AddRawEntry("esaa/plugins/TOPSKY.DLL", "synthetic duplicate");
            MustReject(() => f.Plan());
        });
        Test("identical ZIP path duplicates are rejected", f =>
        {
            f.AddRawEntry("ESAA/Plugins/TopSky.dll", "synthetic duplicate");
            MustReject(() => f.Plan());
        });
        Test("ZIP file-directory collision is rejected", f =>
        {
            f.AddRawEntry("ESAA/Plugins/TopSky.dll/child.txt", "synthetic conflict");
            MustReject(() => f.Plan());
        });
        Test("ZIP Unix symbolic-link entry is rejected", f =>
        {
            f.AddRawEntry("ESAA/Plugins/linked.dll", "../../outside.dll", unchecked((int)0xA1FF0000));
            MustReject(() => f.Plan());
        });
        Test("ZIP Windows reparse-point entry is rejected", f =>
        {
            f.AddRawEntry("ESAA/Plugins/linked.dll", "synthetic reparse", (int)FileAttributes.ReparsePoint);
            MustReject(() => f.Plan());
        });
        Test("local directory at package file destination is rejected", f =>
        {
            Directory.CreateDirectory(f.PathFor("ESAA/Plugins/RDFPlugin.dll"));
            MustReject(() => f.Plan());
        });
        Test("local file at package parent directory is rejected", f =>
        {
            f.Write("ESAA/ASR", "synthetic blocking file");
            f.Entries["ESAA/ASR/new.asr"] = "synthetic ASR";
            f.SaveZip();
            MustReject(() => f.Plan());
        });
        Test("lower revision is rejected even with a later timestamp", f =>
        {
            f.Write("ESAA-Sweden_20260930120000-261001-0004.sct", "synthetic later installed version");
            MustReject(() => f.Plan());
        });
        Test("lower AIRAC is rejected even with a later timestamp", f =>
        {
            f.Write("ESAA-Sweden_20260930120000-261101-0001.sct", "synthetic higher AIRAC installed");
            MustReject(() => f.Plan());
        });
        Test("lower AIRAC package number is rejected", f =>
        {
            f.Write("ESAA-Sweden_20260930120000-261002-0001.sct", "synthetic higher AIRAC package installed");
            MustReject(() => f.Plan());
        });
        Test("same generation repair supplies missing font and updated DLL with a warning", f =>
        {
            f.Write(Fixture.NewStem + ".sct", f.Entries[Fixture.NewStem + ".sct"]);
            f.Write(Fixture.NewStem + ".ese", f.Entries[Fixture.NewStem + ".ese"]);
            var plan = f.Plan();
            Assert(plan.Warnings.Any(w => w.Contains("repair", StringComparison.OrdinalIgnoreCase) || w.Contains("same", StringComparison.OrdinalIgnoreCase) || w.Contains("reinstall", StringComparison.OrdinalIgnoreCase)), "Repairing current generation has no review warning");
            f.Install(plan);
            Assert(f.Read("ESAA/Plugins/TopSky.dll") == f.Entries["ESAA/Plugins/TopSky.dll"], "Same-generation repair left an old DLL");
            Assert(f.Read("ESAA/TopSky.ttf") == f.Entries["ESAA/TopSky.ttf"], "Same-generation repair did not add the missing font");
        });
        Test("newer timestamp within same AIRAC and revision can be reviewed", f =>
        {
            f.Write("ESAA-Sweden_20261001115959-261001-0003.sct", "synthetic earlier full-package sector");
            f.Write("ESAA-Sweden_20261001115959-261001-0003.ese", "synthetic earlier full-package extension");
            Assert(f.Plan().Files.Count > 0, "Later update-only generation was rejected");
        });
        Test("package changed after review is rejected without writing", f =>
        {
            var plan = f.Plan();
            f.Entries["ESAA/Plugins/TopSky.dll"] = "synthetic package changed after preview";
            f.SaveZip();
            var before = f.Snapshot();
            MustReject(() => f.Install(plan));
            AssertSnapshot(before, f.Snapshot());
        });
        Test("local replacement changed after review is rejected without writing", f =>
        {
            var plan = f.Plan();
            f.Write("ESAA/Plugins/TopSky.dll", "synthetic local edit after preview");
            var before = f.Snapshot();
            MustReject(() => f.Install(plan));
            AssertSnapshot(before, f.Snapshot());
        });
        Test("new destination created after review is rejected without writing", f =>
        {
            var plan = f.Plan();
            f.Write("ESAA/Plugins/RDFPlugin.dll", "synthetic local file appeared after preview");
            var before = f.Snapshot();
            MustReject(() => f.Install(plan));
            AssertSnapshot(before, f.Snapshot());
        });
        Test("local replacement removed after review is rejected without writing", f =>
        {
            var plan = f.Plan();
            File.Delete(f.PathFor("ESAA/Plugins/TopSky.dll"));
            var before = f.Snapshot();
            MustReject(() => f.Install(plan));
            AssertSnapshot(before, f.Snapshot());
        });
        Test("installation keeps Windows-1252 session fields and adopts new sector", f =>
        {
            f.Install(f.Plan());
            var profile = f.Read(Fixture.ProfilePath);
            foreach (var line in Fixture.LocalProfile.Split("\r\n").Where(s => s.StartsWith("LastSession\t", StringComparison.Ordinal)))
                Assert(profile.Split("\r\n").Contains(line), "A local LastSession field was not preserved");
            Assert(profile.Contains(Fixture.NewStem + ".sct", StringComparison.Ordinal), "Profile still references old sector");
            Assert(!profile.Contains(Fixture.OldStem + ".sct", StringComparison.Ordinal), "Old sector reference retained");
            Assert(profile.Contains("Åke Öström", StringComparison.Ordinal), "Windows-1252 Swedish name corrupted");
            Assert(f.Bytes(Fixture.ProfilePath).Contains((byte)0xC5), "Swedish name was converted away from Windows-1252");
            Assert(!profile.Contains("package-placeholder", StringComparison.Ordinal), "Package password replaced local session");
        });
        Test("installation keeps VatEFS displays while adopting new package plugins", f =>
        {
            f.Install(f.Plan());
            var lines = f.Read(Fixture.ProfilePath).Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
            var pluginRows = lines.Select(l => l.Split('\t')).Where(p => p.Length >= 3 && p[0] == "Plugins" && Regex.IsMatch(p[1], @"^Plugin\d+$")).ToArray();
            Assert(pluginRows.Any(p => p[2].EndsWith("RDFPlugin.dll", StringComparison.OrdinalIgnoreCase)), "New RDF plugin was not adopted");
            Assert(!pluginRows.Any(p => p[2].EndsWith("AfvEuroScopeBridge.dll", StringComparison.OrdinalIgnoreCase)), "Retired AFV plugin remained enabled");
            var vatEfs = pluginRows.SingleOrDefault(p => p[2].EndsWith("VatEFS.dll", StringComparison.OrdinalIgnoreCase));
            Assert(vatEfs is not null, "Custom VatEFS plugin was removed");
            foreach (var display in new[] { "Ground Radar display", "Standard ES radar screen" })
                Assert(lines.Any(l => l.StartsWith("Plugins\t" + vatEfs![1] + "Display", StringComparison.Ordinal) && l.EndsWith("\t" + display, StringComparison.Ordinal)), "VatEFS display lost");
            var indices = pluginRows.Select(p => int.Parse(p[1][6..])).Order().ToArray();
            Assert(indices.SequenceEqual(Enumerable.Range(0, indices.Length)), "Plugin slots are not dense and unique");
            Assert(File.Exists(f.PathFor("ESAA/Plugins/AfvEuroScopeBridge.dll")), "Installer removed a retired file without cleanup review");
        });
        Test("existing local overrides Hoppie and login profiles remain byte-identical", f =>
        {
            var protectedPaths = new[] { "ESAA/Plugins/TopSkySettingsLocal.txt", "ESAA/Plugins/TopSkyCPDLChoppieCode.txt", "ESAA/Settings/LoginProfiles.txt" };
            var expected = protectedPaths.ToDictionary(p => p, f.Bytes);
            f.Entries[protectedPaths[0]] = "synthetic package local defaults";
            f.Entries[protectedPaths[1]] = "synthetic package Hoppie defaults";
            f.SaveZip();
            var plan = f.Plan();
            foreach (var path in protectedPaths)
            {
                var file = plan.Files.Single(file => Equal(file.RelativePath, path));
                Assert(file.Action == "Keep personal file" && !file.WritesFile, "Review does not distinguish preserved personal files: " + path);
                Assert(file.BeforeHash == file.AfterHash, "Preserved personal file is marked for modification: " + path);
            }
            f.Install(plan);
            foreach (var path in protectedPaths) Assert(expected[path].SequenceEqual(f.Bytes(path)), "Protected local file overwritten: " + path);
        });
        Test("RDF replacement retires AFV profile entry even at a custom external path", f =>
        {
            f.Write(Fixture.ProfilePath, Fixture.LocalProfile.Replace("ESAA\\Plugins\\AfvEuroScopeBridge.dll", "C:\\Synthetic-Custom\\AfvEuroScopeBridge.dll", StringComparison.Ordinal));
            f.Install(f.Plan());
            var profile = f.Read(Fixture.ProfilePath);
            Assert(profile.Contains("RDFPlugin.dll", StringComparison.OrdinalIgnoreCase), "Replacement RDF plugin missing");
            Assert(!profile.Contains("AfvEuroScopeBridge.dll", StringComparison.OrdinalIgnoreCase), "Incompatible external AFV bridge remained enabled");
            Assert(profile.Contains("VatEFS.dll", StringComparison.OrdinalIgnoreCase), "Unrelated external custom plugin was removed");
        });
        Test("custom profile and local-only PRF key survive installation", f =>
        {
            f.Write(Fixture.ProfilePath, Fixture.LocalProfile + "CustomSettings\tOperatorChoice\tÅÄÖ-local\r\n");
            var custom = f.Bytes("Custom.prf");
            f.Install(f.Plan());
            Assert(custom.SequenceEqual(f.Bytes("Custom.prf")), "Non-package custom profile changed");
            Assert(f.Read(Fixture.ProfilePath).Contains("CustomSettings\tOperatorChoice\tÅÄÖ-local", StringComparison.Ordinal), "Local-only profile field lost");
        });
        Test("staged package DLL supersedes active package DLL and restores safely", f =>
        {
            var original = f.Bytes("ESAA/Plugins/TopSky.dll");
            f.Write("ESAA/Plugins/Updated Plugin/Unrelated.dll", "synthetic unrelated local staged DLL");
            f.Entries["ESAA/Plugins/TopSky.dll"] = "synthetic older active package DLL";
            f.Entries["ESAA/Plugins/Updated Plugin/TopSky.dll"] = "synthetic newer staged package DLL";
            f.SaveZip();
            var plan = f.Plan();
            Assert(plan.Files.Any(p => Equal(p.RelativePath, "ESAA/Plugins/TopSky.dll") &&
                (p.Detail.Contains("staged", StringComparison.OrdinalIgnoreCase) || p.Detail.Contains("Updated Plugin", StringComparison.OrdinalIgnoreCase))), "Active DLL review omits staging promotion");
            var result = f.Install(plan);
            Assert(f.Read("ESAA/Plugins/TopSky.dll") == "synthetic newer staged package DLL", "Staged plugin was not promoted to the active path");
            Assert(f.Read("ESAA/Plugins/Updated Plugin/Unrelated.dll") == "synthetic unrelated local staged DLL", "Unrelated local staging was changed");
            f.Restore(result.BackupFolder);
            Assert(original.SequenceEqual(f.Bytes("ESAA/Plugins/TopSky.dll")), "Original active plugin was not restored");
        });
        Test("replaced files are backed up and restore returns exact original tree", f =>
        {
            var before = f.Snapshot();
            var result = f.Install(f.Plan());
            Assert(result.InstalledFileCount > 0, "Installer reported no files");
            Assert(Directory.Exists(result.BackupFolder), "Recovery folder missing");
            Assert(!IsWithin(f.Root, result.BackupFolder), "Recovery folder is inside active EuroScope data");
            Assert(Directory.EnumerateFiles(result.BackupFolder, "*", SearchOption.AllDirectories).Any(p => File.ReadAllBytes(p).SequenceEqual(before[Fixture.ProfilePath])), "Original profile bytes are absent from backup");
            f.Restore(result.BackupFolder);
            AssertSnapshot(before, f.Snapshot());
        });
        Test("restore preserves postinstall edits to replaced files", f =>
        {
            var result = f.Install(f.Plan());
            f.Write("ESAA/Plugins/TopSky.dll", "synthetic postinstall local edit");
            var restored = f.Restore(result.BackupFolder);
            Assert(f.Read("ESAA/Plugins/TopSky.dll") == "synthetic postinstall local edit", "Restore overwrote a postinstall edit");
            Assert(restored.SkippedFiles.Any(), "Skipped edited file was not reported");
            Assert(GngUpdateService.GetPendingUpdate(f.Root, f.Context) is not null, "Incomplete recovery lost its pending marker");
            MustReject(() => f.Plan());
        });
        Test("restore preserves postinstall edits to newly added files", f =>
        {
            var result = f.Install(f.Plan());
            f.Write("ESAA/Plugins/RDFPlugin.dll", "synthetic postinstall new-file edit");
            var restored = f.Restore(result.BackupFolder);
            Assert(f.Read("ESAA/Plugins/RDFPlugin.dll") == "synthetic postinstall new-file edit", "Restore removed edited new file");
            Assert(restored.SkippedFiles.Any(), "Skipped edited new file was not reported");
        });
        Test("backup preserves exact downloaded package bytes", f =>
        {
            var packageBytes = File.ReadAllBytes(f.ZipPath);
            var result = f.Install(f.Plan());
            Assert(result.CachedPackagePath is not null && File.ReadAllBytes(result.CachedPackagePath).SequenceEqual(packageBytes), "Result does not identify an exact package cache");
            Assert(GngUpdateService.GetCachedPackage(f.Root, f.Context) == result.CachedPackagePath, "Current package lookup does not return the installed archive");
            Assert(IsWithin(f.StorageRoot, result.BackupFolder), "Test recovery files escaped the isolated storage root");
            var backupParent = Directory.GetParent(result.BackupFolder)!.FullName;
            var cached = Directory.EnumerateFiles(backupParent, "*.zip", SearchOption.AllDirectories)
                .Where(path => File.ReadAllBytes(path).SequenceEqual(packageBytes)).ToArray();
            Assert(cached.Length > 0, "No exact downloaded ZIP was retained beside recovery data");
            Assert(cached.Any(p => Path.GetFileName(p).Contains(Fixture.NewIdentity, StringComparison.Ordinal)), "Cached ZIP lost package generation identity");
            var manifests = Directory.EnumerateFiles(backupParent, "*.json", SearchOption.AllDirectories).Select(File.ReadAllText).ToArray();
            Assert(manifests.Any(m => m.Contains(Fixture.NewIdentity, StringComparison.Ordinal)), "Recovery/cache manifest lost package generation");
            Assert(manifests.All(m => !m.Contains(Fixture.FakePassword, StringComparison.Ordinal) && !m.Contains(Fixture.FakeHoppie, StringComparison.Ordinal)), "A manifest exposes credential contents");
        });
        Test("restore does not overwrite a modified backup file", f =>
        {
            var original = f.Bytes("ESAA/Plugins/TopSky.dll");
            var result = f.Install(f.Plan());
            var backedUpFile = Directory.EnumerateFiles(Path.Combine(result.BackupFolder, "originals"), "*", SearchOption.AllDirectories)
                .Single(p => File.ReadAllBytes(p).SequenceEqual(original));
            File.WriteAllText(backedUpFile, "synthetic tampered backup");
            var current = f.Bytes("ESAA/Plugins/TopSky.dll");
            try { f.Restore(result.BackupFolder); }
            catch (Exception ex) when (IsRejection(ex)) { }
            Assert(current.SequenceEqual(f.Bytes("ESAA/Plugins/TopSky.dll")), "Tampered backup was restored");
        });
        Test("install failure does not leave partially updated data", f =>
        {
            var plan = f.Plan();
            var before = f.Snapshot();
            using (var locked = new FileStream(f.PathFor("ESAA/Plugins/TopSky.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
                MustReject(() => f.Install(plan));
            AssertSnapshot(before, f.Snapshot());
        });
        Test("failure after first installed file rolls back all data and clears pending state", f =>
        {
            var before = f.Snapshot();
            var installing = 0;
            var progress = new SynchronousProgress(message =>
            {
                if (message.StartsWith("Installing ", StringComparison.Ordinal) && ++installing == 2)
                    throw new IOException("Synthetic interruption after first installed file");
            });
            MustReject(() => f.Install(f.Plan(), progress));
            Assert(installing >= 2, "Failure did not reach the transactional installation phase");
            AssertSnapshot(before, f.Snapshot());
            Assert(GngUpdateService.GetPendingUpdate(f.Root, f.Context) is null, "Successful rollback left a recovery blocker");
        });
        Test("recovery rejects manifest traversal without touching active files", f =>
        {
            var result = f.Install(f.Plan());
            var manifestPath = Path.Combine(result.BackupFolder, "update-manifest.json");
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
            manifest["Files"]![0]!["RelativePath"] = "../escape.dll";
            File.WriteAllText(manifestPath, manifest.ToJsonString());
            var before = f.Snapshot();
            MustReject(() => f.Restore(result.BackupFolder));
            AssertSnapshot(before, f.Snapshot());
        });
        Test("recovery rejects duplicate manifest destinations", f =>
        {
            var result = f.Install(f.Plan());
            var manifestPath = Path.Combine(result.BackupFolder, "update-manifest.json");
            var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
            var files = manifest["Files"]!.AsArray();
            files.Add(files[0]!.DeepClone());
            File.WriteAllText(manifestPath, manifest.ToJsonString());
            var before = f.Snapshot();
            MustReject(() => f.Restore(result.BackupFolder));
            AssertSnapshot(before, f.Snapshot());
        });
        Test("tampered package cache is not returned as a usable baseline", f =>
        {
            var result = f.Install(f.Plan());
            Assert(result.CachedPackagePath is not null, "Cache path missing");
            File.WriteAllText(result.CachedPackagePath!, "synthetic altered cached archive");
            Assert(GngUpdateService.GetCachedPackage(f.Root, f.Context) is null, "Modified cached package was trusted");
        });
        Test("two managed installations restore in order with matching cache metadata", f =>
        {
            var original = f.Snapshot();
            var first = f.Install(f.Plan());
            var firstState = f.Snapshot();
            f.Entries["ESAA/Plugins/TopSky.dll"] = "synthetic same-generation repaired DLL";
            f.SaveZip();
            var second = f.Install(f.Plan());
            Assert(GngUpdateService.GetCachedPackage(f.Root, f.Context) == second.CachedPackagePath, "Second install did not become current baseline");
            MustReject(() => f.Restore(first.BackupFolder));
            f.Restore(second.BackupFolder);
            AssertSnapshot(firstState, f.Snapshot());
            Assert(GngUpdateService.GetCachedPackage(f.Root, f.Context) == first.CachedPackagePath, "Restore did not return the prior package baseline");
            f.Restore(first.BackupFolder);
            AssertSnapshot(original, f.Snapshot());
            Assert(GngUpdateService.GetCachedPackage(f.Root, f.Context) is null, "Original unmanaged state still has a managed baseline");
        });
        Test("new profiles omit package credentials and normalize singular staged plugin paths", f =>
        {
            f.Entries["ESAA NEW.prf"] = Fixture.PackageProfile.Replace(@"ESAA\Plugins\TopSky.dll", @"ESAA\Plugins\Updated Plugin\TopSky.dll");
            f.Entries["ESAA/Plugins/Updated Plugin/TopSky.dll"] = "synthetic promoted TopSky";
            f.SaveZip();
            var plan = f.Plan();
            Assert(plan.Warnings.Any(w => w.Contains("ESAA NEW.prf") && w.Contains("controller settings")), "New profile has no personal-settings review warning");
            f.Install(plan);
            var text = f.Read("ESAA NEW.prf");
            Assert(!text.Contains("LastSession", StringComparison.OrdinalIgnoreCase), "Package credentials were imported into a new profile");
            Assert(!text.Contains("Updated Plugin", StringComparison.OrdinalIgnoreCase), "New profile points at moved staged DLL");
            Assert(text.Contains(@"ESAA\Plugins\TopSky.dll"), "New profile is missing active plugin location");
        });
        Test("fresh Full Package normalizes plural staged plugin paths without importing personal fields", f =>
        {
            var empty = Path.Combine(f.DirectoryPath, "Fresh-synthetic"); Directory.CreateDirectory(empty);
            f.ZipPath = Path.Combine(f.DirectoryPath, "ESAA-Full-Package_" + Fixture.NewIdentity + ".zip");
            f.Entries[Fixture.ProfilePath] = Fixture.PackageProfile.Replace(@"ESAA\Plugins\TopSky.dll", @"ESAA\Plugins\Updated Plugins\TopSky.dll") + "TeamSpeakVccs\tPassword\tfake-package-voice-password\r\nSettings\tAselKey\tpackage-key\r\n";
            f.Entries["ESAA/Plugins/Updated Plugins/TopSky.dll"] = "synthetic promoted TopSky";
            f.SaveZip();
            f.Install(GngUpdateService.BuildPlan(f.ZipPath, empty, null, f.Context));
            var text = File.ReadAllText(Path.Combine(empty, Fixture.ProfilePath), Fixture.ProfileEncoding);
            Assert(!text.Contains("LastSession") && !text.Contains("TeamSpeakVccs"), "New profile imported personal package fields");
            Assert(text.Contains("Settings\tAselKey\tpackage-key"), "Fresh profile lost its nonsecret package shortcut default");
            Assert(text.Contains(@"ESAA\Plugins\TopSky.dll") && !text.Contains("Updated Plugins"), "Fresh profile plugin path is stale");
        });
        Test("OBS omission and personal keys survive profile merge without package credentials", f =>
        {
            var local = string.Join("\r\n", Fixture.LocalProfile.Split("\r\n").Where(l => !l.StartsWith("LastSession\t"))) +
                "TeamSpeakVccs\tPassword\tsynthetic-local-voice\r\nSettings\tAselKey\tlocal-a\r\nSettings\tFreqKey\tlocal-f\r\n";
            f.Write(Fixture.ProfilePath, local);
            f.Entries[Fixture.ProfilePath] += "TeamSpeakVccs\tPassword\tsynthetic-package-voice\r\nSettings\tAselKey\tpackage-a\r\nSettings\tFreqKey\tpackage-f\r\n";
            f.SaveZip(); f.Install(f.Plan());
            var text = f.Read(Fixture.ProfilePath);
            Assert(!text.Contains("LastSession"), "OBS profile omission replaced with package credentials/rating");
            foreach (var value in new[] { "synthetic-local-voice", "local-a", "local-f" }) Assert(text.Contains(value), "Personal key changed");
            Assert(!text.Contains("synthetic-package-voice") && !text.Contains("package-a") && !text.Contains("package-f"), "Package personal values imported");
        });
        Test("non-slot plugin preferences use the local value once", f =>
        {
            f.Write(Fixture.ProfilePath, Fixture.LocalProfile + "Plugins\tOperatorChoice\tlocal-choice\r\n");
            f.Entries[Fixture.ProfilePath] += "Plugins\tOperatorChoice\tpackage-choice\r\n";
            f.SaveZip(); f.Install(f.Plan());
            var lines = f.Read(Fixture.ProfilePath).Split("\r\n").Where(l => l.StartsWith("Plugins\tOperatorChoice\t")).ToArray();
            Assert(lines.SequenceEqual(new[] { "Plugins\tOperatorChoice\tlocal-choice" }), "Plugin preference has conflicting duplicate keys");
        });
        Test("external legacy RDF is replaced by package path with custom RDF displays preserved", f =>
        {
            var outside = Path.Combine(f.DirectoryPath, "External-RDF.dll"); File.WriteAllText(outside, "synthetic legacy RDF bytes");
            var plugin = Path.Combine(f.DirectoryPath, "RDFPlugin.dll"); File.Copy(outside, plugin);
            f.Write(Fixture.ProfilePath, Fixture.LocalProfile + "Plugins\tPlugin7\t" + plugin + "\r\nPlugins\tPlugin7Display8\tCustom display\r\nPlugins\tPlugin7Display2\tStandard ES radar screen\r\n");
            f.Install(f.Plan());
            var text = f.Read(Fixture.ProfilePath);
            Assert(!text.Contains(plugin) && !text.Contains("AfvEuroScopeBridge.dll"), "Legacy audio plugin remained loaded");
            Assert(text.Contains(@"ESAA\Plugins\RDFPlugin.dll") && text.Contains("Custom display"), "Replacement lost RDF path or displays");
            Assert(File.ReadAllText(plugin) == "synthetic legacy RDF bytes", "External legacy plugin bytes changed");
            var rdf = text.Split("\r\n").Select(l => l.Split('\t')).Single(p => p.Length == 3 && p[2] == @"ESAA\Plugins\RDFPlugin.dll");
            var screens = text.Split("\r\n").Select(l => l.Split('\t')).Where(p => p.Length == 3 && p[1].StartsWith(rdf[1] + "Display")).Select(p => p[2]).ToArray();
            Assert(screens.Length == screens.Distinct().Count(), "RDF display values duplicated during sparse-slot merge");
        });
        Test("packaged AFV and RDF co-loading is rejected", f =>
        {
            f.Entries[Fixture.ProfilePath] += "Plugins\tPlugin9\tESAA\\Plugins\\AfvEuroScopeBridge.dll\r\n";
            f.Entries["ESAA/Plugins/AfvEuroScopeBridge.dll"] = "synthetic conflicting audio DLL";
            f.SaveZip(); MustReject(() => f.Plan());
        });
        Test("multiple packaged RDF versions are rejected", f =>
        {
            f.Entries[Fixture.ProfilePath] += "Plugins\tPlugin9\tESAA\\Plugins\\RDF.dll\r\n";
            f.Entries["ESAA/Plugins/RDF.dll"] = "synthetic conflicting older RDF";
            f.SaveZip(); MustReject(() => f.Plan());
        });
        Test("a previously bundled support DLL remains when the user loads it as a custom plugin", f =>
        {
            f.Entries["ESAA/Plugins/Helper.dll"] = "synthetic bundled support DLL";
            f.SaveZip(); f.Install(f.Plan());
            f.Write(Fixture.ProfilePath, f.Read(Fixture.ProfilePath) + "Plugins\tPlugin9\tESAA\\Plugins\\Helper.dll\r\nPlugins\tPlugin9Display0\tCustom helper screen\r\n");
            f.Entries.Remove("ESAA/Plugins/Helper.dll"); f.SaveZip(); f.Install(f.Plan());
            Assert(f.Read(Fixture.ProfilePath).Contains("Helper.dll"), "Bundled-but-never-loaded helper was incorrectly classified as a retired managed plugin");
        });
        Test("a previously profile-loaded managed DLL can retire after verified package comparison", f =>
        {
            f.Entries["ESAA/Plugins/Retired.dll"] = "synthetic former managed plugin";
            f.Entries[Fixture.ProfilePath] += "Plugins\tPlugin9\tESAA\\Plugins\\Retired.dll\r\n";
            f.SaveZip(); f.Install(f.Plan());
            f.Entries.Remove("ESAA/Plugins/Retired.dll"); f.Entries[Fixture.ProfilePath] = Fixture.PackageProfile;
            f.SaveZip(); f.Install(f.Plan());
            Assert(!f.Read(Fixture.ProfilePath).Contains("Retired.dll"), "Retired managed profile slot was retained");
            Assert(File.Exists(f.PathFor("ESAA/Plugins/Retired.dll")), "Installer silently deleted a retired plugin file");
        });
        Test("local RDF setting is preserved with an explicit compatibility warning", f =>
        {
            f.Write("ESAA/Plugins/TopSkySettingsLocal.txt", "RDF_Mode=99\r\n");
            var plan = f.Plan(); Assert(plan.Warnings.Any(w => w.Contains("RDF_Mode") && w.Contains("does not change")), "Local RDF override has no warning");
            f.Install(plan); Assert(f.Read("ESAA/Plugins/TopSkySettingsLocal.txt") == "RDF_Mode=99\r\n", "Updater silently changed local RDF mode");
        });
        Test("Full cache provenance is reported only from a verified complete previous package", f =>
        {
            f.ZipPath = Path.Combine(f.DirectoryPath, "ESAA-Full-Package_" + Fixture.NewIdentity + ".zip"); f.SaveZip();
            var first = f.Install(f.Plan()); Assert(first.PreviousCompletePackagePath is null, "First install invented baseline provenance");
            var second = f.Install(f.Plan()); Assert(second.PreviousCompletePackagePath == first.CachedPackagePath, "Verified complete baseline missing");
            File.WriteAllText(second.CachedPackagePath!, "synthetic tampered full cache");
            var third = f.Install(f.Plan()); Assert(third.PreviousCompletePackagePath is null, "Tampered full cache offered for cleanup");
        });
        Test("Update-Only cache is never offered as an original complete cleanup baseline", f =>
        {
            f.Install(f.Plan()); var next = f.Install(f.Plan());
            Assert(next.PreviousCompletePackagePath is null, "Update-Only cache misrepresented as full package");
        });
        Test("same-version Full package with older build timestamp explains the ordering restriction", f =>
        {
            f.Write("ESAA-Sweden_20261001120054-261001-0003.sct", "synthetic later Update-Only sector");
            f.ZipPath = Path.Combine(f.DirectoryPath, "ESAA-Full-Package_" + Fixture.NewIdentity + ".zip"); f.SaveZip();
            try { f.Plan(); throw new Exception("Earlier build accepted without package evidence"); }
            catch (InvalidOperationException ex) { Assert(ex.Message.Contains("same AIRAC/revision") && ex.Message.Contains("timestamp"), "Ordering diagnostic hides Full/Update-Only distinction"); }
        });
        Test("configured missing application destination and overly broad paths block the preview", f =>
        {
            MustReject(() => f.Plan(new[] { f.PathFor("ESAA/Plugins/RDFPlugin.dll") }));
            MustReject(() => f.Plan(new[] { f.DirectoryPath }));
            Assert(!Directory.Exists(f.StorageRoot), "Rejected preview created recovery files");
        });
        Test("Launchpad storage protection is allowed while package destinations stay separate", f =>
        {
            var plan = f.Plan(new[] { f.StorageRoot }); f.Install(plan);
            Assert(File.Exists(f.PathFor(Fixture.NewStem + ".sct")), "Expected protected Launchpad storage prevented unrelated GNG install");
        });
        Test("data and cache containment is rejected in both directions", f =>
        {
            MustReject(() => GngUpdateService.BuildPlan(f.ZipPath, f.Root, null, f.Context with { StorageRoot = f.PathFor("Backups") }));
            MustReject(() => GngUpdateService.BuildPlan(f.ZipPath, f.Root, null, f.Context with { StorageRoot = f.DirectoryPath }));
            Assert(!Directory.Exists(f.PathFor("Backups")), "Rejected overlap created backup directory");
        });
        Test("occupied maintenance gate blocks installation before storage writes", f =>
        {
            var plan = f.Plan(); Assert(MaintenanceLock.TryAcquire(f.GateName, out var lease), "Could not take synthetic gate");
            using (lease) MustReject(() => f.Install(plan));
            Assert(!Directory.Exists(f.StorageRoot), "Blocked operation created recovery files");
        });
        Test("synthetic running EuroScope guard blocks install and restore", f =>
        {
            var plan = f.Plan(); f.RequireClosed = () => throw new InvalidOperationException("Synthetic EuroScope is running.");
            MustReject(() => f.Install(plan)); Assert(!Directory.Exists(f.StorageRoot), "Running guard wrote recovery files");
            f.RequireClosed = () => { }; var result = f.Install(plan); var before = f.Snapshot();
            f.RequireClosed = () => throw new InvalidOperationException("Synthetic EuroScope is running.");
            MustReject(() => f.Restore(result.BackupFolder)); AssertSnapshot(before, f.Snapshot());
        });
        Test("restore rejects a different reviewed root and newly protected destination", f =>
        {
            var result = f.Install(f.Plan()); var before = f.Snapshot();
            var other = Path.Combine(f.DirectoryPath, "Other-synthetic"); Directory.CreateDirectory(other);
            MustReject(() => GngUpdateService.Restore(result.BackupFolder, other, null, null, f.Context));
            MustReject(() => f.Restore(result.BackupFolder, new[] { f.PathFor(Fixture.ProfilePath) }));
            AssertSnapshot(before, f.Snapshot());
        });
        foreach (var mutation in new[] { "null-file", "oversize", "invalid-status", "redirected-root" })
            Test("recovery rejects damaged metadata: " + mutation, f =>
            {
                var result = f.Install(f.Plan()); var before = f.Snapshot();
                var path = Path.Combine(result.BackupFolder, "update-manifest.json"); var json = JsonNode.Parse(File.ReadAllText(path))!;
                if (mutation == "null-file") json["Files"]![0] = null;
                if (mutation == "oversize") json["Files"]![0]!["AfterLength"] = long.MaxValue;
                if (mutation == "invalid-status") json["Status"] = "unknown";
                if (mutation == "redirected-root") json["DataFolder"] = f.DirectoryPath;
                File.WriteAllText(path, json.ToJsonString()); MustReject(() => f.Restore(result.BackupFolder)); AssertSnapshot(before, f.Snapshot());
            });
        Test("damaged pending marker blocks later installs without trusting its destination", f =>
        {
            var result = f.Install(f.Plan()); var bucket = Path.GetDirectoryName(result.BackupFolder)!;
            File.WriteAllText(Path.Combine(bucket, "pending-update.json"), "{\"BackupFolder\":\"C:\\\\Outside-synthetic\"}");
            MustReject(() => f.Plan()); Assert(File.Exists(Path.Combine(bucket, "pending-update.json")), "Damaged recovery marker silently removed");
        });
        Test("Local-named profiles and DLLs are updated while local configuration remains preserved", f =>
        {
            f.Write("ESAA Local.prf", Fixture.LocalProfile);
            f.Write("ESAA/Plugins/LocalPlugin.dll", "synthetic old local-named DLL");
            f.Entries["ESAA Local.prf"] = Fixture.PackageProfile;
            f.Entries["ESAA/Plugins/LocalPlugin.dll"] = "synthetic new local-named DLL";
            f.SaveZip(); f.Install(f.Plan());
            Assert(f.Read("ESAA Local.prf").Contains(Fixture.NewStem), "Local-named profile retained old sector");
            Assert(!f.Read("ESAA Local.prf").Contains("AfvEuroScopeBridge.dll"), "Local-named profile skipped audio migration");
            Assert(f.Read("ESAA/Plugins/LocalPlugin.dll") == "synthetic new local-named DLL", "Local-named DLL was incorrectly preserved as settings");
        });
        Test("Update-Only permits missing ZIP DLL only when installed dependency exists", f =>
        {
            f.Entries.Remove("ESAA/Plugins/TopSky.dll"); f.SaveZip();
            var before = f.Bytes("ESAA/Plugins/TopSky.dll");
            f.Install(f.Plan()); Assert(before.SequenceEqual(f.Bytes("ESAA/Plugins/TopSky.dll")), "Unchanged installed dependency was overwritten");
            File.Delete(f.PathFor("ESAA/Plugins/TopSky.dll")); MustReject(() => f.Plan());
        });
        Test("Full Package refuses a referenced DLL omitted from its archive", f =>
        {
            f.ZipPath = Path.Combine(f.DirectoryPath, "ESAA-Full-Package_" + Fixture.NewIdentity + ".zip");
            f.Entries.Remove("ESAA/Plugins/TopSky.dll"); f.SaveZip(); MustReject(() => f.Plan());
        });
        Test("Update-Only dependency changes invalidate the preview", f =>
        {
            f.Entries.Remove("ESAA/Plugins/TopSky.dll"); f.SaveZip(); var plan = f.Plan();
            f.Write("ESAA/Plugins/TopSky.dll", "synthetic changed dependency"); var before = f.Snapshot();
            MustReject(() => f.Install(plan)); AssertSnapshot(before, f.Snapshot());
        });
        Test("Update-Only dependencies cannot be edited or renamed during installation", f =>
        {
            f.Entries.Remove("ESAA/Plugins/TopSky.dll"); f.SaveZip(); var plan = f.Plan(); var attempts = 0;
            f.Install(plan, new SynchronousProgress(message =>
            {
                if (!message.StartsWith("Installing ") || attempts++ != 0) return;
                MustReject(() => File.WriteAllText(f.PathFor("ESAA/Plugins/TopSky.dll"), "synthetic concurrent edit"));
                MustReject(() => File.Move(f.PathFor("ESAA/Plugins/TopSky.dll"), f.PathFor("ESAA/Plugins/Moved.dll")));
            }));
            Assert(attempts > 0 && f.Read("ESAA/Plugins/TopSky.dll") == "synthetic old TopSky DLL", "Required plugin identity was not pinned");
            f.Write("ESAA/Plugins/TopSky.dll", "synthetic post-transaction write");
        });
        Test("junction roots and configured junction protections are rejected", f =>
        {
            var alias = Path.Combine(f.DirectoryPath, "EuroScope-alias"); SyntheticJunction.Create(alias, f.Root);
            MustReject(() => GngUpdateService.BuildPlan(f.ZipPath, alias, null, f.Context));
            MustReject(() => f.Plan(new[] { alias }));
        });
        Test("cache junction substitution is rejected before active files change", f =>
        {
            var plan = f.Plan(); var before = f.Snapshot(); var outside = Path.Combine(f.DirectoryPath, "Outside-cache-synthetic"); Directory.CreateDirectory(outside);
            SyntheticJunction.Create(f.StorageRoot, outside); MustReject(() => f.Install(plan));
            AssertSnapshot(before, f.Snapshot()); Assert(!Directory.EnumerateFileSystemEntries(outside).Any(), "Updater wrote through cache junction");
        });
        Test("destination junction substitution prevents restore outside the reviewed root", f =>
        {
            var result = f.Install(f.Plan()); var plugins = f.PathFor("ESAA/Plugins"); var saved = f.PathFor("ESAA/Plugins-saved");
            Directory.Move(plugins, saved);
            var outside = Path.Combine(f.DirectoryPath, "Outside-plugins-synthetic"); Directory.CreateDirectory(outside); SyntheticJunction.Create(plugins, outside);
            try { var restored = f.Restore(result.BackupFolder); Assert(restored.SkippedFiles.Count > 0, "Restore accepted a destination junction"); }
            catch (Exception ex) when (IsRejection(ex)) { }
            Assert(!Directory.EnumerateFileSystemEntries(outside).Any(), "Recovery wrote through destination junction");
        });
        Test("malformed previous-installation metadata is rejected before recovery changes", f =>
        {
            f.Install(f.Plan()); var result = f.Install(f.Plan()); var before = f.Snapshot();
            File.WriteAllText(Path.Combine(result.BackupFolder, "previous-installation.json"), "{\"Files\":[null]}");
            MustReject(() => f.Restore(result.BackupFolder)); AssertSnapshot(before, f.Snapshot());
        });
        Test("packaged sector reference cannot select an external same-named file", f =>
        {
            f.Entries[Fixture.ProfilePath] = Fixture.PackageProfile.Replace("Settings\tsector\t" + Fixture.NewStem, "Settings\tsector\tC:\\External-synthetic\\" + Fixture.NewStem);
            f.SaveZip(); MustReject(() => f.Plan());
        });
        Test("EuroScope package-relative sector and plugin notation is normalized", f =>
        {
            f.Entries[Fixture.ProfilePath] = Fixture.PackageProfile.Replace("Settings\tsector\t", "Settings\tsector\t\\").Replace("\tESAA\\Plugins\\", "\t.\\ESAA\\Plugins\\");
            f.SaveZip(); f.Install(f.Plan());
            Assert(f.Read(Fixture.ProfilePath).Contains("Settings\tsector\t" + Fixture.NewStem + ".sct"), "Package sector was not normalized to the installed root");
            Assert(f.Read(Fixture.ProfilePath).Contains("\tESAA\\Plugins\\TopSky.dll"), "Package plugin was not normalized");
        });
        Test("pending-update use guard leaves blank and unconfigured legacy folders alone", f =>
        {
            foreach (var root in new[] { null, "", "  ", f.Root, Path.Combine(f.DirectoryPath, "Missing-manual-data-folder") })
                GngUpdateService.RequireNoPendingUpdate(root, f.Context);
            Assert(!Directory.Exists(f.StorageRoot), "Read-only guard created recovery storage");
        });
        Test("pending-update use guard recognizes canonical installation through a junction alias", f =>
        {
            var alias = Path.Combine(f.DirectoryPath, "Legacy-data-alias"); SyntheticJunction.Create(alias, f.Root);
            GngUpdateService.RequireNoPendingUpdate(alias, f.Context);
            var result = f.Install(f.Plan());
            f.Write("ESAA/Plugins/TopSky.dll", "synthetic postinstall edit preventing full recovery");
            Assert(f.Restore(result.BackupFolder).SkippedFiles.Count > 0, "Fixture did not create pending recovery");
            var before = f.Snapshot();
            MustReject(() => GngUpdateService.RequireNoPendingUpdate(f.Root, f.Context));
            MustReject(() => GngUpdateService.RequireNoPendingUpdate(alias, f.Context));
            AssertSnapshot(before, f.Snapshot());
        });
        foreach (var shape in new[] { "invalid-json", "outside-path", "directory", "junction" })
            Test("pending-update use guard fails closed for unsafe marker: " + shape, f =>
            {
                var result = f.Install(f.Plan()); var marker = Path.Combine(Path.GetDirectoryName(result.BackupFolder)!, "pending-update.json");
                var before = f.Snapshot();
                if (shape == "invalid-json") File.WriteAllText(marker, "{malformed");
                if (shape == "outside-path") File.WriteAllText(marker, JsonSerializer.Serialize(new { BackupFolder = f.DirectoryPath }));
                if (shape == "directory") Directory.CreateDirectory(marker);
                if (shape == "junction") SyntheticJunction.Create(marker, result.BackupFolder);
                MustReject(() => GngUpdateService.RequireNoPendingUpdate(f.Root, f.Context));
                AssertSnapshot(before, f.Snapshot());
            });
        Test("paired Full reference accepts matching AIRAC revision with different sector timestamp without installing it", f =>
        {
            const string referenceIdentity = "20261001110000-261001-0003";
            var reference = MakeReferencePackage(f, referenceIdentity, entries => entries["ESAA/Settings/FullOnlyDefault.txt"] = "synthetic full-only default");
            var originalZip = File.ReadAllBytes(reference);
            var plan = f.Plan(fullPackagePath: reference);
            Assert(plan.ReferencePackageName == Path.GetFileName(reference), "Review does not identify paired reference");
            Assert(plan.Warnings.Any(w => w.Contains("only the Update-Only ZIP is installed")), "Review obscures reference-only role");
            var result = f.Install(plan);
            Assert(result.CompletePackagePath is not null && originalZip.SequenceEqual(File.ReadAllBytes(result.CompletePackagePath)), "Complete reference was not cached byte-for-byte");
            Assert(result.CachedPackagePath != result.CompletePackagePath, "Installed and reference package identities were conflated");
            Assert(!File.Exists(f.PathFor("ESAA/Settings/FullOnlyDefault.txt")) && !File.Exists(f.PathFor("ESAA-Sweden_" + referenceIdentity + ".sct")), "Reference package was extracted into active data");
            Assert(f.Read(Fixture.ProfilePath).Contains(Fixture.NewStem), "Reference profile replaced the installed Update-Only profile");
        });
        foreach (var identity in new[] { "20261001110000-261002-0003", "20261001110000-261001-0004", "20261029110000-261101-0003" })
            Test("paired reference must match package number AIRAC and revision: " + identity, f =>
            {
                var reference = MakeReferencePackage(f, identity); var before = f.Snapshot();
                MustReject(() => f.Plan(fullPackagePath: reference)); AssertSnapshot(before, f.Snapshot());
            });
        Test("Update-Only archive cannot masquerade as a Full reference", f =>
        {
            MustReject(() => f.Plan(fullPackagePath: f.ZipPath));
        });
        Test("paired reference changes after preview invalidate installation", f =>
        {
            var reference = MakeReferencePackage(f); var plan = f.Plan(fullPackagePath: reference); var before = f.Snapshot();
            MakeReferencePackage(f, customize: entries => entries["ESAA/Settings/Changed.txt"] = "synthetic later reference edit");
            MustReject(() => f.Install(plan)); AssertSnapshot(before, f.Snapshot());
        });
        Test("previous complete reference survives paired updates and ordered restore", f =>
        {
            var original = f.Snapshot(); var reference = MakeReferencePackage(f);
            var first = f.Install(f.Plan(fullPackagePath: reference)); var firstState = f.Snapshot();
            f.Entries["ESAA/Plugins/TopSky.dll"] = "synthetic repaired primary plugin"; f.SaveZip();
            var second = f.Install(f.Plan(fullPackagePath: reference));
            Assert(second.PreviousCompletePackagePath == first.CompletePackagePath, "Prior paired reference was not retained as complete provenance");
            var restored = f.Restore(second.BackupFolder);
            Assert(restored.CompletePackagePath == first.CompletePackagePath && restored.CachedPackagePath == first.CachedPackagePath, "Restoring the receipt did not restore both package identities");
            AssertSnapshot(firstState, f.Snapshot());
            restored = f.Restore(first.BackupFolder);
            Assert(restored.CompletePackagePath is null && restored.CachedPackagePath is null, "Restoring unmanaged state left a false full reference");
            AssertSnapshot(original, f.Snapshot());
        });
        Test("tampered paired reference is never offered as previous cleanup provenance", f =>
        {
            var reference = MakeReferencePackage(f); var first = f.Install(f.Plan(fullPackagePath: reference));
            File.WriteAllText(first.CompletePackagePath!, "synthetic modified full cache");
            var second = f.Install(f.Plan(fullPackagePath: reference));
            Assert(second.PreviousCompletePackagePath is null, "Modified Full reference was trusted");
            Assert(second.CompletePackagePath is not null, "Valid new reference was lost because previous cache changed");
        });
        Test("managed plugin provenance uses the applied primary package rather than the cached Full reference", f =>
        {
            var reference = MakeReferencePackage(f, customize: entries =>
            {
                entries["ESAA/Plugins/ReferenceOnly.dll"] = "synthetic full-reference plugin";
                entries[Fixture.ProfilePath] += "Plugins\tPlugin9\tESAA\\Plugins\\ReferenceOnly.dll\r\n";
            });
            f.Install(f.Plan(fullPackagePath: reference));
            f.Write("ESAA/Plugins/ReferenceOnly.dll", "synthetic independently added custom plugin");
            f.Write(Fixture.ProfilePath, f.Read(Fixture.ProfilePath) + "Plugins\tPlugin9\tESAA\\Plugins\\ReferenceOnly.dll\r\n");
            f.Install(f.Plan(fullPackagePath: reference));
            Assert(f.Read(Fixture.ProfilePath).Contains("ReferenceOnly.dll"), "Unapplied Full reference incorrectly retired a custom plugin");
        });
        Test("legacy Full-primary receipt still supplies complete provenance without new optional fields", f =>
        {
            f.ZipPath = Path.Combine(f.DirectoryPath, "ESAA-Full-Package_" + Fixture.NewIdentity + ".zip"); f.SaveZip();
            var first = f.Install(f.Plan()); var path = Path.Combine(Path.GetDirectoryName(first.BackupFolder)!, "current-installation.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); json.Remove("CompletePackagePath"); json.Remove("CompletePackageHash"); File.WriteAllText(path, json.ToJsonString());
            var second = f.Install(f.Plan()); Assert(second.PreviousCompletePackagePath == first.CachedPackagePath, "Legacy Full receipt lost verified original provenance");
        });
        Test("receipt rejects a complete reference redirected outside its transaction", f =>
        {
            var reference = MakeReferencePackage(f); var first = f.Install(f.Plan(fullPackagePath: reference)); var before = f.Snapshot();
            var path = Path.Combine(Path.GetDirectoryName(first.BackupFolder)!, "current-installation.json"); var json = JsonNode.Parse(File.ReadAllText(path))!;
            json["CompletePackagePath"] = reference; File.WriteAllText(path, json.ToJsonString());
            MustReject(() => f.Plan(fullPackagePath: reference)); AssertSnapshot(before, f.Snapshot());
        });
        Console.WriteLine($"\n{passed} passed, {failures.Count} failed");
        Console.WriteLine("SYNTHETIC FIXTURES " + fixturesDirectory);
        Environment.ExitCode = failures.Count == 0 ? 0 : 1;
    }

    private static string MakeReferencePackage(Fixture fixture, string identity = "20261001110000-261001-0003", Action<Dictionary<string, string>>? customize = null)
    {
        var path = Path.Combine(fixture.DirectoryPath, "ESAA-Full-Package_" + identity + ".zip");
        var entries = fixture.Entries.ToDictionary(p => p.Key.Replace(Fixture.NewIdentity, identity, StringComparison.Ordinal), p => p.Value.Replace(Fixture.NewIdentity, identity, StringComparison.Ordinal));
        customize?.Invoke(entries);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write); using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry.Key).Open(), Fixture.ProfileEncoding); writer.Write(entry.Value);
        }
        return path;
    }

    private void Test(string name, Action<Fixture> test)
    {
        if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) return;
        var fixture = new Fixture(Path.Combine(fixturesDirectory, (passed + failures.Count + 1).ToString("00")));
        try { test(fixture); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failures.Add("FAIL " + name + ": " + ex.GetType().Name + ": " + ex.Message); Console.WriteLine(failures[^1]); }
    }
    private static void AssertSnapshot(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual)
    {
        Assert(expected.Keys.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(actual.Keys.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase), "Data-folder file set changed unexpectedly");
        foreach (var path in expected.Keys) Assert(expected[path].SequenceEqual(actual[path]), "File bytes changed unexpectedly: " + path);
    }
    private static bool IsWithin(string root, string path) => Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool Equal(string a, string b) => string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static bool IsRejection(Exception ex) => ex is InvalidOperationException or IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException;
    private static void MustReject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (IsRejection(ex)) { return; }
        throw new Exception("Expected operation rejection");
    }
}

sealed class SynchronousProgress(Action<string> onReport) : IProgress<string>
{
    public void Report(string value) => onReport(value);
}
