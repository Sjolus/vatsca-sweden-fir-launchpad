using System.Text;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

if (args.Length != 0) throw new ArgumentException("This harness accepts no paths or live-data arguments.");
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var tests = new (string Name, Action Run)[]
{
    ("blank targets fail before credentials or logging", () =>
    {
        foreach (var path in new[] { "", " ", "\t" }) RejectUnavailable(path);
    }),
    ("missing target fails without creating it or reading credentials", () =>
    {
        using var f = new Fixture(); var missing = Path.Combine(f.Root, "never-created");
        RejectUnavailable(missing);
        Check(!Directory.Exists(missing), "Missing target was created.");
    }),
    ("target deleted after review fails before credentials or logging", () =>
    {
        using var f = new Fixture(); var removed = Path.Combine(f.Root, "removed-after-review");
        Directory.CreateDirectory(removed);
        Check(Directory.Exists(removed), "Synthetic target was not created.");
        Directory.Delete(removed); // Exact empty child created by this test; no recursive deletion.
        RejectUnavailable(removed);
        Check(!Directory.Exists(removed), "Deleted target was recreated.");
    }),
    ("preview is read-only and masked; apply preserves Windows-1252 Swedish fields", () =>
    {
        using var f = new Fixture(); f.CreateProfiles();
        var before = f.Profiles.ToDictionary(path => path, File.ReadAllBytes);
        var masked = ProfileService.GeneratePreview(f.Settings, Fixture.Password, Fixture.Hoppie, f.Root);
        Check(CredentialManagerService.Reads == 0 && Logger.Messages.Count == 0, "Preview accessed the credential service or logger.");
        Check(!masked.Contains(Fixture.Password) && !masked.Contains(Fixture.Hoppie) && !masked.Contains(Fixture.OldPassword), "Masked preview exposed a credential.");
        Check(masked.Contains(f.Settings.VatsimName) && masked.Contains(f.Settings.VatsimCid), "Preview omitted reviewed identity fields.");
        foreach (var path in f.Profiles) Check(before[path].SequenceEqual(File.ReadAllBytes(path)), "Preview changed a profile.");

        var clear = ProfileService.GeneratePreview(f.Settings, Fixture.Password, Fixture.Hoppie, f.Root, showCredentials: true);
        var expected = new[]
        {
            "LastSession\trealname\t" + f.Settings.VatsimName,
            "LastSession\tcertificate\t" + f.Settings.VatsimCid,
            "LastSession\tpassword\t" + Fixture.Password,
            "LastSession\trating\t3",
            "LastSession\tserver\tAUTOMATIC"
        };
        foreach (var field in expected) Check(clear.Contains(field), "Clear preview omitted a field that will be applied.");
        ProfileService.Apply(f.Settings, f.Root);
        foreach (var path in f.Profiles)
        {
            var bytes = File.ReadAllBytes(path);
            var lines = File.ReadAllLines(path, Fixture.Encoding);
            foreach (var field in expected) Check(lines.Contains(field), "Applied fields differ from the reviewed fields.");
            Check(bytes.AsSpan().IndexOf(Fixture.Encoding.GetBytes(expected[0])) >= 0, "Swedish identity was not written as Windows-1252 bytes.");
            Check(!bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()), "Profile unexpectedly acquired a UTF-8 BOM.");
            Check(lines.Contains(Fixture.Comment), "Unrelated Swedish text changed.");
            Check(Fixture.PluginRows.All(lines.Contains), "Unrelated plugin rows changed.");
        }
        Check(File.ReadAllText(Path.Combine(f.Root, "ESAA", "Plugins", "TopSkyCPDLChoppieCode.txt")) == Fixture.Hoppie, "Synthetic Hoppie value was not applied.");
        var login = File.ReadAllLines(Path.Combine(f.Root, "ESAA", "Settings", "LoginProfiles.txt"), Fixture.Encoding);
        Check(login.Contains("PROFILE:ES_OBS:300:0") && login.Contains(Fixture.Comment), "OBS change damaged unrelated login text.");
        Check(!Logger.Messages.Any(message => message.Contains(Fixture.Password) || message.Contains(Fixture.Hoppie)), "Application log included a credential.");
    }),
    ("enabling VatEFS preserves unrelated plugin slots and display rows", () =>
    {
        using var f = new Fixture(); f.CreateProfiles();
        f.Settings.PatchVatEfs = true;
        f.Settings.VatEfsPath = Path.Combine(f.Root, "SyntheticVatEfs");
        Directory.CreateDirectory(f.Settings.VatEfsPath);
        var dll = Path.Combine(f.Settings.VatEfsPath, "VatEFS.dll");
        File.WriteAllBytes(dll, Array.Empty<byte>()); // Marker only; never loaded or executed.
        var preview = ProfileService.GeneratePreview(f.Settings, Fixture.Password, Fixture.Hoppie, f.Root);
        var pluginLine = "Plugins\tPlugin4\t" + dll;
        Check(preview.Contains(pluginLine), "Preview did not preserve the existing plugin slots.");
        ProfileService.Apply(f.Settings, f.Root);
        foreach (var path in f.Profiles)
        {
            var lines = File.ReadAllLines(path, Fixture.Encoding);
            Check(lines.Count(line => line == pluginLine) == 1, "VatEFS was not added exactly once in the reviewed slot.");
            Check(Fixture.PluginRows.All(lines.Contains), "Enabling VatEFS changed another plugin or its display rows.");
            Check(lines.Contains("LastSession\trealname\tÅsa Östergård"), "Plugin update damaged the Swedish identity.");
        }
        var first = f.Profiles.ToDictionary(path => path, File.ReadAllBytes);
        ProfileService.Apply(f.Settings, f.Root);
        foreach (var path in f.Profiles) Check(first[path].SequenceEqual(File.ReadAllBytes(path)), "Repeating the same apply changed profile bytes.");
    })
};

