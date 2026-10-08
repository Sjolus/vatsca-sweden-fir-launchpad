using System.IO.Compression;
using System.Text;
using VatscaUpdateChecker.Services;

internal static class BackendListLayoutTests
{
    private const string Lists = "ESAA/Settings/Lists.txt";
    private const string Custom = "Personal/Lists.txt";
    private const string Plugin = "ESAA/Settings/Plugin.txt";
    private static string Native(string group, int x, int y, int visible, string column = "package") =>
        $"{group}\r\nm_X:{x}\r\nm_Y:{y}\r\nm_Visible:{visible}\r\nm_Column:{column}\r\nEND\r\n";
    private static string Route(string group, string path) => $"Settings\tSettingsfile{group}\t{path.Replace('/', '\\')}\r\n";
    private static string After(Fixture f, string path = Lists) => GngUpdateService.PreviewFile(f.Plan(), path).AfterText!;

    internal static void Run(Action<string, Action<Fixture>> test)
    {
        test("list backend routes only the active native group from each source", f =>
        {
            f.Write(Custom, Native("SIL", -30, 40, 1) + Native("SEL", 900, 900, 1));
            f.Write("Personal/Other.txt", Native("SIL", 800, 800, 1) + Native("SEL", 50, -60, 0));
            f.Write(Fixture.ProfilePath, Fixture.LocalProfile + Route("SIL", Custom) + Route("SEL", "Personal/Other.txt"));
            f.Entries[Fixture.ProfilePath] = Fixture.PackageProfile + Route("SIL", Lists) + Route("SEL", Lists);
            f.Entries[Lists] = Native("SIL", 1, 2, 0) + Native("SEL", 3, 4, 1); f.SaveZip();
            Require(After(f) == Native("SIL", -30, 40, 1) + Native("SEL", 50, -60, 0), "Inactive source sections leaked into the merge");
        });
        test("list backend new profile suppresses orphan default-path layouts", f =>
        {
            f.Write(Lists, Native("SIL", 90, 80, 1));
            f.Entries["ESAA NEW.prf"] = Fixture.PackageProfile + Route("SIL", Lists);
            f.Entries[Lists] = Native("SIL", 1, 2, 0); f.SaveZip();
            var plan = f.Plan();
            Require(GngUpdateService.PreviewFile(plan, Lists).AfterText == f.Entries[Lists], "A new profile inherited an inactive layout");
            f.Write("ESAA NEW.prf", Fixture.LocalProfile + Route("SIL", Lists));
            Reject(() => GngUpdateService.PreviewFile(plan, Lists));
            Reject(() => f.Install(plan));
        });
        test("list backend newly introduced route uses package values", f =>
        {
            f.Write(Lists, Native("SIL", 90, 80, 1));
            f.Entries[Fixture.ProfilePath] = Fixture.PackageProfile + Route("SIL", Lists);
            f.Entries[Lists] = Native("SIL", 1, 2, 0); f.SaveZip();
            Require(After(f) == f.Entries[Lists], "A missing old route inherited inactive values");
        });
        test("list backend new TopSky identities do not inherit inactive defaults", f =>
        {
            const string oldList = "TopSky plugin:ACList/Old list";
            const string newList = "TopSky plugin:ACList/New list";
            string List(string name, int x) => $"{name}/m_X:{x}\r\n{name}/m_Y:20\r\n{name}/m_Visible:1\r\n";
            f.Write(Custom, "PLUGINS\r\n" + List(oldList, 100) + "END\r\n");
            f.Write(Plugin, "PLUGINS\r\n" + List(newList, 999) + "END\r\n");
            f.Write(Fixture.ProfilePath, Fixture.LocalProfile + Route("PLUGINS", Custom));
            f.Entries[Fixture.ProfilePath] = Fixture.PackageProfile + Route("PLUGINS", Plugin);
            f.Entries[Plugin] = "PLUGINS\r\n" + List(newList, 5) + "END\r\n"; f.SaveZip();
            Require(After(f, Plugin) == f.Entries[Plugin], "A new plugin list used the inactive canonical file");
        });
        test("list backend follows mixed-case profile section and route keys", f =>
        {
            f.Write(Custom, Native("SIL", -30, 40, 1));
            f.Write(Fixture.ProfilePath, Fixture.LocalProfile + "sEtTiNgS\tsettingsfileSiL\tPersonal\\Lists.txt\r\n");
            f.Entries[Fixture.ProfilePath] = Fixture.PackageProfile + "settings\tSETTINGSFILESIL\tESAA\\Settings\\Lists.txt\r\n";
            f.Entries[Lists] = Native("SIL", 1, 2, 0); f.SaveZip();
            Require(After(f) == Native("SIL", -30, 40, 1), "Profile key case changed the active source");
        });
        test("list backend keeps dependencies for plugin files without TopSky lists", f =>
        {
            f.Write(Custom, "PLUGINS\r\nRDF Plugin for Euroscope:EnableDraw:1\r\nEND\r\n");
            f.Write(Fixture.ProfilePath, Fixture.LocalProfile + Route("PLUGINS", Custom));
            f.Entries[Fixture.ProfilePath] = Fixture.PackageProfile + Route("PLUGINS", Plugin);
            f.Entries[Plugin] = "PLUGINS\r\nRDF Plugin for Euroscope:EnableDraw:0\r\nEND\r\n"; f.SaveZip();
            var plan = f.Plan();
            Require(GngUpdateService.PreviewFile(plan, Plugin).AfterText == f.Entries[Plugin], "Non-list plugin settings were preserved");
            f.Write(Custom, "PLUGINS\r\nRDF Plugin for Euroscope:EnableDraw:0\r\nEND\r\n");
            Reject(() => GngUpdateService.PreviewFile(plan, Plugin));
            Reject(() => f.Install(plan));
        });
        test("list backend preserves package encoding BOM and line endings", f =>
        {
            f.Write(Fixture.ProfilePath, Fixture.LocalProfile + Route("SIL", Custom));
            Directory.CreateDirectory(Path.GetDirectoryName(f.PathFor(Custom))!);
            File.WriteAllText(f.PathFor(Custom), Native("SIL", -30, 40, 1, "Åke"), new UnicodeEncoding(false, true, true));
            f.Entries[Fixture.ProfilePath] = Fixture.PackageProfile + Route("SIL", Lists);
            f.Entries[Lists] = Native("SIL", 1, 2, 0, "Ångström").Replace("\r\n", "\n").TrimEnd('\n'); f.SaveZip();
            var encoding = new UTF8Encoding(true, true);
            using (var zip = ZipFile.Open(f.ZipPath, ZipArchiveMode.Update))
            {
                zip.GetEntry(Lists)!.Delete();
                using var output = zip.CreateEntry(Lists).Open();
                output.Write(encoding.GetPreamble()); output.Write(encoding.GetBytes(f.Entries[Lists]));
            }
            var plan = f.Plan(); var file = plan.Files.Single(p => p.RelativePath.Replace('\\', '/') == Lists);
            var expected = Native("SIL", -30, 40, 1, "Ångström").Replace("\r\n", "\n").TrimEnd('\n');
            Require(file.MergedBytes!.AsSpan().StartsWith(encoding.GetPreamble()), "Package BOM was lost");
            Require(GngUpdateService.PreviewFile(plan, Lists).AfterText == expected, "Package text framing or Unicode changed");
            f.Install(plan); Require(f.Bytes(Lists).SequenceEqual(file.MergedBytes!), "Encoded installed result differs from review");
        });
        test("list backend supports source files which are also reviewed replacements", f =>
        {
            f.Write(Custom, Native("SIL", -30, 40, 1, "old"));
            f.Write(Fixture.ProfilePath, Fixture.LocalProfile + Route("SIL", Custom));
            f.Entries[Fixture.ProfilePath] = Fixture.PackageProfile + Route("SIL", Lists);
            f.Entries[Lists] = Native("SIL", 1, 2, 0);
            f.Entries[Custom] = Native("SIL", 6, 7, 0, "refreshed"); f.SaveZip();
            var before = f.Snapshot(); var result = f.Install(f.Plan());
            Require(f.Read(Lists) == Native("SIL", -30, 40, 1), "Source/write overlap lost original positions");
            Require(f.Read(Custom) == Native("SIL", -30, 40, 1, "refreshed"), "Reviewed source replacement was blocked or incorrect");
            f.Restore(result.BackupFolder);
            Require(before.Count == f.Snapshot().Count && before.All(p => f.Snapshot()[p.Key].SequenceEqual(p.Value)), "Source/write overlap did not restore exactly");
        });
        test("list backend rejects malformed recognized settings under custom filenames", f =>
        {
            f.Entries["ESAA/Settings/Combined.txt"] = "SIL\r\nm_X:invalid\r\nm_Y:2\r\nm_Visible:0\r\nEND\r\n";
            f.SaveZip(); Reject(() => f.Plan());
            Require(f.Plan(preserveListLayout: false).Files.Count > 0, "Explicit opt-out still parsed layout text");
        });
        test("list backend bounds combined retained layout text", f =>
        {
            for (int i = 0; i < 17; i++)
                f.Entries[$"ESAA/Settings/Lists{i}.txt"] = Native("SIL", 1, 2, 0) + ";" + new string('a', 1024 * 1024) + "\r\n";
            f.SaveZip(); Reject(() => f.Plan());
        });
        test("list backend unchanged preserved layout does not warn of replacement", f =>
        {
            f.Write(Lists, Native("SIL", 90, 80, 1, "old"));
            f.Entries[Lists] = Native("SIL", 1, 2, 0); f.SaveZip();
            f.Install(f.Plan(preserveListLayout: false));
            f.Write(Lists, Native("SIL", -30, 40, 1));
            var plan = f.Plan();
            var file = plan.Files.Single(p => p.RelativePath.Replace('\\', '/') == Lists);
            Require(file.Action == "Unchanged", "Only retained positions should be unchanged");
            Require(!plan.Warnings.Contains("Customized package default will be replaced: " + file.RelativePath), "Unchanged list has a false replacement warning");
            Require(GngUpdateService.PreviewFile(plan, Lists).AfterText == f.Read(Lists), "Unchanged layout diff does not show the exact retained bytes");
        });
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException) { return; }
        throw new Exception("Expected list layout review rejection");
    }
}
