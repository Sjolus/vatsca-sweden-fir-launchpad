using System.Buffers.Binary;
using System.Diagnostics;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;
using static PluginRegistrationFixture;

internal static class PluginRegistrationTests
{
    internal static int Run()
    {
        using var fixture = new Fixture();
        string path = Path.Combine(fixture.Root, "inert-registration.dll");
        ReportingPluginRegistration? Read(byte[] image)
        {
            File.WriteAllBytes(path, image);
            return ReportingPluginRegistrationReader.Read(path);
        }
        ReportingPluginMetadata Metadata(byte[] image)
        {
            File.WriteAllBytes(path, image);
            return ReportingPluginVersionReader.Read(path, FileVersionInfo.GetVersionInfo(path));
        }
        var examples = new[] {
            ("Audio for Vatsim EuroScope Bridge", "1.3.0", ReportingDllProduct.AfvEuroscopeBridge),
            ("CDM Plugin", "2.27", ReportingDllProduct.Cdm),
            ("VATCAN Bookings", "1.1.6-2022wb", ReportingDllProduct.VatcanBookings),
            ("Arrival Load Balance", "0.2.11T", ReportingDllProduct.ArrivalLoadBalancer),
            ("VatIRIS", "0.0.5", ReportingDllProduct.Vatiris),
            ("VatEFS", "0.0.5", ReportingDllProduct.Vatefs),
            ("SAAMAN", "1.3.37", ReportingDllProduct.Saaman)
        };
        foreach (var (name, version, product) in examples)
        {
            var result = Metadata(Create(name, version));
            Check(result == new ReportingPluginMetadata(product, version, "9.8.7.6", ReportingDllVersionSource.Registration), name);
        }
        var unknown = Metadata(Create("Private local plugin name", "1.2.3"));
        Check(unknown.Product == ReportingDllProduct.Unknown && unknown.Version == "1.2.3", "Unknown products remain versioned without exporting their name");
        Check(!ReportingWire.Version("1.1.6-2022wb") && ReportingWire.DllVersion("1.1.6-2022wb"), "Narrow DLL-only grammar");
        Check(!ReportingWire.Version("0.2.11T") && ReportingWire.DllVersion("0.2.11T"), "Exact public ALB suffix remains DLL-only");
        Check(Metadata(Create(version: "2.6 beta 4")).Version == "2.6-beta.4", "Public qualified version");
        foreach (string label in new[] { "1.2.3 private-build", "1.2.3+private", "0.0.0", "0.0.0-beta.1", "1.1.6-2022private", "0.2.11T-private" })
        {
            var result = Metadata(Create(version: label));
            Check(result.Product == ReportingDllProduct.Vatefs && result.Version is null && result.FileVersion == "9.8.7.6" &&
                result.Source == ReportingDllVersionSource.Unknown, "Proven unsupported registration must not become a stable resource version");
        }
        Console.WriteLine("PASS registration identity/version arguments override independent Windows resource versions conservatively");

        Check(Read(Create(pattern: Pattern.PointerSlot)) == new ReportingPluginRegistration(ReportingDllProduct.Vatefs, "0.0.5"), "Initialized data pointer");
        Check(Read(Create("VCH", "0.8.6", Pattern.SmallString)) == new ReportingPluginRegistration(ReportingDllProduct.Vch, "0.8.6"), "Initialized MSVC inline strings");
        var frame = Create(code: Function(beforeCall: [0x89, 0x7d, 0xf0, 0xc7, 0x45, 0xec, 0, 0, 0, 0]));
        Check(Read(frame)?.Version == "0.0.5", "Frame stores outside pending arguments are harmless");
        var seh = new byte[] { 0x55, 0x8b, 0xec, 0x6a, 0xff, 0x68, 0, 0, 0, 0, 0x64, 0xa1, 0, 0, 0, 0, 0x50,
            0x81, 0xec, 0x2c, 1, 0, 0, 0xa1, 0, 0, 0, 0, 0x33, 0xc5, 0x89, 0x45, 0xf0, 0x56, 0x57, 0x50,
            0x8d, 0x45, 0xf4, 0x64, 0xa3, 0, 0, 0, 0, 0x8b, 0xf9, 0x89, 0xbd, 0xfc, 0xfe, 0xff, 0xff };
        Check(Read(Create(pattern: Pattern.PointerSlot, code: Function(Pattern.PointerSlot, prefix: seh)))?.Version == "0.0.5", "Exception frame prefix");
        var badPointer = Create(pattern: Pattern.PointerSlot);
        Put32(badPointer, DataOffset(badPointer) + 0x400, 0xffffffff);
        Check(Read(badPointer) is null, "Uninitialized pointer rejected");
        foreach (Action<byte[]> mutate in new Action<byte[]>[] {
            image => Put32(image, DataOffset(image) + 0x520 + 20, 16),
            image => Put32(image, DataOffset(image) + 0x520 + 16, 7),
            image => Put32(image, DataOffset(image) + 0x520 + 16, 0),
            image => { int at = Find(image, [0x83, 0x3d], 512); Put32(image, at + 2, VersionObject + 16); },
            image => { int at = Find(image, [0x0f, 0x47, 0x0d], 512); Put32(image, at + 3, NameObject); }
        })
        {
            var image = Create("VCH", "0.8.6", Pattern.SmallString);
            mutate(image);
            Check(Read(image) is null, "Unproven pointer/string object rejected");
        }
        Console.WriteLine("PASS literal, pointer-slot and exact inline-string constructor forms require initialized bounded values");

        foreach (byte[] interruption in new byte[][] {
            [0xeb, 0], [0xe8, 0, 0, 0, 0], [0x58], [0x8b, 0xe0],
            [0x89, 0x45, 0xb4], [0xc7, 0x45, 0xb5, 0, 0, 0, 0],
            [0x89, 0x85, 0xb4, 0xff, 0xff, 0xff]
        }) Check(Read(Create(code: Function(beforeCall: interruption))) is null, "Branches, unknown effects or argument overwrite must fail closed");
        var fakePrologue = Function();
        var fakeCode = new byte[fakePrologue.Length + 15]; Array.Fill(fakeCode, (byte)0x90);
        fakeCode[0] = 0x55; fakeCode[1] = 0x8b; fakeCode[2] = 0xec; fakeCode[14] = 0xb8;
        fakePrologue.CopyTo(fakeCode, 15);
        Check(Read(Create(code: fakeCode)) is null, "Prologue bytes in another instruction are not a function entry");
        var two = new byte[256]; Array.Fill(two, (byte)0xcc);
        Function().CopyTo(two, 0); Function().CopyTo(two, 128);
        Check(Read(Create(code: two))?.Version == "0.0.5", "Matching constructors agree");
        Function(version: PluginRegistrationFixture.Version + 32).CopyTo(two, 128);
        var conflict = Create(code: two); Text(conflict, DataOffset(conflict) + 0x320, "0.0.6");
        Check(Read(conflict) is null, "Contradictory constructors rejected");
        Function(beforeCall: [0xeb, 0]).CopyTo(two, 128);
        Check(Read(Create(code: two)) is null, "Unresolved second constructor cannot be ignored");
        Console.WriteLine("PASS constructor arguments respect instruction boundaries, frame writes and all matching callsites");

        foreach (Action<byte[]> mutate in new Action<byte[]>[] {
            image => Text(image, DataOffset(image) + 0x100, "UnrelatedLibrary.dll"),
            image => Text(image, DataOffset(image) + 0xa2, "??0CPlugIn@EuroScopePlugIn@@QAE@ABV01@@Z"),
            image => Put32(image, DataOffset(image) + 16, DataRva + 0x84),
            image => Put16(image, 0x84, 0x8664),
            image => Put32(image, 0x178 + 36, 0x40000020),
            image => Put32(image, 0x1c8 + 12, 0x1000),
            image => Put32(image, 0x1c8 + 20, 512),
            image => Put32(image, 0x1c8 + 20, 0xffffff00),
            image => Put32(image, 0x1c8 + 8, 0x250),
            image => Put32(image, 0x98 + 104, 0xfffffff0),
            image => Put32(image, 0x98 + 108, 20),
            image => Array.Fill(image, (byte)'x', DataOffset(image) + 0x300, 65),
            image => image[DataOffset(image) + 0x303] = 0x0a,
            image => Put32(image, DataOffset(image) + 0x60, 0x80000001)
        })
        {
            var image = Create(); mutate(image);
            Check(Read(image) is null, "PE/import/data boundary validation");
        }
        Check(Read([0x4d, 0x5a]) is null, "Truncated PE");
        var fallback = Metadata(Create(code: Function(beforeCall: [0xeb, 0])));
        Check(fallback.Source == ReportingDllVersionSource.Resource && fallback.Version == "9.8.7.6", "Unsupported code layout retains metadata fallback");
        Console.WriteLine("PASS exact constructor import identity and PE/data bounds reject malformed or unrelated evidence");

        string vatEfs = Path.Combine(fixture.Root, "ConfiguredVatEfs"); Directory.CreateDirectory(vatEfs);
        string dll = Path.Combine(vatEfs, "VatEFS.dll");
        File.WriteAllBytes(dll, Create());
        var summary = ReportingInventoryCollector.Collect([], null, null, vatEfs).Single(item => item.Component == ReportingInventoryComponent.VatefsPlugin);
        Check(summary.Status == ReportingInventoryStatus.Known && summary.Version == "0.0.5", "Legacy numeric version remains known");
        File.WriteAllBytes(dll, Create(version: "1.1.6-2022wb"));
        summary = ReportingInventoryCollector.Collect([], null, null, vatEfs).Single(item => item.Component == ReportingInventoryComponent.VatefsPlugin);
        var detailed = ReportingInventoryCollector.CollectDllInventory(null, null, vatEfs);
        Check(summary.Status == ReportingInventoryStatus.Unknown && summary.Version is null, "DLL-only version does not invalidate legacy schema");
        Check(detailed.Entries.Single().Version == "1.1.6-2022wb" && detailed.Entries.Single().VersionSource == ReportingDllVersionSource.Registration &&
            ReportingWire.ValidDllInventory(detailed), "Detailed snapshot retains its supported registered DLL version");
        Console.WriteLine("PASS legacy inventory and detailed DLL versions retain their independent schema contracts");
        return 5;
    }

    private static int Find(byte[] source, byte[] pattern, int start)
    {
        for (int index = start; index <= source.Length - pattern.Length; index++)
            if (source.AsSpan(index, pattern.Length).SequenceEqual(pattern)) return index;
        throw new Exception("Synthetic instruction missing.");
    }
    private static void Check(bool value, string reason) { if (!value) throw new Exception("Synthetic registration assertion: " + reason); }
}
