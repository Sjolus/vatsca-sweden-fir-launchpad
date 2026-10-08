using System.IO.Compression;
using System.Text;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

sealed class Fixture
{
    public const string OldIdentity = "20260903120000-260901-0001";
    public const string NewIdentity = "20261001120000-261001-0003";
    public const string OldStem = "ESAA-Sweden_" + OldIdentity;
    public const string NewStem = "ESAA-Sweden_" + NewIdentity;
    public const string ProfilePath = "ESAA APP ACC.prf";
    public const string FakePassword = "synthetic-password-never-a-real-secret";
    public const string FakeHoppie = "synthetic-hoppie-never-a-real-secret";
    public const string LocalProfile =
        "LastSession\trealname\tÅke Öström\r\n" +
        "LastSession\tcertificate\t0000000\r\n" +
        "LastSession\tpassword\t" + FakePassword + "\r\n" +
        "LastSession\trating\t3\r\n" +
        "LastSession\tserver\tAUTOMATIC\r\n" +
        "LastSession\tcallsign\tTEST_OBS\r\n" +
        "Settings\tsector\t" + OldStem + ".sct\r\n" +
        "Plugins\tPlugin0\tESAA\\Plugins\\TopSky.dll\r\n" +
        "Plugins\tPlugin0Display0\tStandard ES radar screen\r\n" +
        "Plugins\tPlugin1\tESAA\\Plugins\\AfvEuroScopeBridge.dll\r\n" +
        "Plugins\tPlugin1Display0\tStandard ES radar screen\r\n" +
        "Plugins\tPlugin2\tC:\\Synthetic-VatEFS\\VatEFS.dll\r\n" +
        "Plugins\tPlugin2Display0\tGround Radar display\r\n" +
        "Plugins\tPlugin2Display1\tStandard ES radar screen\r\n";
    public const string PackageProfile =
        "LastSession\trealname\tPackage Example\r\n" +
        "LastSession\tcertificate\t1111111\r\n" +
        "LastSession\tpassword\tpackage-placeholder\r\n" +
        "LastSession\trating\t1\r\n" +
        "Settings\tsector\t" + NewStem + ".sct\r\n" +
        "Plugins\tPlugin0\tESAA\\Plugins\\TopSky.dll\r\n" +
        "Plugins\tPlugin0Display0\tStandard ES radar screen\r\n" +
        "Plugins\tPlugin1\tESAA\\Plugins\\RDFPlugin.dll\r\n" +
        "Plugins\tPlugin1Display0\tStandard ES radar screen\r\n";

    public string DirectoryPath { get; }
    public string Root { get; }
    public string StorageRoot => Path.Combine(DirectoryPath, "Launchpad-storage-synthetic");
    public string ZipPath { get; set; }
    public Dictionary<string, string> Entries { get; } = new(StringComparer.Ordinal);
    public static Encoding ProfileEncoding => Encoding.GetEncoding(1252);

    public Fixture(string directory)
    {
        DirectoryPath = directory;
        Root = Path.Combine(directory, "EuroScope-synthetic");
        ZipPath = Path.Combine(directory, "ESAA-Update-Only_" + NewIdentity + ".zip");
        Directory.CreateDirectory(Root);
        Write(OldStem + ".sct", "synthetic old sector");
        Write(OldStem + ".ese", "synthetic old sector extension");
        Write(ProfilePath, LocalProfile);
        Write("ESAA/Plugins/TopSky.dll", "synthetic old TopSky DLL");
        Write("ESAA/Plugins/AfvEuroScopeBridge.dll", "synthetic old AFV DLL");
        Write("ESAA/Plugins/TopSkySettingsLocal.txt", "synthetic local preferences ÅÄÖ");
        Write("ESAA/Plugins/TopSkyCPDLChoppieCode.txt", FakeHoppie);
        Write("ESAA/Settings/LoginProfiles.txt", "synthetic TEST_OBS login ÅÄÖ");
        Write("Custom.prf", "synthetic custom profile ÅÄÖ");
        Entries[NewStem + ".sct"] = "synthetic new sector";
        Entries[NewStem + ".ese"] = "synthetic new sector extension";
        Entries[ProfilePath] = PackageProfile;
        Entries["ESAA/Plugins/TopSky.dll"] = "synthetic new TopSky DLL";
        Entries["ESAA/Plugins/RDFPlugin.dll"] = "synthetic new RDF DLL";
        Entries["ESAA/Plugins/TopSkySettings.txt"] = "synthetic new global settings";
        Entries["ESAA/Settings/LoginProfiles.txt"] = "synthetic package login defaults";
        Entries["ESAA/TopSky.ttf"] = "synthetic font bytes";
        SaveZip();
    }

    public string PathFor(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
    public void Write(string relative, string text)
    {
        var path = PathFor(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, ProfileEncoding);
    }
    public string Read(string relative) => File.ReadAllText(PathFor(relative), ProfileEncoding);
    public byte[] Bytes(string relative) => File.ReadAllBytes(PathFor(relative));
    public void SaveZip(string wrapper = "")
    {
        using var stream = new FileStream(ZipPath, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var item in Entries)
        {
            var entry = zip.CreateEntry(wrapper + item.Key);
            using var writer = new StreamWriter(entry.Open(), ProfileEncoding);
            writer.Write(item.Value);
        }
    }
    public void AddRawEntry(string name, string content, int? externalAttributes = null)
    {
        using var zip = ZipFile.Open(ZipPath, ZipArchiveMode.Update);
        var entry = zip.CreateEntry(name);
        if (externalAttributes.HasValue) entry.ExternalAttributes = externalAttributes.Value;
        using var writer = new StreamWriter(entry.Open(), ProfileEncoding);
        writer.Write(content);
    }
    public Action RequireClosed { get; set; } = () => { };
    public string GateName { get; } = @"Local\GngUpdateTests." + Guid.NewGuid().ToString("N");
    public GngUpdateService.OperationContext Context => new(StorageRoot, () => RequireClosed(), () => MaintenanceLock.TryAcquire(GateName, out var lease) ? lease! : throw new InvalidOperationException("Synthetic maintenance gate occupied."));
    public GngUpdatePlan Plan(IEnumerable<string>? protectedPaths = null, string? fullPackagePath = null, bool preserveListLayout = true) => GngUpdateService.BuildPlan(ZipPath, Root, protectedPaths, Context, fullPackagePath, preserveListLayout);
    public GngUpdateResult Install(GngUpdatePlan plan, IProgress<string>? progress = null) => GngUpdateService.Install(plan, progress, Context);
    public GngUpdateResult Restore(string backupFolder, IEnumerable<string>? protectedPaths = null) => GngUpdateService.Restore(backupFolder, Root, protectedPaths, null, Context);
    public Dictionary<string, byte[]> Snapshot() => Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
        .ToDictionary(p => Path.GetRelativePath(Root, p), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
}
