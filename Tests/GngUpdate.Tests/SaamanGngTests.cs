using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

internal static class SaamanGngTests
{
    internal static void Run(Action<string, Action<Fixture>> test)
    {
        test("SAAMAN GNG merge keeps first slot removes MAESTRO and restores exact prior bytes", f =>
        {
            var policy = Prepare(f);
            var old = f.Bytes(Fixture.ProfilePath);
            f.Entries["ESAA TWR.prf"] = f.Entries[Fixture.ProfilePath]; f.SaveZip();
            var plan = Plan(f, policy);
            var preview = GngUpdateService.PreviewFile(plan, Fixture.ProfilePath);
            Require(preview.AfterText!.Contains("Plugins\tPlugin0\t" + policy.DllPath), "SAAMAN lost first slot.");
            Require(!preview.AfterText.Contains("MAESTRO.dll"), "MAESTRO returned in preview.");
            Require(preview.AfterText.Contains("Plugin0Display0\tCustom APP screen") &&
                preview.AfterText.Contains("Plugin1CustomFlag\tpreserve"), "Permissions or suffix were lost.");
            var tower = GngUpdateService.PreviewFile(plan, "ESAA TWR.prf").AfterText!;
            Require(!tower.Contains("SAAMAN.dll") && tower.Contains("MAESTRO.dll"), "Policy changed a non-target profile.");
            var result = f.Install(plan);
            Require(f.Bytes(Fixture.ProfilePath).SequenceEqual(plan.Files.Single(p => p.RelativePath == Fixture.ProfilePath).MergedBytes!), "Install did not use reviewed bytes.");
            f.Restore(result.BackupFolder);
            Require(f.Bytes(Fixture.ProfilePath).SequenceEqual(old), "Restore did not recover original APP/ACC profile.");
        });
        test("SAAMAN GNG Full package and repeated update retain first-slot policy", f =>
        {
            var policy = Prepare(f);
            f.ZipPath = Path.Combine(f.DirectoryPath, "ESAA-Full-Package_" + Fixture.NewIdentity + ".zip"); f.SaveZip();
            f.Install(Plan(f, policy));
            var second = Plan(f, policy);
            var profile = second.Files.Single(p => p.RelativePath == Fixture.ProfilePath);
            Require(!profile.WritesFile, "Repeated identical policy changes the profile.");
            f.Install(second);
            Require(f.Read(Fixture.ProfilePath).Contains("Plugins\tPlugin0\t" + policy.DllPath), "Repeated update moved SAAMAN.");
        });
        test("SAAMAN GNG preview and install reject changed or missing DLL", f =>
        {
            var policy = Prepare(f);
            var plan = Plan(f, policy);
            var before = f.Snapshot();
            File.AppendAllText(policy.DllPath, "changed");
            Reject(() => GngUpdateService.PreviewFile(plan, Fixture.ProfilePath));
            Reject(() => f.Install(plan));
            foreach (var item in before) Require(item.Value.SequenceEqual(f.Bytes(item.Key)), "Rejected install mutated a file.");
            File.Delete(policy.DllPath);
            Reject(() => Plan(f, policy));
        });
        test("SAAMAN local relocation unblocks GNG and retains MAESTRO restoration", f =>
        {
            var policy = Prepare(f);
            f.Install(Plan(f, policy));
            var moved = Path.Combine(f.DirectoryPath, "Moved", "SAAMAN.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
            File.Move(policy.DllPath, moved);
            Reject(() => Plan(f, policy));
            var relocation = SaamanProfileService.BuildRelocationPlan(policy, moved, f.Root);
            SaamanProfileService.Apply(relocation, new(Path.Combine(f.DirectoryPath, "Profile-recovery"), () => { }));
            var corrected = policy with { DllPath = moved };
            var plan = Plan(f, corrected);
            var preview = GngUpdateService.PreviewFile(plan, Fixture.ProfilePath).AfterText!;
            Require(preview.Contains("Plugins\tPlugin0\t" + moved), "GNG did not use the locally corrected DLL path.");
            f.Install(plan);
            Require(f.Read(Fixture.ProfilePath).Contains("Plugins\tPlugin0\t" + moved), "GNG reverted the local relocation.");
            f.Install(Plan(f, corrected with { Enabled = false }));
            Require(f.Read(Fixture.ProfilePath).Contains("MAESTRO.dll") && !f.Read(Fixture.ProfilePath).Contains("SAAMAN.dll"),
                "Disabling after relocation and GNG update did not restore MAESTRO.");
        });
        test("SAAMAN GNG explicit target override is confined and canonical profile remains package-managed", f =>
        {
            var policy = Prepare(f);
            f.Entries["ESAA CUSTOM APP.prf"] = f.Entries[Fixture.ProfilePath]; f.SaveZip();
            policy = policy with { ProfilePath = f.PathFor("ESAA CUSTOM APP.prf") };
            var plan = Plan(f, policy);
            Require(GngUpdateService.PreviewFile(plan, "ESAA CUSTOM APP.prf").AfterText!.Contains("Plugin0\t" + policy.DllPath), "Explicit target did not get policy.");
            Require(GngUpdateService.PreviewFile(plan, Fixture.ProfilePath).AfterText!.Contains("MAESTRO.dll"), "Canonical profile was modified despite explicit override.");
            Reject(() => Plan(f, policy with { ProfilePath = Path.Combine(f.DirectoryPath, "outside.prf") }));
        });
        test("SAAMAN GNG collision with configured DLL blocks replacement before install", f =>
        {
            var policy = Prepare(f) with { DllPath = f.PathFor("ESAA/Plugins/SAAMAN.dll") };
            f.Write("ESAA/Plugins/SAAMAN.dll", "synthetic installed DLL");
            f.Entries["ESAA/Plugins/SAAMAN.dll"] = "synthetic incoming package DLL"; f.SaveZip();
            Reject(() => Plan(f, policy));
        });
        test("SAAMAN GNG unmanaged policy keeps manual SAAMAN and package plugin behavior", f =>
        {
            var policy = Prepare(f);
            f.Write(Fixture.ProfilePath, f.Read(Fixture.ProfilePath) + "Plugins\tPlugin8\t" + policy.DllPath + "\r\n");
            var plan = f.Plan();
            var after = GngUpdateService.PreviewFile(plan, Fixture.ProfilePath).AfterText!;
            Require(after.Contains("SAAMAN.dll") && after.Contains("MAESTRO.dll"), "Unmanaged default imposed a replacement policy.");
        });
        test("SAAMAN GNG disable restores original MAESTRO without reverting later profile changes", f =>
        {
            var policy = Prepare(f);
            f.Install(Plan(f, policy));
            f.Write(Fixture.ProfilePath, f.Read(Fixture.ProfilePath).Replace("TEST_OBS", "LATER_OBS"));
            var off = policy with { Enabled = false, DllPath = "" };
            File.Delete(policy.DllPath);
            var plan = Plan(f, off);
            var after = GngUpdateService.PreviewFile(plan, Fixture.ProfilePath).AfterText!;
            Require(!after.Contains("SAAMAN.dll") && after.Contains("MAESTRO.dll"), "Disable did not restore MAESTRO.");
            Require(after.Contains("Custom APP screen") && after.Contains("CustomFlag\tpreserve"), "Original display permissions or other plugin settings lost.");
            f.Install(plan);
            var installed = f.Read(Fixture.ProfilePath);
            Require(installed.Contains("MAESTRO.dll") && installed.Contains("LATER_OBS"), "Restoration rolled back unrelated profile changes.");
            var preceding = installed.IndexOf("VatEFS.dll", StringComparison.OrdinalIgnoreCase);
            Require(preceding >= 0 && preceding < installed.IndexOf("MAESTRO.dll", StringComparison.OrdinalIgnoreCase), "MAESTRO did not return after its original preceding plugin.");
            f.Install(Plan(f, off));
            Require(f.Read(Fixture.ProfilePath) == installed, "Repeated disable changed the restored profile.");
        });
        test("SAAMAN GNG changed restoration metadata rejects preview and install", f =>
        {
            var policy = Prepare(f);
            f.Install(Plan(f, policy));
            var plan = Plan(f, policy with { Enabled = false });
            var dependency = plan.SaamanPolicy!.StateDependency!;
            File.AppendAllText(dependency.Path, " ");
            var before = f.Bytes(Fixture.ProfilePath);
            Reject(() => GngUpdateService.PreviewFile(plan, Fixture.ProfilePath));
            Reject(() => f.Install(plan));
            Require(before.SequenceEqual(f.Bytes(Fixture.ProfilePath)), "Stale metadata mutated the profile.");
        });
        test("SAAMAN GNG Full Package can supply the DLL needed to restore MAESTRO", f =>
        {
            var policy = Prepare(f);
            f.Install(Plan(f, policy));
            File.Delete(f.PathFor("ESAA/Plugins/MAESTRO.dll"));
            f.ZipPath = Path.Combine(f.DirectoryPath, "ESAA-Full-Package_" + Fixture.NewIdentity + ".zip"); f.SaveZip();
            var plan = Plan(f, policy with { Enabled = false });
            f.Install(plan);
            Require(File.Exists(f.PathFor("ESAA/Plugins/MAESTRO.dll")) && f.Read(Fixture.ProfilePath).Contains("MAESTRO.dll"), "Package could not supply restored MAESTRO.");
        });
        test("SAAMAN GNG missing restoration DLL blocks when absent from the package", f =>
        {
            var policy = Prepare(f);
            f.Install(Plan(f, policy));
            File.Delete(f.PathFor("ESAA/Plugins/MAESTRO.dll"));
            f.Entries.Remove("ESAA/Plugins/MAESTRO.dll"); f.SaveZip();
            Reject(() => Plan(f, policy with { Enabled = false }));
        });
        test("SAAMAN GNG backup restore retains the original MAESTRO restoration information", f =>
        {
            var policy = Prepare(f);
            f.Install(Plan(f, policy));
            var disabled = f.Install(Plan(f, policy with { Enabled = false }));
            f.Restore(disabled.BackupFolder);
            Require(f.Read(Fixture.ProfilePath).Contains("SAAMAN.dll"), "Backup did not restore enabled state.");
            f.Install(Plan(f, policy with { Enabled = false }));
            Require(!f.Read(Fixture.ProfilePath).Contains("SAAMAN.dll") && f.Read(Fixture.ProfilePath).Contains("MAESTRO.dll"), "Restoration state was lost after GNG rollback.");
        });
    }
    private static SaamanProfilePolicy Prepare(Fixture f)
    {
        var dll = Path.Combine(f.DirectoryPath, "SAAMAN.dll");
        File.WriteAllText(dll, "synthetic inert SAAMAN DLL");
        const string maestro = "Plugins\tPlugin4\tESAA\\Plugins\\MAESTRO.dll\r\nPlugins\tPlugin4Display0\tCustom APP screen\r\n";
        f.Write(Fixture.ProfilePath, Fixture.LocalProfile + "Plugins\tPlugin0CustomFlag\tpreserve\r\n" + maestro);
        f.Entries[Fixture.ProfilePath] = Fixture.PackageProfile + maestro;
        f.Entries["ESAA/Plugins/MAESTRO.dll"] = "synthetic old package plugin";
        f.Write("ESAA/Plugins/MAESTRO.dll", "synthetic old package plugin");
        f.SaveZip();
        return new(f.PathFor(Fixture.ProfilePath), dll, true) { StateRoot = Path.Combine(f.DirectoryPath, "Saaman-state") };
    }
    private static GngUpdatePlan Plan(Fixture f, SaamanProfilePolicy policy) =>
        GngUpdateService.BuildPlan(f.ZipPath, f.Root, null, f.Context, saamanPolicy: policy);
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException) { return; }
        throw new Exception("Unsafe GNG policy was accepted.");
    }
}
