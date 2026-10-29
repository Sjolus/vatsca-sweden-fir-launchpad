using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

internal static class ReviewAssessmentTests
{
    private const string NextIdentity = "20261029191004-261101-0001";

    internal static void Run(Action<string, Action<Fixture>> test)
    {
        test("assessment flags an existing installation without a verified baseline without changing files", f =>
        {
            var before = f.Snapshot(); var plan = f.Plan(); var result = GngReviewNotes.Assess(plan);
            Require(plan.ReviewContext is { HasExistingFiles: true, HasVerifiedBaseline: false }, "Missing baseline evidence was not recorded.");
            Require(result.NeedsAttention && result.Points.Any(point => point.Contains("compare your current files")), "Unmanaged existing files received reassurance.");
            Require(before.Count == f.Snapshot().Count && before.All(file => file.Value.SequenceEqual(f.Snapshot()[file.Key])), "Assessment changed existing files.");
        });
        test("assessment treats a genuine fresh Full install as normal and mentions controller details", f =>
        {
            string root = Path.Combine(f.DirectoryPath, "empty-synthetic"); Directory.CreateDirectory(root);
            f.ZipPath = Path.Combine(f.DirectoryPath, "ESAA-Full-Package_" + Fixture.NewIdentity + ".zip"); f.SaveZip();
            var plan = GngUpdateService.BuildPlan(f.ZipPath, root, null, f.Context);
            var result = GngReviewNotes.Assess(plan);
            Require(!result.NeedsAttention && result.Heading == "Ready to install", "Empty install was treated as an unknown existing installation.");
            Require(result.Points.Any(point => point.Contains("controller details")), "New profile follow-up disappeared.");
            Require(plan.Warnings.Any(warning => warning.StartsWith("No verified previous package")), "Full technical notes were lost.");
        });
        test("assessment treats a fresh GNG install beside unrelated EuroScope samples as normal", f =>
        {
            string root = Path.Combine(f.DirectoryPath, "samples-only-synthetic");
            Directory.CreateDirectory(Path.Combine(root, "Sample"));
            string sample = Path.Combine(root, "Sample", "example.sct");
            File.WriteAllText(sample, "synthetic unrelated EuroScope sample sector");
            string profile = Path.Combine(root, "EuroScope.prf");
            File.WriteAllText(profile, "synthetic unrelated EuroScope sample profile");
            f.ZipPath = Path.Combine(f.DirectoryPath, "ESAA-Full-Package_" + Fixture.NewIdentity + ".zip"); f.SaveZip();
            var plan = GngUpdateService.BuildPlan(f.ZipPath, root, null, f.Context);
            Require(plan.ReviewContext is { HasExistingFiles: false, HasVerifiedBaseline: false } && !GngReviewNotes.Assess(plan).NeedsAttention,
                "Unrelated sample files were treated as an existing GNG installation.");
            Require(File.ReadAllText(sample) == "synthetic unrelated EuroScope sample sector" &&
                File.ReadAllText(profile) == "synthetic unrelated EuroScope sample profile", "Fresh installation assessment changed unrelated sample files.");
        });
        test("assessment accepts a normal managed upgrade and verified reused DLL", f =>
        {
            PrepareManagedUpgrade(f); f.Entries.Remove("ESAA/Plugins/TopSky.dll"); f.SaveZip();
            var plan = f.Plan(); var result = GngReviewNotes.Assess(plan, f.PathFor(Fixture.ProfilePath));
            Require(plan.ReviewContext is { HasVerifiedBaseline: true, UnverifiedDependencyCount: 0, PreservedCustomPluginCount: 0 }, "Verified reused DLL was not matched to its baseline.");
            Require(!result.NeedsAttention, "Routine managed update was flagged: " + string.Join("; ", result.Points));
        });
        test("assessment flags a reused DLL changed since its verified baseline", f =>
        {
            PrepareManagedUpgrade(f); f.Entries.Remove("ESAA/Plugins/TopSky.dll"); f.SaveZip();
            f.Write("ESAA/Plugins/TopSky.dll", "synthetic manually replaced DLL");
            var plan = f.Plan(); var result = GngReviewNotes.Assess(plan);
            Require(plan.ReviewContext!.UnverifiedDependencyCount == 1 && result.NeedsAttention &&
                result.Points.Any(point => point.Contains("could not be matched to the original package")), "Changed dependency was presented as verified.");
            Require(result.Points.Any(point => point.Contains("TopSky.dll") && point.Contains(Fixture.ProfilePath)), "Reused DLL warning does not identify the plugin and its profile.");
        });
        test("assessment flags a reused DLL absent from the verified inventory", f =>
        {
            PrepareManagedUpgrade(f);
            f.Entries[Fixture.ProfilePath] += "Plugins\tPlugin9\tESAA\\Plugins\\Extra.dll\r\n";
            f.SaveZip(); f.Write("ESAA/Plugins/Extra.dll", "synthetic separately installed DLL");
            var plan = f.Plan();
            Require(plan.ReviewContext!.UnverifiedDependencyCount == 1 && GngReviewNotes.Assess(plan).NeedsAttention, "Unmatched dependency was presented as verified.");
        });
        test("assessment groups customized replacements instead of repeating file warnings", f =>
        {
            for (int i = 0; i < 8; i++) f.Entries[$"ESAA/Settings/Custom{i}.txt"] = "synthetic package default";
            f.SaveZip(); PrepareManagedUpgrade(f);
            for (int i = 0; i < 8; i++) f.Write($"ESAA/Settings/Custom{i}.txt", "synthetic customized default");
            var plan = f.Plan(); var result = GngReviewNotes.Assess(plan);
            Require(plan.Warnings.Count(warning => warning.StartsWith("Customized package default")) == 8, "Technical per-file warnings were lost.");
            Require(result.Points.Count(point => point.Contains("customized package")) == 1 && result.Points.Any(point => point.StartsWith("8 customized package files")), "Customized files became a warning wall.");
        });
        test("assessment flags a modified DLL replaced by a promoted package plugin", f =>
        {
            PrepareManagedUpgrade(f);
            f.Write("ESAA/Plugins/TopSky.dll", "synthetic independently changed DLL");
            f.Entries["ESAA/Plugins/Updated Plugin/TopSky.dll"] = "synthetic official promoted DLL"; f.SaveZip();
            var plan = f.Plan(); var plugin = plan.Files.Single(file => file.RelativePath.EndsWith("TopSky.dll", StringComparison.Ordinal));
            Require(plugin.Action == "Promote plugin" && plan.ReviewContext!.CustomizedReplacementCount == 1, "Promotion lost its action or local-change evidence.");
            Require(GngReviewNotes.Assess(plan).Points.Any(point => point.Contains("1 customized package file")), "Modified promoted plugin received green reassurance.");
        });
        test("assessment retains customized-default concerns after list layout merging", f =>
        {
            const string path = "ESAA/Settings/Lists.txt";
            static string Lists(int x, string columns) => $"SIL\r\nm_X:{x}\r\nm_Y:20\r\nm_Visible:1\r\nm_Column:1\r\nm_Column_0:{columns}\r\nEND\r\n";
            f.Entries[path] = Lists(0, "original"); f.SaveZip(); PrepareManagedUpgrade(f);
            f.Write(path, Lists(80, "customized"));
            f.Entries[path] = Lists(0, "new-official"); f.SaveZip();
            var plan = f.Plan(); var list = plan.Files.Single(file => file.RelativePath.Replace('\\', '/') == path);
            Require(list.Action == "Merge list layout" && plan.ReviewContext!.CustomizedReplacementCount == 1, "List merge lost customized replacement evidence.");
            Require(GngReviewNotes.Assess(plan).NeedsAttention, "Merged custom list defaults received green reassurance.");
            f.Entries[path] = Lists(0, "customized"); f.SaveZip();
            var unchanged = f.Plan();
            Require(unchanged.Files.Single(file => file.RelativePath.Replace('\\', '/') == path).Action == "Unchanged" && unchanged.ReviewContext!.CustomizedReplacementCount == 0, "Preserved unchanged list retained a false replacement warning.");
        });
        test("assessment flags incoming files colliding with untracked local files", f =>
        {
            PrepareManagedUpgrade(f);
            const string path = "ESAA/Settings/New.txt";
            f.Write(path, "synthetic custom untracked contents"); f.Entries[path] = "synthetic new official contents"; f.SaveZip();
            var plan = f.Plan(); var result = GngReviewNotes.Assess(plan);
            Require(plan.ReviewContext!.UnmatchedReplacementCount == 1 && result.NeedsAttention && result.Points.Any(point => point.Contains("not be compared with the original package and will be replaced")), "Untracked local collision received green reassurance.");
            f.Write(path, f.Entries[path]);
            Require(f.Plan().ReviewContext!.UnmatchedReplacementCount == 0, "Byte-identical untracked file was treated as a replacement.");
        });
        test("assessment flags retained custom plugins as uncertainty rather than incompatibility", f =>
        {
            f.Install(f.Plan()); Advance(f);
            var plan = f.Plan(); var result = GngReviewNotes.Assess(plan);
            Require(plan.ReviewContext!.PreservedCustomPluginCount == 1 && result.NeedsAttention, "Retained custom plugin uncertainty disappeared.");
            var plugin = plan.ReviewContext.PreservedCustomPlugins.Single();
            Require(plugin.FileName == "VatEFS.dll" && plugin.Profiles.SequenceEqual([Fixture.ProfilePath]) && !plugin.SuppliedByPackage, "Retained plugin facts were not collected.");
            Require(result.Points.Any(point => point.Contains("VatEFS.dll") && point.Contains(Fixture.ProfilePath) && point.Contains("does not include or update it") && point.Contains("Check VatEFS in Apps")), "Retained plugin warning lacks its identity, impact or next step.");
            Require(plan.Warnings.Any(point => point.Contains(plugin.Location) && point.Contains("left untouched")), "Technical notes omitted the retained DLL location.");
        });
        test("assessment groups the same custom plugin across profiles without confusing its folder with package membership", f =>
        {
            PrepareManagedUpgrade(f);
            const string dll = "ESAA/Plugins/Custom.dll";
            const string otherProfile = "ESAA TWR.prf";
            f.Write(dll, "synthetic custom plugin");
            f.Write(Fixture.ProfilePath, f.Read(Fixture.ProfilePath) + "Plugins\tPlugin9\t" + dll + "\r\n");
            f.Entries[otherProfile] = f.Entries[Fixture.ProfilePath];
            f.Write(otherProfile, f.Read(Fixture.ProfilePath)); f.SaveZip();
            var plan = f.Plan(); var plugin = plan.ReviewContext!.PreservedCustomPlugins.Single();
            Require(plugin.FileName == "Custom.dll" && plugin.Profiles.Count == 2 && plan.ReviewContext.PreservedCustomPluginCount == 1, "Duplicate profile references became duplicate plugins.");
            var result = GngReviewNotes.Assess(plan);
            Require(result.Points.Any(point => point.Contains("Custom.dll") && point.Contains(otherProfile) && point.Contains(Fixture.ProfilePath)), "Affected profiles were hidden.");
            Require(result.Points.All(point => !point.Contains("outside")), "A plugin inside EuroScope was described as outside its folder.");
        });
        test("assessment distinguishes a custom plugin entry from a bundled DLL", f =>
        {
            PrepareManagedUpgrade(f);
            const string dll = "ESAA/Plugins/Helper.dll";
            f.Entries[dll] = "synthetic bundled support DLL";
            f.Write(Fixture.ProfilePath, f.Read(Fixture.ProfilePath) + "Plugins\tPlugin9\t" + dll + "\r\n");
            f.SaveZip();
            var plan = f.Plan(); var result = GngReviewNotes.Assess(plan);
            Require(plan.ReviewContext!.PreservedCustomPlugins.Single().SuppliedByPackage, "Supplied support DLL was described as absent from the ZIP.");
            Require(result.Points.Any(point => point.Contains("Helper.dll") && point.Contains("GNG supplies its DLL")), "Summary incorrectly promises to leave a supplied DLL untouched.");
        });
        test("assessment plugin identity remains bound to the approved installation plan", f =>
        {
            f.Install(f.Plan()); Advance(f);
            var plan = f.Plan();
            var altered = plan with { ReviewContext = plan.ReviewContext! with
            {
                PreservedCustomPlugins = [plan.ReviewContext!.PreservedCustomPlugins.Single() with { FileName = "Misidentified.dll" }]
            } };
            var before = f.Snapshot();
            try { f.Install(altered); throw new Exception("Altered plugin review installed files."); }
            catch (InvalidOperationException) { }
            Require(before.Count == f.Snapshot().Count && before.All(file => file.Value.SequenceEqual(f.Snapshot()[file.Key])), "Changed review facts were accepted before installation.");
        });
        test("assessment only highlights an omitted profile when it is selected", f =>
        {
            PrepareManagedUpgrade(f); f.Write("ESAA UNUSED.prf", "synthetic old profile");
            var plan = f.Plan();
            Require(!GngReviewNotes.Assess(plan, f.PathFor(Fixture.ProfilePath)).NeedsAttention, "An unused old profile blocked the normal summary.");
            Require(GngReviewNotes.Assess(plan, f.PathFor("ESAA UNUSED.prf")).Points.Any(point => point.Contains("selected launch profile")), "Selected old profile was not highlighted.");
            Require(GngReviewNotes.Assess(plan, f.PathFor("Custom.prf")).NeedsAttention, "Custom-named selected profile was ignored.");
        });
        test("assessment mentions newly added profiles during an otherwise normal upgrade", f =>
        {
            PrepareManagedUpgrade(f); f.Entries["ESAA NEW.prf"] = f.Entries[Fixture.ProfilePath]; f.SaveZip();
            var result = GngReviewNotes.Assess(f.Plan());
            Require(!result.NeedsAttention && result.Points.Any(point => point.StartsWith("1 new EuroScope profile")), "Added profile follow-up was missing or treated as a failure.");
        });
        test("assessment flags Testing list resets repair and RDF overrides", f =>
        {
            PrepareManagedUpgrade(f);
            Require(GngReviewNotes.Assess(f.Plan(preserveListLayout: false)).Points.Any(point => point.Contains("reset to the package defaults")), "List reset received normal reassurance.");
            f.Write("ESAA/Plugins/TopSkySettingsLocal.txt", "RDF_Mode=1");
            Require(GngReviewNotes.Assess(f.Plan()).Points.Any(point => point.Contains("override the package's RDF setting")), "Local RDF override was hidden.");
            f.ZipPath = Path.Combine(f.DirectoryPath, "ESAA-Testing_" + NextIdentity + ".zip"); f.SaveZip();
            var testing = GngUpdateService.BuildPlan(f.ZipPath, f.Root, null, f.Context, allowTesting: true);
            Require(GngReviewNotes.Assess(testing).Points.Any(point => point.Contains("testing package")), "Testing received stable reassurance.");
            f.Install(testing);
            f.Write("ESAA/Plugins/TopSkySettings.txt", "synthetic repair candidate");
            var repair = GngUpdateService.BuildPlan(f.ZipPath, f.Root, null, f.Context, allowTesting: true);
            Require(GngReviewNotes.Assess(repair).Points.Any(point => point.Contains("version is already present")), "Repair was presented as a newer release.");
        });
        test("assessment fails conservatively for unknown notes and missing structured evidence", f =>
        {
            PrepareManagedUpgrade(f); var plan = f.Plan();
            var unknown = plan with { Warnings = plan.Warnings.Append("Every replaced file is backed up before installation. A new unclassified rule applies.").ToArray() };
            Require(GngReviewNotes.Assess(unknown).NeedsAttention && GngReviewNotes.Assess(unknown).Points.Any(point => point.Contains("A new unclassified rule applies.")), "A new unknown condition was suppressed or replaced by a vague warning.");
            Require(GngReviewNotes.Assess(plan with { ReviewContext = null }).NeedsAttention, "Absent structured assessment became a green summary.");
        });
        test("assessment stale baseline requires a fresh review before installation", f =>
        {
            PrepareManagedUpgrade(f); var plan = f.Plan();
            Require(!GngReviewNotes.Assess(plan).NeedsAttention, "Expected normal initial assessment.");
            var cached = GngUpdateService.GetCachedPackage(f.Root, f.Context)!;
            File.WriteAllText(cached, "synthetic damaged cached package");
            Require(GngReviewNotes.Assess(f.Plan()).NeedsAttention, "Lost baseline was not surfaced.");
            var before = f.Snapshot();
            try { f.Install(plan); throw new Exception("Stale baseline approval installed files."); }
            catch (InvalidOperationException) { }
            Require(before.Count == f.Snapshot().Count && before.All(file => file.Value.SequenceEqual(f.Snapshot()[file.Key])), "Stale baseline rejection changed existing files.");
        });
    }

    private static void PrepareManagedUpgrade(Fixture f)
    {
        f.Write(Fixture.ProfilePath, Fixture.PackageProfile.Replace(Fixture.NewStem, Fixture.OldStem));
        f.Install(f.Plan()); Advance(f);
    }
    private static void Advance(Fixture f)
    {
        var entries = f.Entries.ToArray(); f.Entries.Clear();
        foreach (var item in entries) f.Entries[item.Key.Replace(Fixture.NewIdentity, NextIdentity)] = item.Value.Replace(Fixture.NewIdentity, NextIdentity);
        f.ZipPath = Path.Combine(f.DirectoryPath, "ESAA-Update-Only_" + NextIdentity + ".zip"); f.SaveZip();
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
