using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

internal static class VatEfsProfileTests
{
    internal static int Run()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var tests = new (string, Action)[]
        {
            ("toggle changes only plugin rows and makes exact recovery copies", () =>
            {
                using var f = new VatFixture();
                var before = f.Profiles.ToDictionary(path => path, File.ReadAllBytes);
                CredentialManagerService.Reset(); Logger.Messages.Clear();
                var result = VatEfsProfileService.Apply(f.Settings, true, f.Options);
                Assert(result.Enabled && result.ProfilesChanged == 2 && result.ProfilesChecked == 2 && result.RecoveryFolder is not null);
                var security = new DirectoryInfo(result.RecoveryFolder!).GetAccessControl();
                Assert(security.AreAccessRulesProtected);
                using var user = WindowsIdentity.GetCurrent();
                var allowed = new[] { user.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) };
                Assert(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
                    .All(rule => rule.AccessControlType == AccessControlType.Allow && allowed.Contains(rule.IdentityReference)));
                string restore = File.ReadAllText(Path.Combine(result.RecoveryFolder!, "RESTORE.txt"));
                Assert(restore.Contains("Close EuroScope") && !restore.Contains("SYNTHETIC-UNCHANGED"));
                foreach (var path in f.Profiles)
                {
                    string text = File.ReadAllText(path, VatFixture.Encoding);
                    Assert(text.Contains(VatFixture.Identity) && text.Contains(VatFixture.OtherPlugin));
                    Assert(text.Contains("VatEFS.dll") && text.Contains("Åsa Östergård"));
                    Assert(before[path].SequenceEqual(File.ReadAllBytes(Path.Combine(result.RecoveryFolder!, Path.GetFileName(path)))));
                }
                Assert(File.ReadAllText(f.Unrelated) == "UNRELATED");
                Assert(CredentialManagerService.Reads == 0 && Logger.Messages.Count == 0);
                Assert(!f.Settings.PatchVatEfs && f.Settings.VatsimCid == "not-to-be-applied");
                var first = f.Profiles.ToDictionary(path => path, File.ReadAllBytes);
                result = VatEfsProfileService.Apply(f.Settings, true, f.Options);
                Assert(result.ProfilesChanged == 0 && result.RecoveryFolder is null);
                foreach (var path in f.Profiles) Assert(first[path].SequenceEqual(File.ReadAllBytes(path)));
            }),
            ("disable works after the DLL is removed and preserves other plugin suffixes", () =>
            {
                using var f = new VatFixture();
                string text = VatFixture.Identity + "\r\nPlugins\tPlugin0\tTopSky.dll\r\nPlugins\tPlugin1\t" + f.Dll +
                    "\r\nPlugins\tPlugin1Display0\tStandard ES radar screen\r\nPlugins\tPlugin2\tCDM.dll\r\nPlugins\tPlugin2CustomFlag\tkeep\r\nPlugins\tPlugin2Display9\tÖvrig skärm";
                foreach (var path in f.Profiles) File.WriteAllText(path, text, VatFixture.Encoding);
                File.Delete(f.Dll); f.Settings.VatEfsPath = "";
                var result = VatEfsProfileService.Apply(f.Settings, false, f.Options);
                Assert(!result.Enabled && result.ProfilesChanged == 2);
                string changed = File.ReadAllText(f.Profiles[0], VatFixture.Encoding);
                Assert(!changed.Contains("VatEFS.dll") && changed.Contains("Plugins\tPlugin1\tCDM.dll") &&
                    changed.Contains("Plugins\tPlugin1CustomFlag\tkeep") && changed.Contains("Plugins\tPlugin1Display9\tÖvrig skärm") &&
                    changed.Contains(VatFixture.Identity) && !changed.EndsWith('\n'));
            }),
            ("existing display names are retained even when their numeric positions differ", () =>
            {
                string text = "Plugins\tPlugin0\tTopSky.dll\r\nPlugins\tPlugin0Display0\tGround Radar display\r\nPlugins\tPlugin0Display1\tStandard ES radar screen\r\n" +
                    "Plugins\tPlugin1\tC:\\Synthetic\\VatEFS.dll\r\nPlugins\tPlugin1Display0\tStandard ES radar screen\r\n";
                var output = ProfileService.TransformVatEfs(text, @"C:\Synthetic\VatEFS.dll", true);
                Assert(output.Contains("Plugin1Display0\tStandard ES radar screen") && output.Contains("Plugin1Display1\tGround Radar display"));
                Assert(output.Split('\n').Count(line => line.StartsWith("Plugins\tPlugin1Display")) == 2);
                Assert(ProfileService.TransformVatEfs(output, @"C:\Synthetic\VatEFS.dll", true) == output);
            }),
            ("legacy preview shows the same display-name changes as the isolated toggle", () =>
            {
                using var f = new VatFixture(); f.Settings.PatchVatEfs = true;
                string source = VatFixture.Identity + "\r\nPlugins\tPlugin0\tTopSky.dll\r\nPlugins\tPlugin0Display0\tGround Radar display\r\n" +
                    "Plugins\tPlugin0Display1\tStandard ES radar screen\r\nPlugins\tPlugin1\t" + f.Dll +
                    "\r\nPlugins\tPlugin1Display0\tStandard ES radar screen\r\n";
                foreach (var path in f.Profiles) File.WriteAllText(path, source, VatFixture.Encoding);
                string preview = ProfileService.GeneratePreview(f.Settings, "SYNTHETIC-PREVIEW", "", f.Data);
                Assert(preview.Contains("+ Plugins\tPlugin1Display1\tGround Radar display"));
                Assert(!preview.Contains("Plugins\tPlugin1Display0\tGround Radar display") && !preview.Contains("SYNTHETIC-PREVIEW"));
                VatEfsProfileService.Apply(f.Settings, true, f.Options);
                string changed = File.ReadAllText(f.Profiles[0], VatFixture.Encoding);
                Assert(changed.Contains("Plugins\tPlugin1Display1\tGround Radar display") &&
                    changed.Contains("Plugins\tPlugin1Display0\tStandard ES radar screen"));
            }),
            ("identity-only preview and apply ignore missing enabled plugins and preserve every plugin row", () =>
            {
                using var f = new VatFixture();
                f.Settings.PatchSaaman = f.Settings.SaamanProfileManaged = f.Settings.PatchVatEfs = true;
                f.Settings.SaamanDllPath = Path.Combine(f.Root, "missing-SAAMAN.dll");
                f.Settings.SaamanProfilePath = f.Profiles[0];
                f.Settings.VatsimName = "Cecilia Öst"; f.Settings.VatsimCid = "999999"; f.Settings.VatsimRating = 2;
                f.Settings.ObsCallsign = "ES_TEST";
                foreach (var path in f.Profiles)
                    File.AppendAllText(path, "\r\nPlugins\tPlugin1\t" + f.Dll + "\r\nPlugins\tPlugin1Display7\tÖvrig skärm", VatFixture.Encoding);
                File.Delete(f.Dll);
                var before = f.Profiles.ToDictionary(path => path, path => File.ReadAllLines(path, VatFixture.Encoding)
                    .Where(line => line.StartsWith("Plugins\t", StringComparison.Ordinal)).ToArray());
                CredentialManagerService.Reset(); Logger.Messages.Clear();
                string preview = ProfileService.GeneratePreview(f.Settings, "SYNTHETIC-NEW-PASSWORD", "SYNTHETIC-HOPPIE", f.Data, includePluginChoices: false);
                Assert(preview.Contains("Cecilia Öst") && !preview.Contains("SAAMAN plugin step") && !preview.Contains("Plugins\tPlugin"));
                Assert(!preview.Contains("SYNTHETIC-NEW-PASSWORD") && !preview.Contains("SYNTHETIC-HOPPIE") && CredentialManagerService.Reads == 0);
                CredentialManagerService.Values[CredentialManagerService.TargetVatsim] = "SYNTHETIC-NEW-PASSWORD";
                CredentialManagerService.Values[CredentialManagerService.TargetHoppie] = "SYNTHETIC-HOPPIE";
                Assert(ProfileService.Apply(f.Settings, f.Data, includePluginChoices: false) is null);
                foreach (var path in f.Profiles)
                {
                    string[] lines = File.ReadAllLines(path, VatFixture.Encoding);
                    Assert(before[path].SequenceEqual(lines.Where(line => line.StartsWith("Plugins\t", StringComparison.Ordinal))));
                    Assert(lines.Contains("LastSession\trealname\tCecilia Öst") && lines.Contains("LastSession\tcertificate\t999999") &&
                        lines.Contains("LastSession\tpassword\tSYNTHETIC-NEW-PASSWORD"));
                }
                Assert(File.ReadAllText(Path.Combine(f.Data, "ESAA", "Plugins", "TopSkyCPDLChoppieCode.txt")) == "SYNTHETIC-HOPPIE");
                Assert(File.ReadAllText(Path.Combine(f.Data, "ESAA", "Settings", "LoginProfiles.txt")).Contains("ES_TEST_OBS"));
                Assert(f.Settings.PatchSaaman && f.Settings.PatchVatEfs);
                var applied = f.Profiles.ToDictionary(path => path, File.ReadAllBytes);
                Reject(() => ProfileService.GeneratePreview(f.Settings, "synthetic", "", f.Data));
                Reject(() => ProfileService.Apply(f.Settings, f.Data));
                foreach (var path in f.Profiles) Assert(applied[path].SequenceEqual(File.ReadAllBytes(path)));
            }),
            ("Windows1252 UTF8 and UTF16 encoding and mixed newline conventions survive", () =>
            {
                foreach (var encoding in new Encoding[] { VatFixture.Encoding, new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true), new UnicodeEncoding(true, true, true) })
                {
                    const string source = "LastSession\trealname\tÅsa Östergård\r\nOther\tkeep\tÖ\nPlugins\tPlugin0\tTopSky.dll\rPlugins\tPlugin0Display0\tÖvrig skärm";
                    byte[] bytes = encoding.GetPreamble().Concat(encoding.GetBytes(source)).ToArray();
                    byte[] changed = VatEfsProfileService.Transform(bytes, @"C:\Synthetic\VatEFS.dll", true);
                    Assert(changed.AsSpan().StartsWith(encoding.GetPreamble()));
                    string decoded = encoding.GetString(changed, encoding.GetPreamble().Length, changed.Length - encoding.GetPreamble().Length);
                    Assert(decoded.Contains("LastSession\trealname\tÅsa Östergård\r\nOther\tkeep\tÖ\nPlugins\tPlugin0\tTopSky.dll\r"));
                    Assert(!decoded.EndsWith('\n') && !decoded.EndsWith('\r'));
                    Assert(changed.SequenceEqual(VatEfsProfileService.Transform(changed, @"C:\Synthetic\VatEFS.dll", true)));
                }
            }),
            ("lease running-client and pending-GNG guards reject before mutation", () =>
            {
                using var f = new VatFixture(); var original = f.Profiles.ToDictionary(path => path, File.ReadAllBytes);
                foreach (var options in new[]
                {
                    f.Options with { HasMaintenanceLease = () => false },
                    f.Options with { RequireClosed = () => throw new InvalidOperationException("Synthetic client running") },
                    f.Options with { RequireNoPendingUpdate = _ => throw new InvalidOperationException("Synthetic pending recovery") }
                }) Reject(() => VatEfsProfileService.Apply(f.Settings, true, options));
                foreach (var path in f.Profiles) Assert(original[path].SequenceEqual(File.ReadAllBytes(path)));
                Assert(!Directory.Exists(f.Recovery));
            }),
            ("all profiles are prepared before any is written", () =>
            {
                using var f = new VatFixture();
                File.AppendAllText(f.Profiles[1], "\r\nPlugins\tPlugin0\tOther.dll", VatFixture.Encoding);
                byte[] first = File.ReadAllBytes(f.Profiles[0]);
                Reject(() => VatEfsProfileService.Apply(f.Settings, true, f.Options));
                Assert(first.SequenceEqual(File.ReadAllBytes(f.Profiles[0])) && !Directory.Exists(f.Recovery));
            }),
            ("late failure restores every earlier written profile and retains recovery", () =>
            {
                using var f = new VatFixture(); var before = f.Profiles.ToDictionary(path => path, File.ReadAllBytes);
                Reject(() => VatEfsProfileService.Apply(f.Settings, true, f.Options with
                { BeforeWrite = index => { if (index == 1) throw new IOException("Synthetic final process check failure"); } }));
                foreach (var path in f.Profiles) Assert(before[path].SequenceEqual(File.ReadAllBytes(path)));
                string backup = Directory.GetDirectories(f.Recovery).Single();
                foreach (var path in f.Profiles) Assert(before[path].SequenceEqual(File.ReadAllBytes(Path.Combine(backup, Path.GetFileName(path)))));
            }),
            ("newly added profiles stop the operation without touching the added file", () =>
            {
                using var f = new VatFixture(); byte[] before = File.ReadAllBytes(f.Profiles[0]); string added = Path.Combine(f.Data, "ESNEW.prf");
                Reject(() => VatEfsProfileService.Apply(f.Settings, true, f.Options with
                { BeforeWrite = index => { if (index == 1) File.WriteAllText(added, "NEW-PROFILE"); } }));
                Assert(before.SequenceEqual(File.ReadAllBytes(f.Profiles[0])) && File.ReadAllText(added) == "NEW-PROFILE");
            }),
            ("a client appearing between writes stops rollback too and retains exact recovery", () =>
            {
                using var f = new VatFixture(); var before = f.Profiles.ToDictionary(path => path, File.ReadAllBytes);
                bool running = false;
                try
                {
                    VatEfsProfileService.Apply(f.Settings, true, f.Options with
                    {
                        RequireClosed = () => { if (running) throw new InvalidOperationException("Synthetic EuroScope is running"); },
                        BeforeWrite = index => running = index == 1
                    });
                    throw new Exception("Expected incomplete recovery while the synthetic client is running.");
                }
                catch (VatEfsProfileRecoveryException ex)
                {
                    Assert(ex.Message.Contains("could not be fully restored") && Directory.Exists(ex.RecoveryFolder));
                    Assert(ex.InnerException is InvalidOperationException);
                }
                Assert(!before[f.Profiles[0]].SequenceEqual(File.ReadAllBytes(f.Profiles[0])));
                Assert(before[f.Profiles[1]].SequenceEqual(File.ReadAllBytes(f.Profiles[1])));
                string recovery = Directory.GetDirectories(f.Recovery).Single();
                foreach (var path in f.Profiles)
                    Assert(before[path].SequenceEqual(File.ReadAllBytes(Path.Combine(recovery, Path.GetFileName(path)))));
            }),
            ("hard-linked profiles are rejected without modifying the sibling", () =>
            {
                using var f = new VatFixture(); string sibling = Path.Combine(f.Root, "outside.prf");
                Assert(CreateHardLink(sibling, f.Profiles[0], IntPtr.Zero)); byte[] before = File.ReadAllBytes(sibling);
                Reject(() => VatEfsProfileService.Apply(f.Settings, true, f.Options));
                Assert(before.SequenceEqual(File.ReadAllBytes(sibling)) && !Directory.Exists(f.Recovery));
            }),
            ("unsupported DLL and oversized profile are rejected before any write", () =>
            {
                using var f = new VatFixture(); byte[] before = File.ReadAllBytes(f.Profiles[0]);
                File.WriteAllText(f.Dll, "not-a-plugin");
                Reject(() => VatEfsProfileService.Apply(f.Settings, true, f.Options));
                f.WriteDll(); File.WriteAllBytes(f.Profiles[1], new byte[4 * 1024 * 1024 + 1]);
                Reject(() => VatEfsProfileService.Apply(f.Settings, true, f.Options));
                Assert(before.SequenceEqual(File.ReadAllBytes(f.Profiles[0])) && !Directory.Exists(f.Recovery));
            })
        };
        foreach (var (name, run) in tests) { run(); Console.WriteLine("PASS VatEFS profile: " + name); }
        return tests.Length;
    }

    private static void Assert(bool condition) { if (!condition) throw new Exception("VatEFS profile assertion failed."); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException) { return; }
        throw new Exception("Expected VatEFS profile rejection.");
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string link, string existing, IntPtr security);

    private sealed class VatFixture : IDisposable
    {
        internal static readonly Encoding Encoding = System.Text.Encoding.GetEncoding(1252);
        internal const string Identity = "LastSession\trealname\tÅsa Östergård\r\nLastSession\tpassword\tSYNTHETIC-UNCHANGED\r\nLastSession\tcertificate\tSYNTHETIC-CID";
        internal const string OtherPlugin = "Plugins\tPlugin0\tTopSky.dll\r\nPlugins\tPlugin0Display0\tStandard ES radar screen";
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "LaunchpadVatEfsSynthetic-" + Guid.NewGuid().ToString("N"));
        internal string Data => Path.Combine(Root, "data");
        internal string Recovery => Path.Combine(Root, "recovery");
        internal string Dll => Path.Combine(Root, "plugin", "VatEFS.dll");
        internal string Unrelated => Path.Combine(Data, "Custom.prf");
        internal string[] Profiles => [Path.Combine(Data, "ESAA APP ACC.prf"), Path.Combine(Data, "ESGG TWR.prf")];
        internal AppSettings Settings { get; }
        internal VatEfsProfileService.EnvironmentOptions Options => new(Recovery, () => { }, _ => { }, () => true);
        internal VatFixture()
        {
            Directory.CreateDirectory(Data); Directory.CreateDirectory(Path.GetDirectoryName(Dll)!);
            Settings = new() { EuroscopeDataPath = Data, VatEfsPath = Path.GetDirectoryName(Dll)!, VatsimCid = "not-to-be-applied" };
            foreach (var path in Profiles) File.WriteAllText(path, Identity + "\r\n" + OtherPlugin, Encoding);
            File.WriteAllText(Unrelated, "UNRELATED"); WriteDll();
        }
        internal void WriteDll()
        {
            byte[] bytes = new byte[128]; bytes[0] = (byte)'M'; bytes[1] = (byte)'Z'; bytes[60] = 64;
            bytes[64] = (byte)'P'; bytes[65] = (byte)'E'; bytes[68] = 0x4c; bytes[69] = 1; bytes[87] = 0x20;
            File.WriteAllBytes(Dll, bytes);
        }
        public void Dispose()
        {
            string full = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(full) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()) || !Path.GetFileName(full).StartsWith("LaunchpadVatEfsSynthetic-", StringComparison.Ordinal))
                throw new Exception("Unsafe fixture cleanup target.");
            Directory.Delete(full, recursive: true);
        }
    }
}
