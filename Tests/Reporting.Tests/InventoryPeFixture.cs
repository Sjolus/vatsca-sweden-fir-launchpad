using System.Buffers.Binary;
using System.Text;

// Inert native DLL images with independent string/fixed VERSIONINFO fields.
// Adapted from the configured-path harness; these bytes are never loaded or run.
internal static class InventoryPeFixture
{
    internal static byte[] Create(string? product = null, string? file = null,
        uint productHigh = 0, uint productLow = 0, uint fileHigh = 0, uint fileLow = 0, uint flags = 0, string? productName = null)
    {
        // FileVersionInfo retries other language tables when FileVersion is
        // absent/empty; ordinary fixtures therefore provide matching strings.
        file ??= product;
        byte[] String(string key, string value) => Block(key, 1,
            Encoding.Unicode.GetBytes(value + '\0'), checked((ushort)(value.Length + 1)));
        var fields = new List<byte[]>();
        if (productName is not null) fields.Add(String("ProductName", productName));
        if (product is not null) fields.Add(String("ProductVersion", product));
        if (file is not null) fields.Add(String("FileVersion", file));
        var fixedInfo = new byte[52];
        Put32(fixedInfo, 0, 0xFEEF04BD); Put32(fixedInfo, 4, 0x10000);
        Put32(fixedInfo, 8, fileHigh); Put32(fixedInfo, 12, fileLow);
        Put32(fixedInfo, 16, productHigh); Put32(fixedInfo, 20, productLow);
        Put32(fixedInfo, 24, 0x3f); Put32(fixedInfo, 28, flags);
        Put32(fixedInfo, 32, 0x40004); Put32(fixedInfo, 36, 2);
        var version = Block("VS_VERSION_INFO", 0, fixedInfo, 52,
            Block("StringFileInfo", 1, [], 0, Block("040904B0", 1, [], 0, fields.ToArray())),
            Block("VarFileInfo", 1, [], 0, Block("Translation", 0, [0x09, 0x04, 0xb0, 0x04], 4)));
        var resource = new byte[88 + version.Length];
        foreach (int offset in new[] { 0, 24, 48 }) Put16(resource, offset + 14, 1);
        Put32(resource, 16, 16); Put32(resource, 20, 0x80000018);
        Put32(resource, 40, 1); Put32(resource, 44, 0x80000030);
        Put32(resource, 64, 0x409); Put32(resource, 68, 72);
        Put32(resource, 72, 0x2058); Put32(resource, 76, (uint)version.Length); Put32(resource, 80, 1200);
        version.CopyTo(resource, 88);
        int rawSize = (resource.Length + 511) & ~511;
        var image = new byte[1024 + rawSize];
        image[0] = (byte)'M'; image[1] = (byte)'Z'; Put32(image, 60, 0x80); Put32(image, 0x80, 0x4550);
        Put16(image, 0x84, 0x14c); Put16(image, 0x86, 2); Put16(image, 0x94, 224); Put16(image, 0x96, 0x2102);
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
        return image;
    }

    private static byte[] Block(string key, ushort type, byte[] value, ushort length, params byte[][] children)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: true);
        writer.Write((ushort)0); writer.Write(length); writer.Write(type); writer.Write(Encoding.Unicode.GetBytes(key + '\0'));
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
