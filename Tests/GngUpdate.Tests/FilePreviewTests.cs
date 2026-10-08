using System.IO.Compression;
using System.Text;
using System.Text.Json;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

internal static class FilePreviewTests
{
    public static void Run(Action<string, Action<Fixture>> test)
    {
        test("file preview shows exact merged profile and masks all personal fields", f =>
        {
            f.Write(Fixture.ProfilePath, Fixture.LocalProfile + "TeamSpeakVccs\tUsername\tsynthetic-phone-user\r\nTeamSpeakVccs\tPassword\tsynthetic-phone-password\r\n");
            var before = f.Snapshot(); var plan = f.Plan();
            var preview = GngUpdateService.PreviewFile(plan, Fixture.ProfilePath);
            Require(preview.HasText, "Merged profile text missing");
            Require(preview.AfterText!.Contains("VatEFS.dll") && preview.AfterText.Contains(Fixture.NewStem), "Preview used raw package instead of exact merged result");
            Require(preview.BeforeText!.Contains(Fixture.OldStem) && preview.Summary.Contains("merged profile"), "Merge source was not explained");
            var serialized = preview.Summary + "\n" + preview.BeforeText + "\n" + preview.AfterText;
            foreach (var secret in new[] { Fixture.FakePassword, "Åke Öström", "0000000", "TEST_OBS", "AUTOMATIC", "synthetic-phone-user", "synthetic-phone-password", "package-placeholder" })
                Require(!serialized.Contains(secret, StringComparison.Ordinal), "Personal value reached preview model");
            Require(preview.BeforeText.Contains("LastSession\trating\t[hidden]"), "Non-secret LastSession value was not masked");
            foreach (var file in before) Require(file.Value.SequenceEqual(f.Snapshot()[file.Key]), "Read-only preview changed data");
            Require(!Directory.Exists(f.StorageRoot), "Read-only preview created application storage");
        });
        test("file preview rejects stale selected current bytes", f =>
        {
            var plan = f.Plan(); f.Write(Fixture.ProfilePath, Fixture.LocalProfile + "Settings\tchanged\tsynthetic\r\n");
            Reject(() => GngUpdateService.PreviewFile(plan, Fixture.ProfilePath));
        });
        test("file preview follows installer Windows1252 rules for BOM-less profiles", f =>
        {
            f.Write(Fixture.ProfilePath, Fixture.LocalProfile + "Settings\tcustomText\tÃ¥\r\n");
            var preview = GngUpdateService.PreviewFile(f.Plan(), Fixture.ProfilePath);
            Require(preview.HasText && preview.BeforeText!.Contains("customText\tÃ¥") && preview.AfterText!.Contains("customText\tÃ¥"), "Valid UTF8-looking bytes changed profile meaning");
            Require(preview.Summary.Contains("Windows-1252"), "Profile encoding was not reported");
        });
        test("file preview rejects stale package and unreviewed destinations", f =>
        {
            var plan = f.Plan(); f.Entries["new.txt"] = "synthetic new entry"; f.SaveZip();
            Reject(() => GngUpdateService.PreviewFile(plan, Fixture.ProfilePath));
            Reject(() => GngUpdateService.PreviewFile(plan, "../outside.txt"));
            Reject(() => GngUpdateService.PreviewFile(plan, "missing.txt"));
        });
        test("file preview rejects stale merged result bytes", f =>
        {
            var plan = f.Plan(); var file = plan.Files.Single(x => x.RelativePath == Fixture.ProfilePath);
            file.MergedBytes![0] ^= 1;
            Reject(() => GngUpdateService.PreviewFile(plan, Fixture.ProfilePath));
        });
        test("file preview rejects a destination created after add review", f =>
        {
            var plan = f.Plan(); f.Write(Fixture.NewStem + ".sct", "synthetic new competing file");
            Reject(() => GngUpdateService.PreviewFile(plan, Fixture.NewStem + ".sct"));
        });
        test("file preview validates promoted DLL mapping and uses metadata only", f =>
        {
            f.Entries["ESAA/Plugins/Updated Plugin/TopSky.dll"] = "synthetic promoted DLL"; f.SaveZip();
            var plan = f.Plan(); var file = plan.Files.Single(x => x.RelativePath == @"ESAA\Plugins\TopSky.dll");
            var preview = GngUpdateService.PreviewFile(plan, file.RelativePath);
            Require(preview.Action == "Promote plugin" && !preview.HasText && preview.Summary.Contains("promoted DLL"), "Promoted binary preview was incorrect");
            Require(preview.Summary.Contains(file.AfterHash) && file.AfterLength == Encoding.GetEncoding(1252).GetByteCount("synthetic promoted DLL"), "Wrong promoted bytes were reported");
        });
        test("file preview explains unchanged files without creating a false diff", f =>
        {
            const string path = "ESAA/Plugins/TopSkySettings.txt";
            f.Write(path, f.Entries[path]); var preview = GngUpdateService.PreviewFile(f.Plan(), path);
            Require(preview.HasText && preview.BeforeText == preview.AfterText && preview.Action == "Unchanged", "Unchanged file differs in preview");
            Require(preview.Summary.Contains("bytes are unchanged"), "Unchanged bytes not explained");
        });
        test("file preview hides personal Local Hoppie and LoginProfiles files", f =>
        {
            foreach (var path in new[] { "ESAA/Plugins/TopSkySettingsLocal.txt", "ESAA/Plugins/TopSkyCPDLChoppieCode.txt", "ESAA/Settings/LoginProfiles.txt", "ESAA/Local/overrides.txt" })
                f.Entries[path] = "synthetic-private-file-value";
            f.SaveZip(); var plan = f.Plan();
            foreach (var path in new[] { "ESAA/Plugins/TopSkySettingsLocal.txt", "ESAA/Plugins/TopSkyCPDLChoppieCode.txt", "ESAA/Settings/LoginProfiles.txt", "ESAA/Local/overrides.txt" })
            {
                var preview = GngUpdateService.PreviewFile(plan, path);
                Require(!preview.HasText && preview.BeforeText is null && preview.AfterText is null, "Personal file text was exposed");
                Require(!JsonSerializer.Serialize(preview).Contains("synthetic-private-file-value"), "Personal value reached summary");
            }
        });
        test("file preview masks credential lines on both changed and context sides", f =>
        {
            const string path = "ESAA/Settings/Server.cfg";
            f.Write(path, "mode=before\r\npassword=synthetic-old-value\r\napi_key=synthetic-context-value\r\nauthCode=synthetic-auth-code\r\n");
            f.Entries[path] = "mode=after\r\npassword=synthetic-new-value\r\napi_key=synthetic-context-value\r\nauthCode=synthetic-auth-code\r\n";
            f.SaveZip(); var preview = GngUpdateService.PreviewFile(f.Plan(), path);
            Require(preview.HasText && preview.BeforeText!.Contains("mode=before") && preview.AfterText!.Contains("mode=after"), "Useful nonsecret context missing");
            foreach (var secret in new[] { "synthetic-old-value", "synthetic-new-value", "synthetic-context-value", "synthetic-auth-code" })
                Require(!JsonSerializer.Serialize(preview).Contains(secret), "A credential reached preview text");
        });
        test("file preview hides sensitive unsupported and multiline formats", f =>
        {
            foreach (var (path, value) in new[] {
                ("ESAA/Settings/Server.json", "{\"password\":\"synthetic-secret\"}"),
                ("ESAA/Settings/Server.cfg", "password=\"synthetic-first\nsynthetic-continuation\"\n"),
                ("ESAA/Settings/Server.cfg", "<password>\nsynthetic-secret\n</password>\n"),
                ("ESAA/Settings/Server.cfg", "password: |\n  synthetic-secret\n"),
                ("ESAA/Settings/credentials.txt", "synthetic-unlabeled-secret"),
                ("ESAA/Settings/SecretSection.ini", "[Credentials]\nValue=synthetic-secret\n") })
            {
                f.Entries[path] = value; f.SaveZip(); var preview = GngUpdateService.PreviewFile(f.Plan(), path);
                Require(!preview.HasText && preview.BeforeText is null && preview.AfterText is null, "Unsupported sensitive content was exposed");
            }
        });
        test("file preview hides URL userinfo and query credentials", f =>
        {
            const string path = "ESAA/Settings/Network.cfg";
            f.Entries[path] = "url=https://synthetic-user:synthetic-pass@example.invalid/\nendpoint=https://example.invalid/callback?code=synthetic-code\n";
            f.SaveZip(); var preview = GngUpdateService.PreviewFile(f.Plan(), path);
            var serialized = JsonSerializer.Serialize(preview);
            foreach (var value in new[] { "synthetic-user", "synthetic-pass", "synthetic-code" }) Require(!serialized.Contains(value), "URL credential reached preview");
        });
        test("file preview limits large and binary text-looking files", f =>
        {
            f.Entries["ESAA/Settings/Large.txt"] = new string('a', 2 * 1024 * 1024 + 1);
            f.Entries["ESAA/Settings/Binary.txt"] = "text\0binary"; f.SaveZip(); var plan = f.Plan();
            Require(!GngUpdateService.PreviewFile(plan, "ESAA/Settings/Large.txt").HasText, "Oversized text reached model");
            Require(!GngUpdateService.PreviewFile(plan, "ESAA/Settings/Binary.txt").HasText, "Binary text reached model");
        });
        test("file preview preserves Windows1252 Swedish text and whitespace changes", f =>
        {
            const string path = "ESAA/Settings/Display.txt";
            f.Write(path, "Åke Öström\r\nSpacing  \r\n"); f.Entries[path] = "Åke Öström\nSpacing \n"; f.SaveZip();
            var preview = GngUpdateService.PreviewFile(f.Plan(), path);
            Require(preview.BeforeText == "Åke Öström\r\nSpacing  \r\n" && preview.AfterText == "Åke Öström\nSpacing \n", "Encoding/newlines/spaces changed during preview");
            Require(preview.Summary.Contains("Windows-1252") && preview.Summary.Contains("CRLF 2, LF 0") && preview.Summary.Contains("CRLF 0, LF 2"), "Newline/encoding metadata absent");
        });
        test("file preview bounds newline-heavy text before redaction allocations", f =>
        {
            const string path = "ESAA/Settings/Newlines.txt";
            f.Entries[path] = new string('\n', 2 * 1024 * 1024); f.SaveZip();
            var preview = GngUpdateService.PreviewFile(f.Plan(), path);
            Require(!preview.HasText && preview.BeforeText is null && preview.AfterText is null && preview.Summary.Contains("20,000 lines"), "Newline-heavy file reached text views");
        });
        test("file preview accepts 20000 lines and rejects 20001 on either side", f =>
        {
            const string path = "ESAA/Settings/LineBoundary.txt";
            var permitted = string.Concat(Enumerable.Repeat("x\r\n", 20_000));
            f.Entries[path] = permitted; f.SaveZip();
            Require(GngUpdateService.PreviewFile(f.Plan(), path).HasText, "Exact line-count limit was rejected");
            f.Entries[path] = permitted + "x\n"; f.SaveZip();
            Require(!GngUpdateService.PreviewFile(f.Plan(), path).HasText, "Excess after lines were accepted");
            f.Write(path, permitted + "x"); f.Entries[path] = "short\n"; f.SaveZip();
            Require(!GngUpdateService.PreviewFile(f.Plan(), path).HasText, "Excess before lines were accepted");
        });
        test("file preview decodes strict UTF8 and UTF16 BOMs and reports final newline", f =>
        {
            const string path = "ESAA/Settings/Unicode.txt";
            f.Entries[path] = "placeholder"; f.SaveZip();
            var before = new UTF8Encoding(true, true); var after = new UnicodeEncoding(false, true, true);
            var text = "Åke 東京\n";
            Directory.CreateDirectory(Path.GetDirectoryName(f.PathFor(path))!);
            File.WriteAllBytes(f.PathFor(path), before.GetPreamble().Concat(before.GetBytes(text)).ToArray());
            SetZipBytes(f.ZipPath, path, after.GetPreamble().Concat(after.GetBytes(text.TrimEnd('\n'))).ToArray());
            var preview = GngUpdateService.PreviewFile(f.Plan(), path);
            Require(preview.BeforeText == text && preview.AfterText == text.TrimEnd('\n'), "Unicode preview differs from source");
            Require(preview.Summary.Contains("UTF-8 with BOM") && preview.Summary.Contains("UTF-16 LE with BOM") && preview.Summary.Contains("no final line break"), "Unicode metadata missing");
        });
        test("file preview rejects linked selected paths", f =>
        {
            const string path = "ESAA/Settings/Synthetic.txt"; f.Write(path, "before"); f.Entries[path] = "after"; f.SaveZip(); var plan = f.Plan();
            var source = Path.GetFullPath(f.PathFor("ESAA/Settings")); var target = Path.GetFullPath(Path.Combine(f.DirectoryPath, "external-settings"));
            var boundary = Path.GetFullPath(f.DirectoryPath) + Path.DirectorySeparatorChar;
            Require(source.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) && target.StartsWith(boundary, StringComparison.OrdinalIgnoreCase), "Synthetic move escaped root");
            Directory.Move(source, target); SyntheticJunction.Create(source, target);
            Reject(() => GngUpdateService.PreviewFile(plan, path));
        });
    }

    private static void SetZipBytes(string zipPath, string name, byte[] bytes)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Update); zip.GetEntry(name)!.Delete();
        using var stream = zip.CreateEntry(name).Open(); stream.Write(bytes);
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException) { return; }
        throw new Exception("Expected stale/unsafe preview rejection");
    }
}
