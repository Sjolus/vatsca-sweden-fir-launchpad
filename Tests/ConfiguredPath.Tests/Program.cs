using System.Buffers.Binary;
using System.Text;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

var root = Path.Combine(Path.GetTempPath(), "Launchpad-ConfiguredPath-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
var service = new ConfiguredPathValidationService();
try
{
    void Check(string name, Action body) { body(); passed++; Console.WriteLine("PASS " + name); }
    void Status(ConfiguredPathKind kind, string path, PathValidationStatus expected, ConfiguredPathValidationService? validator = null)
    {
        var result = (validator ?? service).Validate(kind, path);
        if (result.Status != expected) throw new Exception($"Expected {expected}, got {result.Status}: {result.Message}");
    }
    string Folder(string name) { var path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
    string Binary(string name, string product, string version = "1.4.0", bool dll = false, string? original = null, bool nsis = false)
    {
        string folder = Folder(Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, Fixtures.Pe(product, version, original ?? name, dll, nsis));
        return path;
    }
    var kinds = new[] { ConfiguredPathKind.EuroScopeExecutable, ConfiguredPathKind.TrackAudioExecutable,
        ConfiguredPathKind.VacsExecutable, ConfiguredPathKind.VatisExecutable };
    var names = new[] { "EuroScope.exe", "trackaudio.exe", "vacs-client.exe", "vATIS.exe" };
    var products = new[] { "EuroScope Application", "TrackAudio", "vacs", "vATIS" };
    var versions = new[] { "3.2.3.2", "1.4.0", "2.8.0", "4.1.0-beta.19" };
    var apps = names.Select((name, i) => Binary(name, products[i], versions[i])).ToArray();

    Check("empty optional fields perform no filesystem access", () =>
    {
        var noAccess = new ConfiguredPathValidationService(new() { ReadPath = _ => throw new Exception("Unexpected access") });
        if (noAccess.Validate(new AppSettings()).Count(r => r.Status == PathValidationStatus.NotSelected) != 6) throw new Exception("Missing optional results");
    });
    Check("relative path rejected", () => Status(kinds[0], "EuroScope.exe", PathValidationStatus.Invalid));
    Check("quoted path has actionable rejection", () =>
    {
        var result = service.Validate(kinds[0], '"' + apps[0] + '"');
        if (result.Status != PathValidationStatus.Invalid || !result.Message.Contains("quotation")) throw new Exception("Quoted path guidance missing");
    });
    Check("missing executable rejected", () => Status(kinds[0], Path.Combine(root, "absent.exe"), PathValidationStatus.Invalid));
    Check("directory instead of executable rejected", () => Status(kinds[0], root, PathValidationStatus.Invalid));
    Check("file instead of data folder rejected", () => Status(ConfiguredPathKind.EuroScopeDataFolder, apps[0], PathValidationStatus.Invalid));
    Check("shortcut is not an executable", () =>
    {
        var file = Path.Combine(root, "EuroScope.lnk"); File.WriteAllText(file, "synthetic shortcut");
        Status(kinds[0], file, PathValidationStatus.Invalid);
    });
    for (int i = 0; i < kinds.Length; i++)
    {
        int app = i;
        Check("real synthetic PE metadata recognizes " + products[i], () => Status(kinds[app], apps[app], PathValidationStatus.Verified));
    }
    Check("whitespace trimmed in result", () =>
    {
        var result = service.Validate(kinds[1], "  " + apps[1] + "  ");
        if (result.Path != apps[1] || result.Status != PathValidationStatus.Verified) throw new Exception("Whitespace normalization failed");
    });
    Check("older EuroScope remains optional warning", () => Status(kinds[0], Binary(names[0], products[0], "3.2.2.3"), PathValidationStatus.Warning));
    Check("older VACS remains optional warning", () => Status(kinds[2], Binary(names[2], products[2], "1.9.0"), PathValidationStatus.Warning));
    Check("recognized custom executable name warns", () => Status(kinds[1], Binary("my-audio.exe", products[1]), PathValidationStatus.Warning));
    Check("renamed unrelated executable rejected", () => Status(kinds[1], Binary(names[1], "Unrelated Editor"), PathValidationStatus.Invalid));
    Check("wrong ATC application's identity rejected", () => Status(kinds[2], Binary(names[2], "TrackAudio"), PathValidationStatus.Invalid));
    Check("renamed installer metadata rejected", () => Status(kinds[1], Binary(names[1], products[1], original: "Setup.exe"), PathValidationStatus.Invalid));
    Check("renamed matching-product NSIS installer rejected", () => Status(kinds[2], Binary(names[2], products[2], "2.8.0", nsis: true), PathValidationStatus.Invalid));
    Check("renamed vATIS Velopack Setup rejected by embedded bundle", () =>
    {
        var file = Binary(names[3], products[3], versions[3], original: "");
        var image = File.ReadAllBytes(file);
        int offset = image.Length;
        Array.Resize(ref image, offset + 64);
        // The native Setup descriptor lies inside .text, before the end of PE sections.
        const int marker = 576;
        BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(marker - 16), offset);
        BinaryPrimitives.WriteInt64LittleEndian(image.AsSpan(marker - 8), 64);
        Convert.FromHexString("94F0B17B6893E02937EB34EF53AAE7D42B54F5707EF5D6F57854983E5E94ED7D").CopyTo(image, marker);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset), 0x04034b50);
        File.WriteAllBytes(file, image);
        Status(kinds[3], file, PathValidationStatus.Invalid);
    });
    Check("DLL renamed to exe rejected", () => Status(kinds[1], Binary(names[1], products[1], dll: true), PathValidationStatus.Invalid));
    Check("missing metadata cannot verify expected filename", () => Status(kinds[1], Binary(names[1], "", original: ""), PathValidationStatus.Warning));
    Check("missing metadata cannot verify arbitrary filename", () => Status(kinds[1], Binary("unrelated.exe", "", original: ""), PathValidationStatus.Invalid));
    Check("vATIS managed original filename supported", () => Status(kinds[3], Binary(names[3], products[3], versions[3], original: "vATIS.dll"), PathValidationStatus.Verified));
    Check("malformed binary rejected", () =>
    {
        var file = Path.Combine(root, "malformed.exe"); File.WriteAllBytes(file, [0x4d, 0x5a, 0]);
        Status(kinds[1], file, PathValidationStatus.Invalid);
    });
    Check("out-of-bounds PE section rejected", () =>
    {
        var file = Binary(names[1], products[1]); var bytes = File.ReadAllBytes(file);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x178 + 20), uint.MaxValue); File.WriteAllBytes(file, bytes);
        Status(kinds[1], file, PathValidationStatus.Invalid);
    });
    Check("oversized resource directory inspection is bounded", () =>
    {
        var file = Binary(names[1], products[1]); var bytes = File.ReadAllBytes(file);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x98 + 96 + 20), 9 * 1024 * 1024); File.WriteAllBytes(file, bytes);
        Status(kinds[1], file, PathValidationStatus.Warning);
    });
    Check("access failure is sanitized warning", () =>
    {
        var validator = new ConfiguredPathValidationService(new() { ReadPath = _ => throw new UnauthorizedAccessException("private fixture detail") });
        var result = validator.Validate(kinds[0], apps[0]);
        if (result.Status != PathValidationStatus.Warning || result.Message.Contains("private fixture")) throw new Exception("Access failure leaked detail");
    });
    Check("network path is not probed", () =>
    {
        var validator = new ConfiguredPathValidationService(new() { ReadPath = _ => throw new Exception("Unexpected network access") });
        Status(kinds[1], @"\\fixture-host\apps\trackaudio.exe", PathValidationStatus.Warning, validator);
    });
    Check("VatEFS DLL metadata verified", () =>
    {
        var file = Binary("VatEFS.dll", "VatEFS", dll: true);
        Status(ConfiguredPathKind.VatEfsFolder, Path.GetDirectoryName(file)!, PathValidationStatus.Verified);
    });
    Check("VatEFS uncertain custom metadata warns", () =>
    {
        var file = Binary("VatEFS.dll", "", dll: true, original: "");
        Status(ConfiguredPathKind.VatEfsFolder, Path.GetDirectoryName(file)!, PathValidationStatus.Warning);
    });
    Check("VatEFS missing DLL rejected", () => Status(ConfiguredPathKind.VatEfsFolder, Folder("empty-plugin"), PathValidationStatus.Invalid));
    Check("executable pretending to be VatEFS DLL rejected", () =>
    {
        var file = Binary("VatEFS.dll", "VatEFS");
        Status(ConfiguredPathKind.VatEfsFolder, Path.GetDirectoryName(file)!, PathValidationStatus.Invalid);
    });

    const string generation = "ESAA-Sweden_20260223160904-260201-0003";
    string Bundle(string name, bool pair = true, bool activeDll = true)
    {
        string folder = Folder(name); Directory.CreateDirectory(Path.Combine(folder, "ESAA", "Plugins"));
        File.WriteAllText(Path.Combine(folder, "ESAA TOPSKY.prf"), "synthetic profile marker");
        File.WriteAllText(Path.Combine(folder, generation + ".sct"), "synthetic sector marker");
        if (pair) File.WriteAllText(Path.Combine(folder, generation + ".ese"), "synthetic sector marker");
        if (activeDll) File.WriteAllText(Path.Combine(folder, "ESAA", "Plugins", "TopSky.dll"), "synthetic file marker");
        return folder;
    }
    Check("empty GNG folder rejected", () => Status(ConfiguredPathKind.EuroScopeDataFolder, Folder("empty-gng"), PathValidationStatus.Invalid));
    Check("bare ESAA directory is insufficient", () =>
    {
        var folder = Folder("bare-gng"); Directory.CreateDirectory(Path.Combine(folder, "ESAA"));
        Status(ConfiguredPathKind.EuroScopeDataFolder, folder, PathValidationStatus.Invalid);
    });
    Check("selected inner ESAA gives parent guidance", () =>
    {
        var result = service.Validate(ConfiguredPathKind.EuroScopeDataFolder, Path.Combine(Bundle("inner-gng"), "ESAA"));
        if (result.Status != PathValidationStatus.Invalid || !result.Message.Contains("parent")) throw new Exception("Missing parent guidance");
    });
    Check("program folder gives data-folder guidance", () => Status(ConfiguredPathKind.EuroScopeDataFolder, Path.GetDirectoryName(apps[0])!, PathValidationStatus.Invalid));
    Check("known package markers verify without reading profile content", () =>
    {
        var folder = Bundle("complete-gng");
        using var lockedProfile = new FileStream(Path.Combine(folder, "ESAA TOPSKY.prf"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Status(ConfiguredPathKind.EuroScopeDataFolder, folder, PathValidationStatus.Verified);
    });
    Check("partial GNG pair warns", () => Status(ConfiguredPathKind.EuroScopeDataFolder, Bundle("partial-gng", pair: false), PathValidationStatus.Warning));
    Check("staged DLLs do not count as active plugin evidence", () =>
    {
        var folder = Bundle("staged-gng", activeDll: false); var stage = Path.Combine(folder, "ESAA", "Plugins", "Updated Plugin DLLs");
        Directory.CreateDirectory(stage); File.WriteAllText(Path.Combine(stage, "TopSky.dll"), "synthetic staged marker");
        Status(ConfiguredPathKind.EuroScopeDataFolder, folder, PathValidationStatus.Warning);
    });
    Check("oversized directory becomes warning", () =>
    {
        var validator = new ConfiguredPathValidationService(new()
        {
            ReadPath = _ => new("fixture", true),
            EnumerateDirectory = _ => Enumerable.Range(0, 2049).Select(i => new ConfiguredPathEntry(i + ".sct", false, 1)).ToArray()
        });
        Status(ConfiguredPathKind.EuroScopeDataFolder, root, PathValidationStatus.Warning, validator);
    });
    Check("unchanged invalid and warning paths can be retained", () =>
    {
        var settings = new AppSettings { TrackAudioExePath = "  " + apps[1] + "  " };
        foreach (var status in new[] { PathValidationStatus.Invalid, PathValidationStatus.Warning })
            if (ConfiguredPathValidationService.NeedsAttention(new(kinds[1], apps[1].ToUpperInvariant(), status, ""), settings)) throw new Exception("Unchanged path blocked");
    });
    Check("changed invalid and warning paths require attention", () =>
    {
        foreach (var status in new[] { PathValidationStatus.Invalid, PathValidationStatus.Warning })
            if (!ConfiguredPathValidationService.NeedsAttention(new(kinds[1], apps[1], status, ""), new())) throw new Exception("Changed issue missed");
    });
    Check("legacy null setting is treated as an empty location", () =>
    {
        var settings = new AppSettings { TrackAudioExePath = null! };
        if (ConfiguredPathValidationService.PathFor(kinds[1], settings) != "" ||
            !ConfiguredPathValidationService.NeedsAttention(new(kinds[1], apps[1], PathValidationStatus.Warning, ""), settings))
            throw new Exception("Null legacy setting was not handled");
    });
    Check("clearing optional path never requires warning override", () =>
    {
        if (ConfiguredPathValidationService.NeedsAttention(new(kinds[1], " ", PathValidationStatus.Invalid, ""), new() { TrackAudioExePath = apps[1] })) throw new Exception("Empty blocked");
    });
    Check("verified changed path does not require override", () =>
    {
        if (ConfiguredPathValidationService.NeedsAttention(new(kinds[1], apps[1], PathValidationStatus.Verified, ""), new())) throw new Exception("Verified blocked");
    });
    Check("all settings fields map to the matching result", () =>
    {
        var settings = new AppSettings { EuroscopeExePath = apps[0], TrackAudioExePath = apps[1], VacsExePath = apps[2], VatisExePath = apps[3], EuroscopeDataPath = "data", VatEfsPath = "plugin" };
        var results = service.Validate(settings);
        if (results.Count != 6 || results.Select(r => r.Kind).Distinct().Count() != 6 || results.Any(r => r.Path != ConfiguredPathValidationService.PathFor(r.Kind, settings))) throw new Exception("Incorrect field mapping");
    });
    Console.WriteLine($"Configured path checks: {passed} passed. Synthetic files and injected probes only; no application execution.");
}
finally
{
    // This unique directory was created above and contains only this harness's fixtures.
    if (Path.GetFileName(root).StartsWith("Launchpad-ConfiguredPath-", StringComparison.Ordinal) && Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()))
        Directory.Delete(root, recursive: true);
}