foreach (var test in tests)
{
    CredentialManagerService.Reset(); Logger.Messages.Clear();
    test.Run();
    Console.WriteLine("PASS profile: " + test.Name);
}
Console.WriteLine($"{tests.Length} profile checks passed. Synthetic temporary files only; no real credentials, settings, registry, apps or network.");

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void RejectUnavailable(string path)
{
    CredentialManagerService.Reset(); Logger.Messages.Clear();
    try { ProfileService.Apply(new AppSettings(), path); throw new Exception("Unavailable target was accepted."); }
    catch (DirectoryNotFoundException) { }
    Check(CredentialManagerService.Reads == 0, "Unavailable-target guard accessed credentials.");
    Check(Logger.Messages.Count == 0, "Unavailable-target guard reached the logger.");
}

sealed class Fixture : IDisposable
{
    internal const string Password = "SYNTHETIC-PASSWORD-ONLY";
    internal const string OldPassword = "SYNTHETIC-OLD-PASSWORD-ONLY";
    internal const string Hoppie = "SYNTHETIC-HOPPIE-ONLY";
    internal const string Comment = "; Övrigt – behåll ÅÄÖ åäö";
    internal static readonly Encoding Encoding = Encoding.GetEncoding(1252);
    internal static readonly string[] PluginRows =
    [
        "Plugins\tPlugin0\tESAA\\Plugins\\TopSky.dll",
        "Plugins\tPlugin0Display0\tGround Radar display",
        "Plugins\tPlugin0Display1\tStandard ES radar screen",
        "Plugins\tPlugin3\tESAA\\Plugins\\Custom.dll",
        "Plugins\tPlugin3Display0\tStandard ES radar screen",
        "Plugins\tCustomSetting\tBehåll"
    ];
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "Launchpad-Profile-tests-" + Guid.NewGuid().ToString("N"));
    internal string[] Profiles => [Path.Combine(Root, "ESAA TOPSKY.prf"), Path.Combine(Root, "ESAA TWR.prf")];
    internal AppSettings Settings { get; } = new()
    {
        VatsimName = "Åsa Östergård", VatsimCid = "9999999", VatsimRating = 3, ObsCallsign = "ES"
    };
    internal Fixture()
    {
        Directory.CreateDirectory(Root);
        CredentialManagerService.Values[CredentialManagerService.TargetVatsim] = Password;
        CredentialManagerService.Values[CredentialManagerService.TargetHoppie] = Hoppie;
    }
    internal void CreateProfiles()
    {
        var source = new[]
        {
            Comment, "LastSession\trealname\tGöran Åström", "LastSession\tcertificate\t1111111",
            "LastSession\tpassword\t" + OldPassword, "LastSession\trating\t2", "LastSession\tserver\tOLD",
            "LastSession\tcustom\tÖvrig inställning"
        }.Concat(PluginRows).ToArray();
        foreach (var profile in Profiles) File.WriteAllLines(profile, source, Encoding);
        var folder = Path.Combine(Root, "ESAA", "Settings");
        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, "LoginProfiles.txt"), [Comment, "PROFILE", "PROFILE:OLD_OBS:300:0", "END"], Encoding);
    }
    public void Dispose()
    {
        var full = Path.GetFullPath(Root);
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith("Launchpad-Profile-tests-", StringComparison.Ordinal))
            throw new Exception("Refused synthetic cleanup outside the owned temporary root.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }
}
