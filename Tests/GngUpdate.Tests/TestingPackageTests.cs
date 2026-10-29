using System.Text.Json.Nodes;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

internal static class TestingPackageTests
{
    internal static void Run(Action<string, Action<Fixture>> test)
    {
        test("Testing packages require explicit manual import and plan read-only", f =>
        {
            SelectTesting(f);
            var before = f.Snapshot();
            Reject(() => GngUpdateService.ValidatePackage(f.ZipPath));
            Reject(() => f.Plan());
            Require(GngUpdateService.ValidatePackage(f.ZipPath, allowTesting: true) == "2610/01 rev.3", "Manual testing validation failed.");
            var plan = Plan(f);
            Require(plan.IsTestingPackage && plan.Warnings.Any(w => w.Contains("navteam testing package")), "Testing origin or warning was lost.");
            SameFiles(before, f.Snapshot());
            Require(!Directory.Exists(f.StorageRoot), "Preview created recovery storage.");
        });
        test("Testing package requires all referenced plugins even when already installed", f =>
        {
            SelectTesting(f);
            f.Entries.Remove("ESAA/Plugins/TopSky.dll"); f.SaveZip();
            Reject(() => GngUpdateService.ValidatePackage(f.ZipPath, allowTesting: true));
            Reject(() => Plan(f));
        });
        test("Testing package imports into an empty folder and restores without stable references", f =>
        {
            SelectTesting(f);
            string empty = Path.Combine(f.DirectoryPath, "empty-synthetic"); Directory.CreateDirectory(empty);
            var plan = GngUpdateService.BuildPlan(f.ZipPath, empty, null, f.Context, allowTesting: true);
            var result = f.Install(plan);
            Require(result.CompletePackagePath is null && result.PreviousCompletePackagePath is null, "Testing became a Full reference.");
            Require(result.CachedPackagePath is not null && Path.GetFileName(result.CachedPackagePath) == plan.PackageName, "Original Testing archive was not retained.");
            Require(GngUpdateService.IsTestingInstallation(empty, f.Context), "Testing receipt was not recognized.");
            var restored = GngUpdateService.Restore(result.BackupFolder, empty, null, null, f.Context);
            Require(restored.SkippedFiles.Count == 0 && !Directory.EnumerateFiles(empty, "*", SearchOption.AllDirectories).Any(), "Fresh Testing install did not restore the empty root.");
            Require(!GngUpdateService.IsTestingInstallation(empty, f.Context), "Restored Testing receipt survived.");
        });
        test("Testing file preview keeps merge and privacy rules", f =>
        {
            SelectTesting(f);
            var preview = GngUpdateService.PreviewFile(Plan(f), Fixture.ProfilePath);
            Require(preview.HasText && preview.AfterText!.Contains(Fixture.NewStem), "Testing preview did not use the merged profile.");
            Require(!preview.AfterText!.Contains(Fixture.FakePassword) && !preview.BeforeText!.Contains(Fixture.FakePassword), "Testing preview exposed credentials.");
        });
        test("Testing revalidation rejects altered approval without changing installed files", f =>
        {
            SelectTesting(f); var plan = Plan(f); var before = f.Snapshot();
            Reject(() => f.Install(plan with { IsTestingPackage = false }));
            SameFiles(before, f.Snapshot());
        });
        test("Testing cannot be paired with stable cleanup references in either direction", f =>
        {
            string stable = Path.Combine(f.DirectoryPath, "ESAA-Full-Package_" + Fixture.NewIdentity + ".zip");
            File.Copy(f.ZipPath, stable); SelectTesting(f);
            Reject(() => GngUpdateService.BuildPlan(f.ZipPath, f.Root, null, f.Context, stable, allowTesting: true));
            Reject(() => GngUpdateService.BuildPlan(stable, f.Root, null, f.Context, f.ZipPath, allowTesting: true));
        });
        test("Testing installation marker requires unchanged current sector pair", f =>
        {
            SelectTesting(f); f.Install(Plan(f));
            Require(GngUpdateService.IsTestingInstallation(f.Root, f.Context), "Testing install was not detected.");
            foreach (string extension in new[] { ".sct", ".ese" })
            {
                string relative = Fixture.NewStem + extension; byte[] original = f.Bytes(relative);
                f.Write(relative, "synthetic manual replacement");
                Require(!GngUpdateService.IsTestingInstallation(f.Root, f.Context), "Changed sector retained the Testing label.");
                File.WriteAllBytes(f.PathFor(relative), original);
            }
            string newer = "ESAA-Sweden_20261101120000-261101-0001.sct";
            f.Write(newer, "synthetic newer manual sector");
            Require(!GngUpdateService.IsTestingInstallation(f.Root, f.Context), "Newer manual sector inherited Testing label.");
            File.Delete(f.PathFor(newer));
            Require(GngUpdateService.IsTestingInstallation(f.Root, f.Context), "Unchanged original pair no longer recognized.");
        });
        test("Testing to stable transition and reverse restore retain provenance without cleanup baseline", f =>
        {
            SelectTesting(f); var before = f.Snapshot(); var testing = f.Install(Plan(f));
            Require(GngUpdateService.GetCachedPackage(f.Root, f.Context) == testing.CachedPackagePath, "Verified Testing provenance was unavailable.");
            f.ZipPath = Path.Combine(f.DirectoryPath, "ESAA-Full-Package_" + Fixture.NewIdentity + ".zip"); f.SaveZip();
            var stable = f.Install(f.Plan());
            Require(stable.PreviousCompletePackagePath is null && stable.CompletePackagePath is not null, "Testing was offered as previous Full package.");
            Require(!GngUpdateService.IsTestingInstallation(f.Root, f.Context), "Stable installation retained Testing label.");
            Require(f.Restore(stable.BackupFolder).SkippedFiles.Count == 0 && GngUpdateService.IsTestingInstallation(f.Root, f.Context), "Restoring stable did not restore Testing provenance.");
            Require(f.Restore(testing.BackupFolder).SkippedFiles.Count == 0 && !GngUpdateService.IsTestingInstallation(f.Root, f.Context), "Restoring Testing did not clear its provenance.");
            SameFiles(before, f.Snapshot());
        });
        test("Testing receipt rejects an attached stable cleanup reference", f =>
        {
            SelectTesting(f); var installed = f.Install(Plan(f)); var before = f.Snapshot();
            string path = Path.Combine(Path.GetDirectoryName(installed.BackupFolder)!, "current-installation.json");
            var receipt = JsonNode.Parse(File.ReadAllText(path))!;
            receipt["CompletePackagePath"] = Path.Combine(installed.BackupFolder, "complete-package", "ESAA-Full-Package_" + Fixture.NewIdentity + ".zip");
            receipt["CompletePackageHash"] = receipt["PackageHash"]!.GetValue<string>();
            File.WriteAllText(path, receipt.ToJsonString());
            Reject(() => Plan(f));
            Reject(() => GngUpdateService.IsTestingInstallation(f.Root, f.Context));
            SameFiles(before, f.Snapshot());
        });
        test("Testing archive stays out of automatic downloads and matching stable cache", f =>
        {
            SelectTesting(f);
            string folder = Path.Combine(f.StorageRoot, "Downloads", new string('1', 32)); Directory.CreateDirectory(folder);
            File.Copy(f.ZipPath, Path.Combine(folder, Path.GetFileName(f.ZipPath)));
            Require(!GngBrowserPolicy.TryGetDownloadFilename("https://files.aero-nav.com/ESAA/download", f.ZipPath, out _), "Automatic download accepted Testing.");
            Require(!GngBrowserPolicy.IsCompletePackage(f.ZipPath), "Testing is advertised as Full.");
            const string html = "<table><tr><th>Client</th><th>Packagename</th><th>AIRAC</th><th>Version</th><th>Released</th><th colspan='2'>Download</th></tr><tr><td>ES</td><td>ESAA Full_Package</td><td>2610 / 01</td><td>3</td><td>2026-10-01 12:00:00</td><td>zip</td><td>7z</td></tr></table>";
            var cached = GngPackageCache.TryFindAsync(f.StorageRoot, GngPackageKind.Full, _ => Task.FromResult(html)).GetAwaiter().GetResult();
            Require(cached is null, "Testing was reused for a stable download.");
        });
        test("Testing preserves generation validation and rejects unsafe archive paths", f =>
        {
            SelectTesting(f);
            string original = f.ZipPath;
            f.ZipPath = Path.Combine(f.DirectoryPath, "ESAA-Testing_20261002120000-261001-0003.zip"); f.SaveZip();
            Reject(() => GngUpdateService.ValidatePackage(f.ZipPath, allowTesting: true));
            f.ZipPath = original; f.SaveZip(); f.AddRawEntry("../outside.txt", "synthetic traversal");
            Reject(() => GngUpdateService.ValidatePackage(f.ZipPath, allowTesting: true));
            f.SaveZip(); f.AddRawEntry("ESAA/Plugins/TOPSKY.dll", "synthetic duplicate");
            Reject(() => GngUpdateService.ValidatePackage(f.ZipPath, allowTesting: true));
        });
        test("Testing installation does not permit an older stable generation", f =>
        {
            SelectTesting(f); f.Install(Plan(f));
            var entries = f.Entries.ToArray(); f.Entries.Clear();
            foreach (var entry in entries) f.Entries[entry.Key.Replace(Fixture.NewIdentity, Fixture.OldIdentity)] = entry.Value.Replace(Fixture.NewIdentity, Fixture.OldIdentity);
            f.ZipPath = Path.Combine(f.DirectoryPath, "ESAA-Full-Package_" + Fixture.OldIdentity + ".zip"); f.SaveZip();
            var before = f.Snapshot(); Reject(() => f.Plan()); SameFiles(before, f.Snapshot());
        });
    }

    private static void SelectTesting(Fixture f)
    {
        f.ZipPath = Path.Combine(f.DirectoryPath, "ESAA-Testing_" + Fixture.NewIdentity + ".zip"); f.SaveZip();
    }
    private static GngUpdatePlan Plan(Fixture f) => GngUpdateService.BuildPlan(f.ZipPath, f.Root, null, f.Context, allowTesting: true);
    private static void SameFiles(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after) =>
        Require(before.Count == after.Count && before.All(file => after.TryGetValue(file.Key, out var bytes) && file.Value.SequenceEqual(bytes)), "Installed files changed unexpectedly.");
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidOperationException or IOException or ArgumentException) { return; }
        throw new Exception("Unsafe Testing operation was accepted.");
    }
}
