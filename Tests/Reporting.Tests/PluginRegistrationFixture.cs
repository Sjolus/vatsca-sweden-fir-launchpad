using System.Buffers.Binary;
using System.Text;

// Synthetic PE32 images containing inert MSVC-style constructor instructions.
// These files have no executable test entry point and are only read as bytes.
internal static class PluginRegistrationFixture
{
    internal const uint Base = 0x400000, DataRva = 0x3000, DataAddress = Base + DataRva;
    internal const uint Iat = DataAddress + 0x80, Name = DataAddress + 0x200, Version = DataAddress + 0x300;
    internal const uint Pointer = DataAddress + 0x400, NameObject = DataAddress + 0x500, VersionObject = DataAddress + 0x520;
    internal enum Pattern { Literal, PointerSlot, SmallString }

    internal static byte[] Create(string name = "VatEFS", string version = "0.0.5", Pattern pattern = Pattern.Literal,
        string resource = "9.8.7.6", byte[]? code = null)
    {
        var original = InventoryPeFixture.Create(resource, resource);
        var image = new byte[original.Length + 2048]; original.CopyTo(image, 0);
        Put16(image, 0x86, 3); Put32(image, 0x98 + 56, 0x4000);
        Put32(image, 0x98 + 104, DataRva); Put32(image, 0x98 + 108, 40);
        Put32(image, 0x178 + 8, 512);
        const int header = 0x1c8;
        Encoding.ASCII.GetBytes(".rdata").CopyTo(image, header);
        Put32(image, header + 8, 2048); Put32(image, header + 12, DataRva);
        Put32(image, header + 16, 2048); Put32(image, header + 20, (uint)original.Length);
        Put32(image, header + 36, 0x40000040);
        int data = original.Length;
        Put32(image, data, DataRva + 0x60); Put32(image, data + 12, DataRva + 0x100); Put32(image, data + 16, DataRva + 0x80);
        Put32(image, data + 0x60, DataRva + 0xa0); Put32(image, data + 0x80, DataRva + 0xa0);
        Text(image, data + 0xa2, "??0CPlugIn@EuroScopePlugIn@@QAE@HPBD000@Z");
        Text(image, data + 0x100, "EuroScopePlugInDll.dll");
        Text(image, data + 0x200, name); Text(image, data + 0x300, version); Put32(image, data + 0x400, Version);
        if (pattern == Pattern.SmallString)
        {
            Inline(image, data + 0x500, name); Inline(image, data + 0x520, version);
        }
        Array.Fill(image, (byte)0xcc, 512, 512);
        (code ?? Function(pattern)).CopyTo(image, 512);
        return image;
    }

    internal static byte[] Function(Pattern pattern = Pattern.Literal, byte[]? beforeCall = null, uint version = Version,
        byte[]? prefix = null)
    {
        var code = new List<byte>();
        code.AddRange(prefix ?? [0x55, 0x8b, 0xec, 0x83, 0xec, 0x40]);
        Push(code, 0); Push(code, 0);
        if (pattern == Pattern.PointerSlot) { code.AddRange([0xff, 0x35]); U32(code, Pointer); }
        else if (pattern == Pattern.SmallString)
        {
            // ecx = version.c_str(), eax = name.c_str().
            Small(code, 1, VersionObject); code.Add(0x51);
        }
        else Push(code, version);
        if (pattern == Pattern.SmallString) { Small(code, 0, NameObject); code.Add(0x50); }
        else Push(code, Name);
        code.AddRange([0x6a, 0x10]);
        if (beforeCall is not null) code.AddRange(beforeCall);
        code.AddRange([0xff, 0x15]); U32(code, Iat); code.Add(0xc3);
        return code.ToArray();
    }

    private static void Small(List<byte> code, int register, uint address)
    {
        code.Add((byte)(0xb8 + register)); U32(code, address);
        code.AddRange([0x83, 0x3d]); U32(code, address + 20); code.Add(15);
        code.AddRange([0x0f, 0x47, (byte)(0x05 + register * 8)]); U32(code, address);
    }
    internal static int DataOffset(byte[] image) => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(0x1c8 + 20)));
    internal static void Text(byte[] bytes, int offset, string value) => Encoding.ASCII.GetBytes(value + '\0').CopyTo(bytes, offset);
    internal static void Put16(byte[] bytes, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), value);
    internal static void Put32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void Inline(byte[] bytes, int offset, string value)
    {
        if (value.Length is < 1 or > 15) throw new ArgumentException("Synthetic inline strings must fit.");
        Text(bytes, offset, value); Put32(bytes, offset + 16, (uint)value.Length); Put32(bytes, offset + 20, 15);
    }
    private static void U32(List<byte> code, uint value) { var bytes = new byte[4]; Put32(bytes, 0, value); code.AddRange(bytes); }
    private static void Push(List<byte> code, uint value) { code.Add(0x68); U32(code, value); }
}
