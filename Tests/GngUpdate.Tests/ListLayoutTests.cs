using System.Text;
using VatscaUpdateChecker.Services;

internal static class ListLayoutTests
{
    private const string Lists = "ESAA/Settings/Lists.txt";
    private const string Plugin = "ESAA/Settings/Plugin.txt";
    private const string Custom = "Personal/Controller lists.txt";
    private static string Native(string section, int x, int y, int visible, string columns, int header = 0) =>
        $"{section}\r\nm_Visible:{visible}\r\nm_X:{x}\r\nm_Y:{y}\r\nm_HeaderOnly:{header}\r\nm_Column:1\r\nm_Column_0:{columns}\r\nm_OrderingColumn:0\r\nEND\r\n";
    private static string TopSky(string name, int x, int y, int visible, string columns) =>
        $"TopSky plugin:ACList/{name}/m_X:{x}\r\nTopSky plugin:ACList/{name}/m_Y:{y}\r\nTopSky plugin:ACList/{name}/m_Visible:{visible}\r\nTopSky plugin:ACList/{name}/m_Column_0:{columns}\r\n";
    private static string Route(string group, string file) => $"Settings\tSettingsfile{group}\t{file.Replace('/', '\\')}\r\n";

    internal static void Run(Action<string, Action<Fixture>> test)
    {
        test("list layout preserves only native position and whole-list visibility", f =>
        {
            Setup(f);
            var original = f.Snapshot();
            var plan = f.Plan();
            var file = plan.Files.Single(x => x.RelativePath.Replace('\\', '/') == Lists);
            var preview = GngUpdateService.PreviewFile(plan, Lists);
            Require(file.Action == "Merge list layout" && file.MergedBytes is not null, "List file was not merged");
            Require(preview.HasText && preview.AfterText!.Contains("m_X:-852") && preview.AfterText.Contains("m_Y:188") && preview.AfterText.Contains("m_Visible:1"), "Native placement/visibility was reset");
            Require(preview.AfterText!.Contains("m_Column_0:new-column") && preview.AfterText.Contains("m_HeaderOnly:0") && !preview.AfterText.Contains("old-column"), "Package columns or collapsed state were overridden");
            Require(preview.Summary.Contains("list", StringComparison.OrdinalIgnoreCase) && !preview.Summary.Contains("merged profile"), "List merge is mislabeled as a profile");
            var diff = GngTextDiff.Create(preview.BeforeText!, preview.AfterText!);
            Require(!diff.Text.Contains("-m_X:") && !diff.Text.Contains("+m_X:") && diff.Text.Contains("+m_Column_0:new-column"), "Diff misrepresents preserved placement");
            Require(original.All(x => f.Snapshot()[x.Key].SequenceEqual(x.Value)) && !Directory.Exists(f.StorageRoot), "Preview wrote files");
            var result = f.Install(plan);
            Require(f.Bytes(Lists).SequenceEqual(file.MergedBytes!), "Installed list differs from the reviewed result");
            f.Restore(result.BackupFolder);
            Require(original.Count == f.Snapshot().Count && original.All(x => f.Snapshot()[x.Key].SequenceEqual(x.Value)), "List restore did not recover exact original files");
        });
        test("list layout preserves TopSky identities while refreshing columns and RDF settings", f =>
        {
            Setup(f);
            f.Write(Plugin, "PLUGINS\r\n" + TopSky("Sector List", -40, 60, 0, "old") + TopSky("Other List", 900, 700, 1, "old-other") + "RDF Plugin for Euroscope:EnableDraw:1\r\nEND\r\n");
            f.Entries[Plugin] = "PLUGINS\r\n" + TopSky("Other List", 0, 0, 0, "new-other") + TopSky("Sector List", 20, 30, 1, "new") + "RDF Plugin for Euroscope:EnableDraw:0\r\nEND\r\n";
            f.SaveZip();
            var text = GngUpdateService.PreviewFile(f.Plan(), Plugin).AfterText!;
            Require(text.Contains("ACList/Sector List/m_X:-40") && text.Contains("ACList/Sector List/m_Y:60") && text.Contains("ACList/Sector List/m_Visible:0"), "TopSky list values were not preserved by identity");
            Require(text.Contains("ACList/Other List/m_X:900") && text.Contains("ACList/Other List/m_Column_0:new-other") && text.Contains("RDF Plugin for Euroscope:EnableDraw:0"), "New plugin settings or per-list identities were lost");
        });
        test("list layout retains new defaults and does not resurrect retired lists", f =>
        {
            Setup(f);
            f.Write(Lists, Native("SIL", 10, 20, 1, "old") + Native("SEL", 30, 40, 0, "retired"));
            f.Entries[Lists] = Native("SIL", 0, 0, 0, "new") + Native("ARR", 50, 60, 1, "brand-new");
            f.SaveZip();
            var text = GngUpdateService.PreviewFile(f.Plan(), Lists).AfterText!;
            Require(text.Contains(Native("ARR", 50, 60, 1, "brand-new")) && !text.Contains("SEL\r\n") && !text.Contains("retired"), "New/retired lists do not follow the package");
        });
        test("list layout uses the active custom profile route instead of inactive defaults", f =>
        {
            Setup(f, Custom);
            var custom = f.Bytes(Custom);
            f.Write(Lists, Native("SIL", 1, 2, 0, "inactive"));
            var plan = f.Plan();
            var preview = GngUpdateService.PreviewFile(plan, Lists);
            Require(preview.AfterText!.Contains("m_X:-852") && preview.AfterText.Contains("m_Y:188"), "Inactive default path won over active custom settings");
            var installed = f.Install(plan);
            Require(f.Bytes(Custom).SequenceEqual(custom), "Custom source file was modified");
            Require(f.Read(Fixture.ProfilePath).Contains(Route("SIL", Lists)), "Profile did not select the refreshed package destination");
            f.Restore(installed.BackupFolder);
            Require(f.Bytes(Custom).SequenceEqual(custom) && f.Read(Fixture.ProfilePath).Contains(Route("SIL", Custom)), "Restore lost custom-route configuration");
        });
        test("list layout accepts reviewed-root absolute and package-relative custom routes", f =>
        {
            Setup(f, Custom);
            foreach (var path in new[] { f.PathFor(Custom), "\\" + Custom.Replace('/', '\\'), ".\\" + Custom.Replace('/', '\\') })
            {
                f.Write(Fixture.ProfilePath, Fixture.LocalProfile + Route("SIL", path) + Route("PLUGINS", Plugin));
                Require(GngUpdateService.PreviewFile(f.Plan(), Lists).AfterText!.Contains("m_X:-852"), "Supported route form lost position");
            }
        });
        test("list layout rejects conflicting profiles instead of choosing the first", f =>
        {
            Setup(f, Custom);
            f.Write("ESAA SECOND.prf", Fixture.LocalProfile + Route("SIL", "Personal/Other lists.txt"));
            f.Entries["ESAA SECOND.prf"] = Fixture.PackageProfile + Route("SIL", Lists);
            f.Write("Personal/Other lists.txt", Native("SIL", 100, 200, 1, "second"));
            f.SaveZip();
            Reject(() => f.Plan());
            f.Write("Personal/Other lists.txt", Native("SIL", -852, 188, 1, "different-old-column"));
            Require(GngUpdateService.PreviewFile(f.Plan(), Lists).AfterText!.Contains("m_X:-852"), "Agreeing positions were rejected solely for differing columns");
        });
        test("list layout stale custom inputs invalidate both file diff and installation", f =>
        {
            Setup(f, Custom);
            var plan = f.Plan();
            f.Write(Custom, Native("SIL", -853, 188, 1, "old-column", 1));
            var before = f.Snapshot();
            Reject(() => GngUpdateService.PreviewFile(plan, Lists));
            Reject(() => f.Install(plan));
            Require(before.All(x => f.Snapshot()[x.Key].SequenceEqual(x.Value)), "Rejected stale merge modified files");
        });
        test("list layout source edits invalidate approval even when coordinates stay identical", f =>
        {
            Setup(f, Custom);
            var plan = f.Plan();
            f.Write(Custom, Native("SIL", -852, 188, 1, "edited-column", 1));
            Reject(() => GngUpdateService.PreviewFile(plan, Lists));
            Reject(() => f.Install(plan));
        });
        test("list layout is on by default and can be disabled for package defaults", f =>
        {
            Setup(f, Custom);
            Require(f.Plan().PreserveListLayout, "List preservation is not on by default");
            var plan = f.Plan(preserveListLayout: false);
            Require(!plan.PreserveListLayout, "Disabled preference was lost");
            Require(GngUpdateService.PreviewFile(plan, Lists).AfterText == f.Entries[Lists], "Opting out did not show exact package defaults");
            f.Write(Custom, "Custom format that cannot be merged");
            Require(GngUpdateService.PreviewFile(plan, Lists).AfterText == f.Entries[Lists], "Disabled merge still depends on a custom source");
            var result = f.Install(plan);
            Require(f.Read(Lists) == f.Entries[Lists] && f.Read(Plugin) == f.Entries[Plugin], "Install silently re-enabled preservation");
            f.Restore(result.BackupFolder);
        });
        test("list layout opt-out permits unresolved custom routes without reading them", f =>
        {
            Setup(f);
            f.Write(Fixture.ProfilePath, Fixture.LocalProfile + Route("SIL", "C:\\Missing-controller-data\\Lists.txt"));
            Reject(() => f.Plan());
            var plan = f.Plan(preserveListLayout: false);
            Require(GngUpdateService.PreviewFile(plan, Lists).AfterText == f.Entries[Lists], "Unsafe old route prevented explicit package-default choice");
            f.Install(plan);
            Require(f.Read(Lists) == f.Entries[Lists], "Opt-out did not survive install revalidation");
        });
        test("list layout changed profile routing invalidates a file comparison", f =>
        {
            Setup(f, Custom);
            var plan = f.Plan();
            f.Write("Personal/Second.txt", Native("SIL", -852, 188, 1, "same-values", 1));
            f.Write(Fixture.ProfilePath, Fixture.LocalProfile + Route("SIL", "Personal/Second.txt") + Route("PLUGINS", Plugin));
            Reject(() => GngUpdateService.PreviewFile(plan, Lists));
            Reject(() => f.Install(plan));
        });
        test("list layout pins custom read dependencies throughout installation", f =>
        {
            Setup(f, Custom);
            var checkedLock = false;
            f.Install(f.Plan(), new InlineProgress(message =>
            {
                if (!message.StartsWith("Installing ", StringComparison.Ordinal) || checkedLock) return;
                checkedLock = true;
                var denied = false;
                try { using var write = new FileStream(f.PathFor(Custom), FileMode.Open, FileAccess.Write, FileShare.ReadWrite); }
                catch (IOException) { denied = true; }
                Require(denied, "Custom layout input remained writable during installation");
            }));
            Require(checkedLock, "Dependency lock was never exercised");
        });
        foreach (var bad in new[] { "m_X:12oops", "m_X:2147483648", "m_Visible:2", "m_X:1\r\nm_X:2" })
            test("list layout rejects ambiguous or invalid local placement: " + bad.Replace("\r\n", " / "), f =>
            {
                Setup(f);
                var text = Native("SIL", -852, 188, 1, "old-column");
                text = bad.StartsWith("m_Visible") ? text.Replace("m_Visible:1", bad) : text.Replace("m_X:-852", bad);
                f.Write(Lists, text);
                Reject(() => f.Plan());
            });
        test("list layout rejects duplicate sections and routes", f =>
        {
            Setup(f);
            f.Write(Lists, Native("SIL", 1, 2, 1, "a") + Native("SIL", 3, 4, 1, "b"));
            Reject(() => f.Plan());
            Setup(f);
            f.Write(Fixture.ProfilePath, Fixture.LocalProfile + Route("SIL", Lists) + Route("SIL", Custom));
            Reject(() => f.Plan());
        });
        test("list layout rejects missing external linked and protected sources", f =>
        {
            Setup(f, Custom);
            f.Write(Fixture.ProfilePath, Fixture.LocalProfile + Route("SIL", "Personal/Missing.txt"));
            Reject(() => f.Plan());
            var external = Path.Combine(f.DirectoryPath, "outside.txt");
            File.WriteAllText(external, Native("SIL", 1, 2, 1, "outside"));
            f.Write(Fixture.ProfilePath, Fixture.LocalProfile + Route("SIL", external));
            Reject(() => f.Plan());
            Setup(f, Custom);
            Reject(() => f.Plan([f.PathFor(Custom)]));
            var alias = Path.GetFullPath(f.PathFor("Alias"));
            var target = Path.GetFullPath(f.PathFor("Personal"));
            SyntheticJunction.Create(alias, target);
            f.Write(Fixture.ProfilePath, Fixture.LocalProfile + Route("SIL", "Alias/Controller lists.txt"));
            Reject(() => f.Plan());
        });
        test("list layout fresh files use package defaults without inventing saved positions", f =>
        {
            Setup(f);
            File.Delete(f.PathFor(Lists));
            File.Delete(f.PathFor(Plugin));
            File.Delete(f.PathFor(Fixture.ProfilePath));
            f.ZipPath = Path.Combine(f.DirectoryPath, "ESAA-Full-Package_" + Fixture.NewIdentity + ".zip");
            f.SaveZip();
            var plan = f.Plan();
            var result = GngUpdateService.PreviewFile(plan, Lists);
            Require(result.AfterText == f.Entries[Lists], "Fresh defaults were altered without an existing layout");
        });
        test("list layout diff decodes merged Windows1252 bytes consistently", f =>
        {
            Setup(f);
            f.Entries[Lists] = Native("SIL", 0, 47, 0, "new-Ã¥");
            f.SaveZip();
            var preview = GngUpdateService.PreviewFile(f.Plan(), Lists);
            Require(preview.AfterText!.Contains("new-Ã¥"), "BOM-less merged settings were incorrectly guessed as UTF8");
        });
    }

    private static void Setup(Fixture f, string source = Lists)
    {
        f.Write(source, Native("SIL", -852, 188, 1, "old-column", 1));
        f.Entries[Lists] = Native("SIL", 0, 47, 0, "new-column");
        f.Write(Plugin, "PLUGINS\r\n" + TopSky("Sector List", 400, 500, 1, "old") + "END\r\n");
        f.Entries[Plugin] = "PLUGINS\r\n" + TopSky("Sector List", 0, 0, 0, "new") + "END\r\n";
        f.Write(Fixture.ProfilePath, Fixture.LocalProfile + Route("SIL", source) + Route("PLUGINS", Plugin));
        f.Entries[Fixture.ProfilePath] = Fixture.PackageProfile + Route("SIL", Lists) + Route("PLUGINS", Plugin);
        f.SaveZip();
    }

    private static void Require(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException) { return; }
        throw new Exception("Expected a rejected list-layout review");
    }
    private sealed class InlineProgress(Action<string> action) : IProgress<string>
    {
        public void Report(string value) => action(value);
    }
}
