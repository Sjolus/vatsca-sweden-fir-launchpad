using System.IO.Compression;
using VatscaUpdateChecker.Services;

internal static class PackageCacheTests
{
    private const string FullIdentity = "20261001222500-261001-0003";
    private const string UpdateIdentity = "20261001222554-261001-0003";
    private const string Download = "Downloads/11111111111111111111111111111111";
    private static readonly string Transaction = "Installations/" + new string('A', 64) + "/20261007-123456-22222222222222222222222222222222";

    internal static void Run(Action<string, Action<Fixture>> test)
    {
        test("package cache reuses the matching Update Only and Full originals", f =>
        {
            string update = Package(f, GngPackageKind.UpdateOnly, Download, UpdateIdentity);
            string full = Package(f, GngPackageKind.Full, Download, FullIdentity);
            var before = Directory.GetFiles(f.StorageRoot, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
            var result = Find(f, GngPackageKind.UpdateOnly);
            Require(result?.PackagePath == update && result.FullPackagePath == full && result.Version == "2610/01 rev.3", "Matching pair was not reused.");
            Require(before.Count == Directory.GetFiles(f.StorageRoot, "*", SearchOption.AllDirectories).Length
                && before.All(p => File.ReadAllBytes(p.Key).SequenceEqual(p.Value)), "Cache check changed cached files.");
        });
        test("package cache Full requests do not need an Update Only reference", f =>
        {
            string full = Package(f, GngPackageKind.Full, Download, FullIdentity);
            var result = Find(f, GngPackageKind.Full);
            Require(result?.PackagePath == full && result.FullPackagePath is null, "Full package was not reused by itself.");
            var partial = Find(f, GngPackageKind.UpdateOnly);
            Require(partial is { PackagePath: null } && partial.FullPackagePath == full, "Full reference was not returned separately from the missing Update Only package.");
        });
        test("package cache returns Update Only when the matching reference still needs downloading", f =>
        {
            string primary = Package(f, GngPackageKind.UpdateOnly, Download, UpdateIdentity);
            var partial = Find(f, GngPackageKind.UpdateOnly);
            Require(partial?.PackagePath == primary && partial.FullPackagePath is null, "Reusable primary package was not returned.");
            Package(f, GngPackageKind.Full, Download, "20261001222500-261001-0002");
            partial = Find(f, GngPackageKind.UpdateOnly);
            Require(partial?.PackagePath == primary && partial.FullPackagePath is null, "A different Full revision was accepted.");
        });
        test("package cache can retain a matching reference while a corrupt primary is downloaded again", f =>
        {
            string primary = Package(f, GngPackageKind.UpdateOnly, Download, UpdateIdentity);
            string reference = Package(f, GngPackageKind.Full, Download, FullIdentity);
            File.WriteAllText(primary, "synthetic interrupted primary ZIP");
            var partial = Find(f, GngPackageKind.UpdateOnly);
            Require(partial is { PackagePath: null } && partial.FullPackagePath == reference, "Valid reference was lost when the primary ZIP was invalid.");
        });
        test("package cache compares AIRAC package number and revision", f =>
        {
            Package(f, GngPackageKind.Full, Download, FullIdentity);
            foreach (string changed in new[] { "2611 / 01|3", "2610 / 02|3", "2610 / 01|4" })
            {
                string[] parts = changed.Split('|');
                Require(Find(f, GngPackageKind.Full, Catalog(parts[0], parts[1])) is null, "Changed public version reused an older cache.");
            }
        });
        test("package cache uses the newest valid cached build of the public version", f =>
        {
            Package(f, GngPackageKind.Full, Download, FullIdentity);
            string newest = Package(f, GngPackageKind.Full, Download, "20261002222500-261001-0003");
            Require(Find(f, GngPackageKind.Full)?.PackagePath == newest, "Newest cached build was not preferred.");
        });
        test("package cache skips corrupt archives and incomplete download suffixes", f =>
        {
            string valid = Package(f, GngPackageKind.Full, Download, FullIdentity);
            string corrupt = Package(f, GngPackageKind.Full, Download, "20261002222500-261001-0003");
            File.WriteAllText(corrupt, "synthetic interrupted ZIP");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(valid)!, "ESAA-Full-Package_20261003222500-261001-0003.zip.crdownload"), "partial");
            Require(Find(f, GngPackageKind.Full)?.PackagePath == valid, "A valid cached original was lost behind incomplete candidates.");
        });
        test("package cache prefers the newest copy over older interrupted duplicate downloads", f =>
        {
            for (int index = 1; index <= 4; index++)
            {
                string invalid = Package(f, GngPackageKind.Full, "Downloads/" + index.ToString("D32"), FullIdentity);
                File.WriteAllText(invalid, "synthetic earlier incomplete ZIP");
                File.SetLastWriteTimeUtc(invalid, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
            }
            string valid = Package(f, GngPackageKind.Full, "Downloads/" + 5.ToString("D32"), FullIdentity);
            File.SetLastWriteTimeUtc(valid, new DateTime(2026, 10, 7, 0, 0, 0, DateTimeKind.Utc));
            Require(Find(f, GngPackageKind.Full)?.PackagePath == valid, "Interrupted older copies exhausted validation before the latest download.");
        });
        test("package cache archive contents must match the advertised filename", f =>
        {
            string invalid = Package(f, GngPackageKind.Full, Download, FullIdentity);
            using (var file = new FileStream(invalid, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("not-a-sector.txt").Open())) writer.Write("synthetic invalid package");
            Require(Find(f, GngPackageKind.Full) is null, "Filename-only package validation was used.");
        });
        test("package cache can reuse originals retained beside installation recovery", f =>
        {
            string update = Package(f, GngPackageKind.UpdateOnly, Transaction + "/package", UpdateIdentity);
            string full = Package(f, GngPackageKind.Full, Transaction + "/complete-package", FullIdentity);
            File.WriteAllText(Path.Combine(f.StorageRoot, Transaction, "update-manifest.json"), "not read by package cache");
            var result = Find(f, GngPackageKind.UpdateOnly);
            Require(result?.PackagePath == update && result.FullPackagePath == full, "Retained original package pair was ignored.");
        });
        test("package cache never searches backup files arbitrary folders or nested download content", f =>
        {
            foreach (string folder in new[] { Transaction + "/files", "Other", "Downloads/not-a-download", Download + "/nested" })
                Package(f, GngPackageKind.Full, folder, FullIdentity);
            Require(Find(f, GngPackageKind.Full) is null, "Cache scan reached outside original archive folders.");
        });
        test("package cache rejects redirected download and recovery folders", f =>
        {
            string outside = Path.Combine(f.DirectoryPath, "Synthetic-outside-cache");
            string originalStorage = f.StorageRoot;
            Directory.CreateDirectory(outside);
            string archive = Package(f, GngPackageKind.Full, "Outside-originals", FullIdentity);
            File.Copy(archive, Path.Combine(outside, Path.GetFileName(archive)));
            string parent = Path.Combine(originalStorage, "Downloads"); Directory.CreateDirectory(parent);
            SyntheticJunction.Create(Path.Combine(parent, "11111111111111111111111111111111"), outside);
            Require(Find(f, GngPackageKind.Full) is null, "A redirected cache folder was traversed.");
        });
        test("package cache requires unambiguous public package rows", f =>
        {
            Package(f, GngPackageKind.Full, Download, FullIdentity);
            foreach (string html in new[] { "<html>Sign in</html>", Catalog() + Catalog(), Catalog().Replace("ESAA Full_Package", "Different package"),
                Catalog().Replace("2610 / 01", "2615 / 01"), Catalog().Replace("<td>3</td>", "<td>not a revision</td>"),
                Catalog().Replace("2026-10-01 22:25:00", "2026-02-31 22:25:00") })
                Require(Find(f, GngPackageKind.Full, html) is null, "Malformed or ambiguous public metadata was accepted.");
        });
        test("package cache handles public table whitespace entities and omitted closing cells", f =>
        {
            Package(f, GngPackageKind.Full, Download, FullIdentity);
            string html = Catalog().Replace("ESAA Full_Package", " ESAA&nbsp;Full_Package ").Replace("2610 / 01", "2610\n /\t01")
                .Replace("22:25:00</td>", "22:25:00");
            Require(Find(f, GngPackageKind.Full, html) is not null, "Public AeroNav table formatting was not handled.");
        });
        test("package cache missing public Full reference never supplies an Update Only pair", f =>
        {
            Package(f, GngPackageKind.UpdateOnly, Download, UpdateIdentity);
            Package(f, GngPackageKind.Full, Download, FullIdentity);
            Require(Find(f, GngPackageKind.UpdateOnly, Catalog().Replace("ESAA Full_Package", "Other package")) is null, "Unpublished Full reference was reused.");
        });
        test("package cache metadata failures fall back without creating storage", f =>
        {
            var result = GngPackageCache.TryFindAsync(f.StorageRoot, GngPackageKind.Full,
                _ => throw new HttpRequestException("synthetic public-page failure")).GetAwaiter().GetResult();
            Require(result is null && !Directory.Exists(f.StorageRoot), "Failed metadata lookup created cache content.");
            Require(Find(f, GngPackageKind.Full, new string(' ', 2 * 1024 * 1024 + 1)) is null, "Oversized public page was accepted.");
        });
        test("package cache cancellation does not open or create cache content", f =>
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try
            {
                GngPackageCache.TryFindAsync(f.StorageRoot, GngPackageKind.Full,
                    _ => throw new Exception("Public request should not begin."), cancellation.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                Require(!Directory.Exists(f.StorageRoot), "Cancellation created cache storage."); return;
            }
            throw new Exception("Cancellation was swallowed.");
        });
    }