static class Fixtures
{
    public static byte[] Pe(string product, string version, string original, bool dll, bool nsis)
    {
        byte[] Text(string value) => Encoding.Unicode.GetBytes(value + '\0');
        byte[] String(string key, string value) => Block(key, 1, Text(value), (ushort)(value.Length + 1));
        var strings = Block("040904B0", 1, [], 0,
            String("ProductName", product), String("FileDescription", product), String("OriginalFilename", original),
            String("InternalName", Path.GetFileNameWithoutExtension(original)), String("ProductVersion", version), String("FileVersion", version));
        var fixedInfo = new byte[52];
        Put32(fixedInfo, 0, 0xFEEF04BD); Put32(fixedInfo, 4, 0x10000); Put32(fixedInfo, 8, 0x10004);
        Put32(fixedInfo, 16, 0x10004); Put32(fixedInfo, 24, 0x3f); Put32(fixedInfo, 32, 0x40004); Put32(fixedInfo, 36, dll ? 2u : 1u);
        var versionResource = Block("VS_VERSION_INFO", 0, fixedInfo, 52,
            Block("StringFileInfo", 1, [], 0, strings), Block("VarFileInfo", 1, [], 0, Block("Translation", 0, [0x09, 0x04, 0xb0, 0x04], 4)));
        var resource = new byte[88 + versionResource.Length];
        foreach (int offset in new[] { 0, 24, 48 }) Put16(resource, offset + 14, 1);
        Put32(resource, 16, 16); Put32(resource, 20, 0x80000018);
        Put32(resource, 40, 1); Put32(resource, 44, 0x80000030);
        Put32(resource, 64, 0x409); Put32(resource, 68, 72);
        Put32(resource, 72, 0x2000 + 88); Put32(resource, 76, (uint)versionResource.Length); Put32(resource, 80, 1200);
        versionResource.CopyTo(resource, 88);
        int rawSize = (resource.Length + 511) & ~511;
        var image = new byte[1024 + rawSize + (nsis ? 512 : 0)];
        image[0] = (byte)'M'; image[1] = (byte)'Z'; Put32(image, 60, 0x80); Put32(image, 0x80, 0x4550);
        Put16(image, 0x84, 0x14c); Put16(image, 0x86, 2); Put16(image, 0x94, 224); Put16(image, 0x96, dll ? (ushort)0x2102 : (ushort)0x102);
        const int optional = 0x98;
        Put16(image, optional, 0x10b); Put32(image, optional + 4, 512); Put32(image, optional + 8, (uint)rawSize);
        Put32(image, optional + 16, 0x1000); Put32(image, optional + 20, 0x1000); Put32(image, optional + 24, 0x2000);
        Put32(image, optional + 28, 0x400000); Put32(image, optional + 32, 4096); Put32(image, optional + 36, 512);
        Put16(image, optional + 40, 6); Put16(image, optional + 48, 6); Put32(image, optional + 56, 0x3000);
        Put32(image, optional + 60, 512); Put16(image, optional + 68, 3); Put32(image, optional + 92, 16);
        Put32(image, optional + 112, 0x2000); Put32(image, optional + 116, (uint)resource.Length);
        Section(image, 0x178, ".text", 1, 0x1000, 512, 512, 0x60000020);
        Section(image, 0x1a0, ".rsrc", (uint)resource.Length, 0x2000, (uint)rawSize, 1024, 0x40000040);
        image[512] = 0xc3; resource.CopyTo(image, 1024);
        if (nsis)
        {
            int overlay = 1024 + rawSize; Put32(image, overlay + 4, 0xDEADBEEF);
            Encoding.ASCII.GetBytes("NullsoftInst").CopyTo(image, overlay + 8);
        }
        return image;
    }

    private static byte[] Block(string key, ushort type, byte[] value, ushort valueLength, params byte[][] children)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true);
        writer.Write((ushort)0); writer.Write(valueLength); writer.Write(type); writer.Write(Encoding.Unicode.GetBytes(key + '\0'));
        void Align() { while (stream.Position % 4 != 0) writer.Write((byte)0); }
        Align(); writer.Write(value);
        foreach (var child in children) { Align(); writer.Write(child); }
        var result = stream.ToArray(); Put16(result, 0, checked((ushort)result.Length)); return result;
    }
    private static void Section(byte[] image, int offset, string name, uint size, uint rva, uint rawSize, uint rawOffset, uint flags)
    {
        Encoding.ASCII.GetBytes(name).CopyTo(image, offset); Put32(image, offset + 8, size); Put32(image, offset + 12, rva);
        Put32(image, offset + 16, rawSize); Put32(image, offset + 20, rawOffset); Put32(image, offset + 36, flags);
    }
    private static void Put16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    private static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
}
