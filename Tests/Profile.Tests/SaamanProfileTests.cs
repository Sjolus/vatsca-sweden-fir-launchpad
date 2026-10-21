using System.Text;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

internal static class SaamanProfileTests
{
    internal static int Run()
    {
        var count = 0;
        void Test(string name, Action action)
        { action(); count++; Console.WriteLine("PASS SAAMAN profile: " + name); }
        var policy = new SaamanProfilePolicy(@"C:\Synthetic\ESAA APP ACC.prf", @"C:\Synthetic\SAAMAN.dll", true);
        const string source = "; Behåll ÅÄÖ\r\nLastSession\tpassword\tSYNTHETIC-ONLY\r\n" +
            "Plugins\tPlugin0\tESAA\\Plugins\\TopSky.dll\r\nPlugins\tPlugin0Display7\tStandard ES radar screen\r\n" +
            "Plugins\tPlugin0CustomFlag\tkeep me\r\nPlugins\tPlugin4\tESAA\\Plugins\\MAESTRO.dll\r\n" +
            "Plugins\tPlugin4Display3\tCustom APP screen\r\nPlugins\tPlugin9\tESAA\\Plugins\\VatEFS.dll\r\n" +
            "Plugins\tPlugin9Display5\tGround Radar display\r\nPlugins\tCustomSetting\tBehåll\r\n";
        Test("unmanaged default does not inspect a path or change manual configuration", () =>
        {
            Require(SaamanProfileService.Capture(new AppSettings(), "") is null, "Default off was managed.");
        });
        Test("SAAMAN is first and renumbering carries every suffix without changing other order", () =>
        {
            var text = SaamanProfileService.Transform(source, policy);
            Require(text.Contains("Plugins\tPlugin0\t" + policy.DllPath), "SAAMAN is not first.");
            Require(!text.Contains("MAESTRO.dll"), "MAESTRO remained enabled.");
            Require(text.Contains("Plugins\tPlugin0Display0\tCustom APP screen"), "MAESTRO display name was lost.");
            Require(text.Contains("Plugins\tPlugin1\tESAA\\Plugins\\TopSky.dll") &&
                text.Contains("Plugins\tPlugin1Display7\tStandard ES radar screen") &&
                text.Contains("Plugins\tPlugin1CustomFlag\tkeep me"), "TopSky suffixes detached or changed.");
            Require(text.Contains("Plugins\tPlugin2Display5\tGround Radar display") &&
                text.Contains("Plugins\tCustomSetting\tBehåll") && text.Contains("SYNTHETIC-ONLY"), "Unrelated contents changed.");
            Require(SaamanProfileService.Transform(text, policy) == text, "Repeated application is not idempotent.");
        });
        Test("without a recorded replacement disable does not invent a MAESTRO path", () =>
        {
            var on = SaamanProfileService.Transform(source, policy);
            var off = SaamanProfileService.Transform(on, policy with { Enabled = false });
            Require(!off.Contains("SAAMAN.dll") && !off.Contains("MAESTRO.dll"), "Disable restored the old plugin.");
            Require(off.Contains("Plugin0CustomFlag\tkeep me"), "Disable lost a surviving suffix.");
            Require(SaamanProfileService.Transform(source, policy with { Enabled = false }) == source, "Disabled policy touched a profile without SAAMAN.");
        });
        Test("existing SAAMAN permissions reconcile by name and duplicates consolidate", () =>
        {
            var text = source + "Plugins\tPlugin12\tC:\\Old\\SAAMAN.dll\r\nPlugins\tPlugin12Display0\tOther screen\r\n" +
                "Plugins\tPlugin15\tC:\\Other\\SAAMAN.dll\r\nPlugins\tPlugin15Display8\tOther screen\r\n";
            var result = SaamanProfileService.Transform(text, policy);
            Require(result.Split("SAAMAN.dll").Length == 2, "Duplicate SAAMAN entries survived.");
            Require(result.Contains("Plugin0Display0\tOther screen") && result.Contains("Plugin0Display1\tCustom APP screen"), "Screen names lost or duplicated.");
        });
        Test("orphan duplicate and conflicting plugin groups are rejected", () =>
        {
            foreach (var bad in new[] {
                source + "Plugins\tPlugin2Display0\tOrphan\r\n",
                source + "Plugins\tPlugin0\tOther.dll\r\n",
                source + "Plugins\tPlugin0Display7\tOther screen\r\n",
                source + "Plugins\tPlugin99999999999999999999\tBad.dll\r\n",
                source + "Plugins\tPlugin12\tSAAMAN.dll\r\nPlugins\tPlugin12Flag\t1\r\nPlugins\tPlugin13\tSAAMAN.dll\r\nPlugins\tPlugin13Flag\t2\r\n" })
                Reject(() => SaamanProfileService.Transform(bad, policy));
        });
        Test("missing configured DLL profile and outside-root override block review", () =>
        {
            using var f = new SaamanFixture(source);
            Reject(() => SaamanProfileService.BuildPlan(f.Policy with { ProfilePath = Path.Combine(f.Base, "Outside.prf") }, f.Data));
            Reject(() => SaamanProfileService.BuildPlan(f.Policy with { ProfilePath = Path.Combine(f.Data, "Missing.prf") }, f.Data));
            File.Delete(f.Dll);
            Reject(() => SaamanProfileService.BuildPlan(f.Policy, f.Data));
        });
        Test("apply backs up exact bytes and changes only selected profile with a synthetic process guard", () =>
        {
            using var f = new SaamanFixture(source);
            var original = File.ReadAllBytes(f.Profile);
            var sibling = Path.Combine(f.Data, "ESAA TWR.prf"); File.WriteAllBytes(sibling, original);
            var plan = SaamanProfileService.BuildPlan(f.Policy, f.Data);
            var preview = SaamanProfileService.GeneratePreview(plan);
            Require(!preview.Contains("SYNTHETIC-ONLY"), "Preview exposed a credential field.");
            var guardCount = 0;
            var backup = SaamanProfileService.Apply(plan, new(f.Recovery, () => guardCount++));
            Require(guardCount >= 2, "Native boundary was not guarded.");
            Require(backup is not null && File.ReadAllBytes(Path.Combine(backup, Path.GetFileName(f.Profile))).SequenceEqual(original), "Original recovery bytes differ.");
            Require(File.ReadAllBytes(sibling).SequenceEqual(original), "Other profile changed.");
            Require(File.ReadAllBytes(f.Profile).SequenceEqual(plan.AfterBytes), "Applied bytes differ from preview.");
            var second = SaamanProfileService.BuildPlan(f.Policy, f.Data);
            Require(!second.HasChanges && SaamanProfileService.Apply(second, new(f.Recovery, () => {})) is null, "No-op created a backup.");
        });
        Test("stale profile DLL and open EuroScope reject before mutation", () =>
        {
            using var f = new SaamanFixture(source);
            var plan = SaamanProfileService.BuildPlan(f.Policy, f.Data);
            var before = File.ReadAllBytes(f.Profile);
            Reject(() => SaamanProfileService.Apply(plan, new(f.Recovery, () => throw new InvalidOperationException("Synthetic running EuroScope"))));
            File.AppendAllText(f.Dll, "changed");
            Reject(() => SaamanProfileService.Apply(plan, new(f.Recovery, () => {})));
            Require(before.SequenceEqual(File.ReadAllBytes(f.Profile)), "Failure mutated profile.");
            File.WriteAllText(f.Dll, "synthetic inert DLL");
            File.AppendAllText(f.Profile, "; changed");
            Reject(() => SaamanProfileService.Apply(plan, new(f.Recovery, () => {})));
            Require(!Directory.Exists(f.Recovery), "Rejected apply created recovery storage.");
        });
        Test("UTF8 BOM and newline style survive the reviewed transformation", () =>
        {
            using var f = new SaamanFixture(source);
            File.WriteAllText(f.Profile, source.Replace("\r\n", "\n"), new UTF8Encoding(true));
            var plan = SaamanProfileService.BuildPlan(f.Policy, f.Data);
            Require(plan.AfterBytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), "BOM lost.");
            Require(!Encoding.UTF8.GetString(plan.AfterBytes).Contains("\r\n"), "Newline style changed.");
        });
        Test("missing target permits explicit disable only and rechecks before saving", () =>
        {
            using var f = new SaamanFixture(source);
            File.Delete(f.Profile);
            var plan = SaamanProfileService.BuildPlan(f.Policy with { Enabled = false }, f.Data);
            Require(plan.ProfileMissing && !plan.HasChanges && plan.Summary.Contains("remain unchanged"), "Missing target was described as edited.");
            Require(SaamanProfileService.Apply(plan, new(f.Recovery, () => {})) is null, "Missing target created a recovery copy.");
            File.WriteAllText(f.Profile, source);
            Reject(() => SaamanProfileService.Apply(plan, new(f.Recovery, () => {})));
            Require(File.ReadAllText(f.Profile) == source, "Newly appeared target was edited.");
        });
        Test("restart and disable restore original MAESTRO order and every display suffix", () =>
        {
            using var f = new SaamanFixture(source);
            f.Enable();
            var off = SaamanProfileService.BuildPlan(f.Policy with { Enabled = false }, f.Data);
            Require(off.Summary.Contains("restore the original MAESTRO"), "Restoration was omitted from review.");
            SaamanProfileService.Apply(off, new(f.Recovery, () => {}));
            var restored = f.Read();
            Require(!restored.Contains("SAAMAN.dll") && restored.Contains("Plugin1\tESAA\\Plugins\\MAESTRO.dll") &&
                restored.Contains("Plugin1Display3\tCustom APP screen"), "Original MAESTRO group or order was not restored.");
            Require(restored.Contains("Plugin0CustomFlag\tkeep me") && restored.Contains("Plugin2Display5\tGround Radar display"),
                "Restoration changed other plugins.");
            var repeat = SaamanProfileService.BuildPlan(f.Policy with { Enabled = false }, f.Data);
            Require(!repeat.HasChanges, "Repeated disable changed the restored profile.");
            SaamanProfileService.Apply(repeat, new(f.Recovery, () => {}));
            Require(f.Read() == restored, "Repeated disable was not idempotent.");
        });
        Test("restore keeps later identity plugin and preference edits", () =>
        {
            using var f = new SaamanFixture(source);
            f.Enable();
            var edited = f.Read().Replace("SYNTHETIC-ONLY", "LATER-SYNTHETIC-ONLY").Replace("keep me", "later plugin flag") +
                "Plugins\tPlugin7\tNewPlugin.dll\r\nPlugins\tPlugin7Custom\tnew value\r\nOther\tSetting\tÅäö changed\r\n";
            f.Write(edited);
            SaamanProfileService.Apply(SaamanProfileService.BuildPlan(f.Policy with { Enabled = false }, f.Data), new(f.Recovery, () => {}));
            var restored = f.Read();
            Require(restored.Contains("LATER-SYNTHETIC-ONLY") && restored.Contains("later plugin flag") &&
                restored.Contains("NewPlugin.dll") && restored.Contains("new value") && restored.Contains("Åäö changed"),
                "Restoring MAESTRO rolled back later unrelated edits.");
        });
        Test("unchanged enable retains durable snapshot without rewriting it", () =>
        {
            using var f = new SaamanFixture(source);
            var first = f.Enable();
            var statePath = first.Policy.StateDependency!.Path;
            var baseline = File.ReadAllBytes(statePath);
            var timestamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(statePath, timestamp);
            var repeat = SaamanProfileService.BuildPlan(f.Policy, f.Data);
            SaamanProfileService.Apply(repeat, new(f.Recovery, () => {}));
            Require(File.GetLastWriteTimeUtc(statePath) == timestamp && File.ReadAllBytes(statePath).SequenceEqual(baseline),
                "Repeated enable rewrote its baseline.");
            var metadata = Encoding.UTF8.GetString(baseline);
            Require(!metadata.Contains("SYNTHETIC-ONLY") && !metadata.Contains("LastSession") && !metadata.Contains("keep me"),
                "The plugin-only snapshot copied unrelated fields.");
        });
        Test("moving the enabled DLL updates one profile and retains the original MAESTRO baseline", () =>
        {
            using var f = new SaamanFixture(source);
            var first = f.Enable();
            var baseline = File.ReadAllBytes(first.Policy.StateDependency!.Path);
            var relocated = Path.Combine(f.Base, "Moved", "SAAMAN.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(relocated)!);
            File.Move(f.Dll, relocated);
            var before = File.ReadAllBytes(f.Profile);
            var plan = SaamanProfileService.BuildRelocationPlan(f.Policy, relocated, f.Data);
            Require(plan.HasChanges && plan.Policy.Enabled, "Relocation disabled the plugin or omitted its change.");
            var recovery = SaamanProfileService.Apply(plan, new(f.Recovery, () => {}));
            Require(f.Read().Contains("Plugin0\t" + relocated) && !f.Read().Contains("MAESTRO.dll"), "The enabled reference was not relocated.");
            Require(File.ReadAllBytes(first.Policy.StateDependency.Path).SequenceEqual(baseline), "Relocation replaced the original MAESTRO baseline.");
            Require(recovery is not null && File.ReadAllBytes(Path.Combine(recovery, Path.GetFileName(f.Profile))).SequenceEqual(before),
                "Relocation did not preserve the exact previous profile.");
            var off = SaamanProfileService.BuildPlan(f.Policy with { DllPath = relocated, Enabled = false }, f.Data);
            SaamanProfileService.Apply(off, new(f.Recovery, () => {}));
            Require(f.Read().Contains("Plugin1\tESAA\\Plugins\\MAESTRO.dll") &&
                f.Read().Contains("Plugin1Display3\tCustom APP screen") && !f.Read().Contains("SAAMAN.dll"),
                "Disabling after relocation did not restore the original MAESTRO group.");
        });
        Test("relocation rejects missing baseline and profiles that no longer use the saved DLL", () =>
        {
            using var f = new SaamanFixture(source);
            var first = f.Enable();
            var relocated = Path.Combine(f.Base, "Moved", "SAAMAN.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(relocated)!);
            File.Copy(f.Dll, relocated);
            var original = f.Read();
            f.Write(original.Replace(f.Dll, relocated));
            Reject(() => SaamanProfileService.BuildRelocationPlan(f.Policy, relocated, f.Data));
            f.Write(original);
            File.Delete(first.Policy.StateDependency!.Path);
            Reject(() => SaamanProfileService.BuildRelocationPlan(f.Policy, relocated, f.Data));
            Require(f.Read() == original, "Rejected relocation changed the profile.");
        });
        Test("relocation rechecks new DLL profile and MAESTRO state before writing", () =>
        {
            using var f = new SaamanFixture(source);
            f.Enable();
            var relocated = Path.Combine(f.Base, "Moved", "SAAMAN.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(relocated)!);
            File.Copy(f.Dll, relocated);
            var before = File.ReadAllBytes(f.Profile);
            var plan = SaamanProfileService.BuildRelocationPlan(f.Policy, relocated, f.Data);
            File.AppendAllText(relocated, "changed after review");
            Reject(() => SaamanProfileService.Apply(plan, new(f.Recovery, () => {})));
            Require(File.ReadAllBytes(f.Profile).SequenceEqual(before), "Changed DLL was applied.");
            File.Copy(f.Dll, relocated, overwrite: true);
            plan = SaamanProfileService.BuildRelocationPlan(f.Policy, relocated, f.Data);
            Reject(() => SaamanProfileService.Apply(plan, new(f.Recovery, () => throw new InvalidOperationException("Synthetic running EuroScope"))));
            var state = plan.Policy.StateDependency!;
            File.AppendAllText(state.Path, " ");
            Reject(() => SaamanProfileService.Apply(plan, new(f.Recovery, () => {})));
            Require(File.ReadAllBytes(f.Profile).SequenceEqual(before), "Changed restoration state was applied.");
            File.WriteAllBytes(state.Path, plan.Policy.StateAfterBytes!);
            plan = SaamanProfileService.BuildRelocationPlan(f.Policy, relocated, f.Data);
            f.Write(f.Read() + "; later edit\r\n");
            var edited = File.ReadAllBytes(f.Profile);
            Reject(() => SaamanProfileService.Apply(plan, new(f.Recovery, () => {})));
            Require(File.ReadAllBytes(f.Profile).SequenceEqual(edited), "A stale relocation replaced later edits.");
        });
        Test("a new enable after disable captures the user's changed MAESTRO configuration", () =>
        {
            using var f = new SaamanFixture(source);
            f.Enable();
            SaamanProfileService.Apply(SaamanProfileService.BuildPlan(f.Policy with { Enabled = false }, f.Data), new(f.Recovery, () => {}));
            var custom = Path.Combine(f.Data, "Custom", "MAESTRO.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(custom)!);
            File.WriteAllText(custom, "new synthetic MAESTRO");
            f.Write(f.Read().Replace(@"ESAA\Plugins\MAESTRO.dll", @"Custom\MAESTRO.dll").Replace("Custom APP screen", "Later screen"));
            f.Enable();
            var off = SaamanProfileService.BuildPlan(f.Policy with { Enabled = false }, f.Data);
            SaamanProfileService.Apply(off, new(f.Recovery, () => {}));
            Require(f.Read().Contains(@"Custom\MAESTRO.dll") && f.Read().Contains("Plugin1Display3\tLater screen"),
                "Re-enable used an obsolete baseline.");
        });
        Test("GNG recovery of an enabled profile reuses the retained inactive snapshot", () =>
        {
            using var f = new SaamanFixture(source);
            f.Enable(); var enabled = File.ReadAllBytes(f.Profile);
            SaamanProfileService.Apply(SaamanProfileService.BuildPlan(f.Policy with { Enabled = false }, f.Data), new(f.Recovery, () => {}));
            File.WriteAllBytes(f.Profile, enabled);
            var off = SaamanProfileService.BuildPlan(f.Policy with { Enabled = false }, f.Data);
            SaamanProfileService.Apply(off, new(f.Recovery, () => {}));
            Require(f.Read().Contains("Plugin1Display3\tCustom APP screen"), "Recovered enabled profile lost its MAESTRO baseline.");
        });
        Test("inactive GNG merge keeps the current local MAESTRO configuration", () =>
        {
            using var f = new SaamanFixture(source);
            f.Enable();
            SaamanProfileService.Apply(SaamanProfileService.BuildPlan(f.Policy with { Enabled = false }, f.Data), new(f.Recovery, () => {}));
            var local = f.Read().Replace("Custom APP screen", "Later local screen");
            var prepared = SaamanProfileService.PreparePolicy(f.Policy with { Enabled = false }, local, f.Data);
            var merged = SaamanProfileService.TransformMerged(local, source, prepared);
            Require(merged.Contains("Plugin1Display3\tLater local screen") && !merged.Contains("Custom APP screen"),
                "A later GNG merge restored stale permissions.");
        });
        Test("restoration state changes after review block all profile writes", () =>
        {
            using var f = new SaamanFixture(source);
            var first = f.Enable();
            var off = SaamanProfileService.BuildPlan(f.Policy with { Enabled = false }, f.Data);
            var before = File.ReadAllBytes(f.Profile);
            File.AppendAllText(first.Policy.StateDependency!.Path, " ");
            Reject(() => SaamanProfileService.Apply(off, new(f.Recovery, () => {})));
            Require(File.ReadAllBytes(f.Profile).SequenceEqual(before), "Stale metadata changed the profile.");
        });
        Test("missing or changed original MAESTRO DLL blocks restoration", () =>
        {
            using var f = new SaamanFixture(source);
            f.Enable();
            var off = SaamanProfileService.BuildPlan(f.Policy with { Enabled = false }, f.Data);
            File.AppendAllText(f.Maestro, "new version");
            Reject(() => SaamanProfileService.Apply(off, new(f.Recovery, () => {})));
            File.Delete(f.Maestro);
            Reject(() => SaamanProfileService.BuildPlan(f.Policy with { Enabled = false }, f.Data));
            Require(f.Read().Contains("SAAMAN.dll"), "Failed restoration removed SAAMAN.");
        });
        Test("disable keeps a verified prior sidecar and protections survive reset flags", () =>
        {
            using var f = new SaamanFixture(source);
            var enabled = f.Enable();
            var statePath = enabled.Policy.StateDependency!.Path;
            var active = File.ReadAllBytes(statePath);
            SaamanProfileService.Apply(SaamanProfileService.BuildPlan(f.Policy with { Enabled = false }, f.Data), new(f.Recovery, () => {}));
            Require(File.ReadAllBytes(statePath + ".previous").SequenceEqual(active), "The prior baseline was not retained.");
            File.Delete(f.Maestro);
            var protectedPaths = SaamanProfileService.GetProtectedPaths(new AppSettings { EuroscopeDataPath = f.Data }, f.State);
            Require(protectedPaths.Contains(f.Maestro) && protectedPaths.Contains(statePath) && protectedPaths.Contains(statePath + ".previous"),
                "Restorable state or missing original DLL stopped being protected after settings reset.");
        });
        Test("no original MAESTRO records the limitation and restores no fabricated path", () =>
        {
            using var f = new SaamanFixture("Plugins\tPlugin0\tTopSky.dll\r\n");
            f.Enable();
            var off = SaamanProfileService.BuildPlan(f.Policy with { Enabled = false }, f.Data);
            Require(off.Summary.Contains("no MAESTRO path will be invented"), "The no-original limitation was not reviewed.");
            SaamanProfileService.Apply(off, new(f.Recovery, () => {}));
            Require(!f.Read().Contains("MAESTRO.dll") && !f.Read().Contains("SAAMAN.dll"), "Disable invented a plugin.");
        });
        Test("three original MAESTRO groups preserve distinct suffixes and order", () =>
        {
            using var f = new SaamanFixture("Plugins\tPlugin0\tTopSky.dll\r\n" +
                "Plugins\tPlugin1\tESAA\\Plugins\\MAESTRO.dll\r\nPlugins\tPlugin1Display3\tFirst\r\n" +
                "Plugins\tPlugin2\tESAA\\Plugins\\MAESTRO.dll\r\nPlugins\tPlugin2Display7\tSecond\r\n" +
                "Plugins\tPlugin3\tESAA\\Plugins\\MAESTRO.dll\r\nPlugins\tPlugin3Display9\tThird\r\n");
            f.Enable();
            SaamanProfileService.Apply(SaamanProfileService.BuildPlan(f.Policy with { Enabled = false }, f.Data), new(f.Recovery, () => {}));
            var text = f.Read();
            Require(text.Contains("Plugin1Display3\tFirst") && text.Contains("Plugin2Display7\tSecond") && text.Contains("Plugin3Display9\tThird"),
                "Repeated MAESTRO paths reversed original group order.");
        });
        Test("invalid state and state stored inside GNG fail closed", () =>
        {
            using var f = new SaamanFixture(source);
            Reject(() => SaamanProfileService.BuildPlan(f.Policy with { StateRoot = Path.Combine(f.Data, "State") }, f.Data));
            var first = f.Enable();
            File.WriteAllText(first.Policy.StateDependency!.Path, "{broken");
            Reject(() => SaamanProfileService.BuildPlan(f.Policy with { Enabled = false }, f.Data));
            Require(f.Read().Contains("SAAMAN.dll"), "Invalid state changed the profile.");
        });
        Test("missing saved state cannot silently discard an enabled replacement", () =>
        {
            using var f = new SaamanFixture(source);
            var enabled = f.Enable();
            File.Delete(enabled.Policy.StateDependency!.Path);
            var before = File.ReadAllBytes(f.Profile);
            Reject(() => SaamanProfileService.BuildPlan(f.Policy with { Enabled = false }, f.Data));
            Require(File.ReadAllBytes(f.Profile).SequenceEqual(before), "Missing state silently removed the plugin.");
        });
        Test("metadata completion failure restores the enabled profile and original baseline", () =>
        {
            using var f = new SaamanFixture(source);
            var enabled = f.Enable();
            var statePath = enabled.Policy.StateDependency!.Path;
            var stateBefore = File.ReadAllBytes(statePath);
            var profileBefore = File.ReadAllBytes(f.Profile);
            var off = SaamanProfileService.BuildPlan(f.Policy with { Enabled = false }, f.Data);
            Directory.CreateDirectory(statePath + ".previous");
            Reject(() => SaamanProfileService.Apply(off, new(f.Recovery, () => {})));
            Require(File.ReadAllBytes(f.Profile).SequenceEqual(profileBefore) && File.ReadAllBytes(statePath).SequenceEqual(stateBefore),
                "Failed restoration metadata left the profile or baseline changed.");
        });
        Test("protection discovers custom profiles after preferences reset and excludes other roots", () =>
        {
            using var f = new SaamanFixture(source);
            var customProfile = Path.Combine(f.Data, "Custom APP ACC.prf");
            File.Move(f.Profile, customProfile);
            var customPlan = SaamanProfileService.BuildPlan(f.Policy with { ProfilePath = customProfile }, f.Data);
            SaamanProfileService.Apply(customPlan, new(f.Recovery, () => {}));
            var otherData = Path.Combine(f.Base, "OtherGng");
            Directory.CreateDirectory(otherData);
            var otherPolicy = SaamanProfileService.PreparePolicy(
                f.Policy with { ProfilePath = Path.Combine(otherData, "Other.prf") }, source, otherData);
            using (var state = SaamanProfileService.PersistRestoreState(otherPolicy)) state.Complete();
            var paths = SaamanProfileService.GetProtectedPaths(new AppSettings { EuroscopeDataPath = f.Data }, f.State);
            Require(paths.Contains(f.Maestro) && paths.Contains(customPlan.Policy.StateDependency!.Path),
                "Reset profile preferences lost a custom profile's original MAESTRO protection.");
            Require(!paths.Contains(otherPolicy.StateDependency!.Path) &&
                !paths.Contains(Path.Combine(otherData, "ESAA", "Plugins", "MAESTRO.dll")),
                "Protection included a different configured GNG root.");
        });
        Test("a retained previous snapshot stays protective when its main sidecar is missing", () =>
        {
            using var f = new SaamanFixture(source);
            var enabled = f.Enable();
            SaamanProfileService.Apply(SaamanProfileService.BuildPlan(f.Policy with { Enabled = false }, f.Data), new(f.Recovery, () => {}));
            var statePath = enabled.Policy.StateDependency!.Path;
            File.Delete(statePath);
            var paths = SaamanProfileService.GetProtectedPaths(new AppSettings { EuroscopeDataPath = f.Data }, f.State);
            Require(paths.Contains(f.Maestro) && paths.Contains(statePath + ".previous"), "A retained snapshot stopped protecting original files.");
        });
        Test("protection enumeration fails closed at its fixed entry limit", () =>
        {
            using var f = new SaamanFixture(source);
            Directory.CreateDirectory(f.State);
            for (var i = 0; i <= SaamanProfileService.MaximumProtectedStateEntries; i++)
                File.WriteAllText(Path.Combine(f.State, "extra-" + i + ".txt"), "");
            Reject(() => SaamanProfileService.GetProtectedPaths(new AppSettings { EuroscopeDataPath = f.Data }, f.State));
        });
        Test("full apply does not save SAAMAN choice after a failed plugin step", () =>
        {
            using var f = new SaamanFixture(source);
            var settings = new AppSettings { PatchSaaman = true, SaamanDllPath = f.Dll };
            var saved = false;
            var original = File.ReadAllBytes(f.Profile);
            File.Delete(f.Dll);
            Reject(() => ProfileService.Apply(settings, f.Data, () => saved = true, policy =>
                SaamanProfileService.Apply(SaamanProfileService.BuildPlan(policy with { StateRoot = f.State }, f.Data), new(f.Recovery, () => {}))));
            Require(!saved && File.ReadAllBytes(f.Profile).SequenceEqual(original), "A failed plugin step saved the requested choice.");
        });
        Test("full apply saves completed SAAMAN choice before a later identity-write failure", () =>
        {
            using var f = new SaamanFixture(source);
            var settings = new AppSettings { PatchSaaman = true, SaamanDllPath = f.Dll };
            var saved = false;
            var blocked = Path.Combine(f.Data, "ESZZ identity-failure.prf");
            File.WriteAllText(blocked, "; synthetic identity target");
            using var lockTarget = new FileStream(blocked, FileMode.Open, FileAccess.Read, FileShare.None);
            Reject(() => ProfileService.Apply(settings, f.Data, () =>
            {
                Require(f.Read().Contains("SAAMAN.dll") && !f.Read().Contains("MAESTRO.dll"), "Choice was saved before the plugin result.");
                saved = true;
            }, policy => SaamanProfileService.Apply(
                SaamanProfileService.BuildPlan(policy with { StateRoot = f.State }, f.Data), new(f.Recovery, () => {}))));
            Require(saved && f.Read().Contains("SAAMAN.dll"), "A later identity failure lost the successful plugin choice.");
        });
        return count;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or ArgumentException) { return; }
        throw new Exception("Unsafe change was accepted.");
    }
    private sealed class SaamanFixture : IDisposable
    {
        internal string Base { get; } = Path.Combine(Path.GetTempPath(), "Launchpad-SaamanProfile-tests-" + Guid.NewGuid().ToString("N"));
        internal string Data => Path.Combine(Base, "Gng");
        internal string Profile => Path.Combine(Data, SaamanProfileService.DefaultProfileName);
        internal string Dll => Path.Combine(Base, "SAAMAN.dll");
        internal string Recovery => Path.Combine(Base, "Recovery");
        internal string State => Path.Combine(Base, "State");
        internal string Maestro => Path.Combine(Data, "ESAA", "Plugins", "MAESTRO.dll");
        internal SaamanProfilePolicy Policy => new(Profile, Dll, true) { StateRoot = State };
        internal SaamanProfileChange Enable()
        {
            var plan = SaamanProfileService.BuildPlan(Policy, Data);
            SaamanProfileService.Apply(plan, new(Recovery, () => {}));
            return plan;
        }
        internal string Read() => File.ReadAllText(Profile, Encoding.GetEncoding(1252));
        internal void Write(string text) => File.WriteAllText(Profile, text, Encoding.GetEncoding(1252));
        internal SaamanFixture(string text)
        {
            Directory.CreateDirectory(Data);
            File.WriteAllText(Profile, text, Encoding.GetEncoding(1252));
            File.WriteAllText(Dll, "synthetic inert DLL");
            Directory.CreateDirectory(Path.GetDirectoryName(Maestro)!);
            File.WriteAllText(Maestro, "synthetic inert MAESTRO DLL");
        }
        public void Dispose()
        {
            var path = Path.GetFullPath(Base);
            if (Path.GetDirectoryName(path) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) ||
                !Path.GetFileName(path).StartsWith("Launchpad-SaamanProfile-tests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected synthetic test root.");
            Directory.Delete(path, recursive: true);
        }
    }
}