    private static GngCachedPackages? Find(Fixture fixture, GngPackageKind kind, string? html = null) =>
        GngPackageCache.TryFindAsync(fixture.StorageRoot, kind, _ => Task.FromResult(html ?? Catalog())).GetAwaiter().GetResult();

    private static string Package(Fixture fixture, GngPackageKind kind, string folder, string identity)
    {
        string target = Path.Combine(fixture.StorageRoot, folder.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(target);
        string path = Path.Combine(target, "ESAA-" + (kind == GngPackageKind.Full ? "Full-Package" : "Update-Only") + "_" + identity + ".zip");
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var entry in fixture.Entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry.Key.Replace(Fixture.NewIdentity, identity, StringComparison.Ordinal)).Open(), Fixture.ProfileEncoding);
            writer.Write(entry.Value.Replace(Fixture.NewIdentity, identity, StringComparison.Ordinal));
        }
        return path;
    }
    private static string Catalog(string airac = "2610 / 01", string revision = "3") =>
        "<table><tr><th>Client</th><th>Packagename</th><th>AIRAC</th><th>Version</th><th>Released</th><th colspan='2'>Download</th></tr>" +
        $"<tr><td>ES</td><td>ESAA Full_Package</td><td>{airac}</td><td>{revision}</td><td>2026-10-01 22:25:00</td><td>zip</td><td>7z</td></tr>" +
        $"<tr><td>ES</td><td>ESAA Update_Only</td><td>{airac}</td><td>{revision}</td><td>2026-10-01 22:25:54</td><td>zip</td><td>7z</td></tr></table>";

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
