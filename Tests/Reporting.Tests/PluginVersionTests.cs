using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

internal static class PluginVersionTests
{
    internal static int Run()
    {
        using var fixture = new Fixture();
        string path = Path.Combine(fixture.Root, "synthetic-plugin.dll");
        ReportingPluginMetadata Read(byte[] image)
        {
            File.WriteAllBytes(path, image);
            return ReportingPluginVersionReader.Read(path, FileVersionInfo.GetVersionInfo(path));
        }
        var top = Read(Image("TopSky plugin for EuroScope", "2.5.99.4", "2.6 beta 4"));
        Check(top == new ReportingPluginMetadata(ReportingDllProduct.Topsky, "2.6-beta.4", "2.5.99.4", ReportingDllVersionSource.Embedded));
        var ground = Read(Image("Ground Radar plugin for EuroScope", "1.6.0.6", "1.6 beta 6"));
        Check(ground.Product == ReportingDllProduct.GroundRadar && ground.Version == "1.6-beta.6" && ground.FileVersion == "1.6.0.6");
        var duplicate = Read(Image("TopSky plugin for EuroScope", "2.5.99.4", "2.6 beta 4", "2.6 beta 4"));
        Check(duplicate.Version == "2.6-beta.4");
        Console.WriteLine("PASS recognized plugin labels retain separate Windows file versions");

        foreach (var labels in new[] { new[] { "2.6 beta 4", "2.6 beta 3" }, new[] { "prefix 2.6 beta 4" },
            new[] { "2.6 beta 4 private" }, new[] { "2.6 beta 4\n" }, new[] { "2.6 beta4" } })
        {
            var value = Read(Image("TopSky plugin for EuroScope", "2.5.99.4", labels));
            Check(value.Version == "2.5.99.4" && value.Source == ReportingDllVersionSource.Resource);
        }
        var unrelated = Read(Image("Synthetic custom product", "2.5.99.4", "2.6 beta 4"));
        Check(unrelated.Product == ReportingDllProduct.Unknown && unrelated.Source == ReportingDllVersionSource.Resource);
        var instructions = Image("TopSky plugin for EuroScope", "2.5.99.4", "2.6 beta 4");
        Put32(instructions, 0x1c8 + 36, 0x60000040);
        Check(Read(instructions).Source == ReportingDllVersionSource.Resource);
        var malformed = Image("TopSky plugin for EuroScope", "2.5.99.4", "2.6 beta 4");
        Put32(malformed, 0x1c8 + 16, 9 * 1024 * 1024);
        Check(Read(malformed).Source == ReportingDllVersionSource.Resource);
        var overlay = InventoryPeFixture.Create("2.5.99.4", "2.5.99.4", productName: "TopSky plugin for EuroScope")
            .Concat(Encoding.ASCII.GetBytes("\0" + "2.6 beta 4" + "\0")).ToArray();
        Check(Read(overlay).Source == ReportingDllVersionSource.Resource);
        Console.WriteLine("PASS ambiguous, arbitrary, executable and out-of-bounds strings cannot become plugin versions");

        var unknown = new ReportingPluginMetadata(ReportingDllProduct.Unknown, null, null, ReportingDllVersionSource.Unknown);
        var known = ReportingPluginVersionReader.RecognizeFingerprint(unknown,
            "e6f248dad87ba2ca3b677fbd2e560958d9b7d7645b79a05d0a90545814f5d43b");
        Check(known.Product == ReportingDllProduct.AfvEuroscopeBridge && known.Version is null);
        Check(ReportingPluginVersionReader.RecognizeFingerprint(unknown, new string('0', 64)) == unknown);
        Console.WriteLine("PASS official binary identity matching never substitutes a release version");
        return 3;
    }

    private static byte[] Image(string product, string version, params string[] labels)
    {
        byte[] original = InventoryPeFixture.Create(version, version, productName: product);
        byte[] data = Encoding.ASCII.GetBytes("\0" + string.Join('\0', labels) + "\0");
        int rawSize = (data.Length + 511) & ~511;
        var image = new byte[original.Length + rawSize]; original.CopyTo(image, 0);
        Put16(image, 0x86, 3); Put32(image, 0x98 + 56, 0x4000);
        const int section = 0x1c8;
        Encoding.ASCII.GetBytes(".data").CopyTo(image, section);
        Put32(image, section + 8, (uint)data.Length); Put32(image, section + 12, 0x3000);
        Put32(image, section + 16, (uint)rawSize); Put32(image, section + 20, (uint)original.Length);
        Put32(image, section + 36, 0xc0000040); data.CopyTo(image, original.Length);
        return image;
    }
    private static void Check(bool value) { if (!value) throw new Exception("Synthetic plugin version assertion failed."); }
    private static void Put16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    private static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
}
