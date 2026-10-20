using System.Buffers.Binary;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Text;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

internal sealed record ReportingPluginRegistration(ReportingDllProduct Product, string? Version);

// Reads initialized arguments to the imported EuroScope CPlugIn constructor.
// This is a deliberately small MSVC x86 recognizer, not a disassembler or a
// claim about code that runs later. Unsupported layouts retain the metadata
// fallback. No plugin, import, initializer or entry point is ever executed.
internal static class ReportingPluginRegistrationReader
{
    private const string Module = "EuroScopePlugInDll.dll";
    private const string Constructor = "??0CPlugIn@EuroScopePlugIn@@QAE@HPBD000@Z";
    private const int MaximumCodeBytes = 16 * 1024 * 1024, MaximumDataBytes = 8 * 1024 * 1024;
    private const int MaximumCallsites = 16, MaximumInstructions = 160, MaximumFunctionPrefix = 512;

    internal static ReportingPluginRegistration? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 64L * 1024 * 1024) return null;
            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
            var image = Image.Read(pe, stream);
            if (image is null) return null;
            var slots = image.ConstructorSlots();
            if (slots is null || slots.Count == 0) return null;
            (string Name, string Version)? agreed = null;
            int callsites = 0;
            foreach (var section in image.Code)
            {
                var bytes = section.Bytes;
                for (int offset = 0; offset <= bytes.Length - 6; offset++)
                {
                    if (bytes[offset] != 0xff || bytes[offset + 1] != 0x15 || !slots.Contains(U32(bytes, offset + 2))) continue;
                    if (++callsites > MaximumCallsites) return null;
                    // Starting at a recognized frame prologue establishes the
                    // instruction boundaries; a byte match inside an operand
                    // is not a constructor call. Every instruction must decode.
                    (string Name, string Version)? found = null;
                    for (int start = Math.Max(0, offset - MaximumFunctionPrefix); start + 3 <= offset; start++)
                    {
                        if (bytes[start] != 0x55 || bytes[start + 1] != 0x8b || bytes[start + 2] != 0xec) continue;
                        // Supported MSVC functions begin at the section or an
                        // aligned CC-padding boundary. This is a conservative
                        // layout recognizer, not a proof of runtime reachability.
                        if (start != 0 && (((section.Rva + start) & 15) != 0 || bytes[start - 1] != 0xcc)) continue;
                        var candidate = Arguments(image, bytes, start + 3, offset);
                        if (candidate is null) continue;
                        if (found is not null && found != candidate) return null;
                        found = candidate;
                    }
                    if (found is null || (agreed is not null && agreed != found)) return null;
                    agreed = found;
                }
            }
            if (agreed is null) return null;
            var value = agreed.Value;
            string? version = ReportingInventoryCollector.NormalizePluginVersion(value.Version);
            if (version is null && ReportingWire.DllVersion(value.Version)) version = value.Version;
            if (version is not null && !version.Split('-')[0].Any(c => c is >= '1' and <= '9')) version = null;
            return new(Product(value.Name), version);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException or
            ArgumentException or NotSupportedException or OverflowException or System.Security.SecurityException)
        { return null; }
    }

    private static ReportingDllProduct Product(string name) => name switch
    {
        "Audio for Vatsim EuroScope Bridge" => ReportingDllProduct.AfvEuroscopeBridge,
        "CDM Plugin" => ReportingDllProduct.Cdm,
        "VATCAN Bookings" => ReportingDllProduct.VatcanBookings,
        "Arrival Load Balance" => ReportingDllProduct.ArrivalLoadBalancer,
        "VatIRIS" => ReportingDllProduct.Vatiris,
        "VatEFS" => ReportingDllProduct.Vatefs,
        "VCH" => ReportingDllProduct.Vch,
        "SAAMAN" => ReportingDllProduct.Saaman,
        _ => ReportingDllProduct.Unknown
    };

    private static (string Name, string Version)? Arguments(Image image, byte[] code, int start, int call)
    {
        var registers = new uint?[8];
        var stack = new List<(uint? Value, int Offset)>();
        uint? comparedCapacity = null;
        int position = start, instructions = 0, stackOffset = 0;
        bool Has(int size) => size >= 0 && position <= call - size;
        bool Push(uint? value) { stackOffset -= 4; stack.Add((value, stackOffset)); return stack.Count <= 40; }
        while (position < call)
        {
            if (++instructions > MaximumInstructions) return null;
            byte opcode = code[position];
            if (opcode == 0x90) { position++; continue; }
            if (opcode == 0x68 && Has(5)) { if (!Push(U32(code, position + 1))) return null; position += 5; continue; }
            if (opcode == 0x6a && Has(2)) { if (!Push(unchecked((uint)(sbyte)code[position + 1]))) return null; position += 2; continue; }
            if (opcode is >= 0x50 and <= 0x57) { if (!Push(registers[opcode - 0x50])) return null; position++; continue; }
            if (opcode is >= 0xb8 and <= 0xbf && Has(5))
            {
                int register = opcode - 0xb8;
                if (register is 4 or 5) return null;
                registers[register] = U32(code, position + 1); position += 5; continue;
            }
            // The observed exception-frame prologues access fs:[0]. Treat its
            // contents as unknown; it is neither PE data nor a string source.
            if (opcode == 0x64 && Has(6) && code[position + 1] is 0xa1 or 0xa3 && U32(code, position + 2) == 0)
            { if (code[position + 1] == 0xa1) registers[0] = null; position += 6; continue; }
            if (opcode == 0xa1 && Has(5))
            { registers[0] = image.Data32(U32(code, position + 1)); position += 5; continue; }
            if (opcode == 0xff && Has(6) && code[position + 1] == 0x35)
            { if (!Push(image.Data32(U32(code, position + 2)))) return null; position += 6; continue; }
            if (opcode is 0x81 or 0x83 && Has(opcode == 0x81 ? 6 : 3) && code[position + 1] == 0xec)
            {
                uint amount = opcode == 0x81 ? U32(code, position + 2) : code[position + 2];
                if (amount > 1024 * 1024 || (opcode == 0x83 && amount > 127)) return null;
                stackOffset -= (int)amount;
                stack.Clear(); comparedCapacity = null; position += opcode == 0x81 ? 6 : 3; continue;
            }
            // MSVC std::string::c_str(): compare this exact object's capacity,
            // conditionally select its pointer, then push that register. Only
            // an initialized 15-byte inline string is resolved. Heap/dynamic
            // objects remain unknown, even if a nearby literal looks plausible.
            if (opcode == 0x83 && Has(7) && code[position + 1] == 0x3d && code[position + 6] == 15)
            { comparedCapacity = U32(code, position + 2); position += 7; continue; }
            if (opcode == 0x0f && Has(7) && code[position + 1] == 0x47 && (code[position + 2] & 0xc7) == 0x05)
            {
                int register = (code[position + 2] >> 3) & 7;
                if (register is 4 or 5) return null;
                uint address = U32(code, position + 3);
                registers[register] = registers[register] == address && comparedCapacity == (ulong)address + 20 && image.InlineString(address)
                    ? address : null;
                position += 7; continue;
            }
            if (opcode is 0x8b or 0x89 or 0x8d or 0x33 or 0xc7 && Has(2))
            {
                byte modrm = code[position + 1]; int mode = modrm >> 6, register = (modrm >> 3) & 7, operand = modrm & 7;
                if (mode == 3 && opcode is 0x8b or 0x89 or 0x33)
                {
                    int target = opcode == 0x89 ? operand : register, source = opcode == 0x89 ? register : operand;
                    if (target is 4 or 5) return null;
                    registers[target] = opcode == 0x33 ? target == source ? 0u : null : registers[source];
                    if (opcode == 0x33) comparedCapacity = null;
                    position += 2; continue;
                }
                if (mode == 0 && operand == 5 && opcode is 0x8b or 0x8d && Has(6))
                {
                    if (register is 4 or 5) return null;
                    uint address = U32(code, position + 2);
                    registers[register] = opcode == 0x8d ? address : image.Data32(address);
                    position += 6; continue;
                }
                // Frame-local stores do not modify argument values or image
                // data. Loads/addresses from the frame cannot prove a string.
                if (operand == 5 && mode is 1 or 2 && opcode is 0x89 or 0x8b or 0x8d or 0xc7)
                {
                    int size = mode == 1 ? 3 : 6;
                    if (opcode == 0xc7) { if (register != 0) return null; size += 4; }
                    if (!Has(size)) return null;
                    if (opcode is 0x89 or 0xc7)
                    {
                        int displacement = mode == 1 ? (sbyte)code[position + 2] : unchecked((int)U32(code, position + 2));
                        if (stack.Any(slot => (long)displacement < (long)slot.Offset + 4 && (long)displacement + 4 > slot.Offset)) return null;
                    }
                    if (opcode is 0x8b or 0x8d) { if (register is 4 or 5) return null; registers[register] = null; }
                    position += size; continue;
                }
            }
            // Branches, calls, pops, arbitrary writes and unknown encodings
            // cannot be skipped while reconstructing constructor arguments.
            return null;
        }
        if (position != call || stack.Count < 5 || stack[^1].Value != 0x10 || stack[^2].Value is not uint nameAddress || stack[^3].Value is not uint versionAddress) return null;
        string? name = image.DataString(nameAddress, 128), version = image.DataString(versionAddress, 64);
        return string.IsNullOrEmpty(name) || string.IsNullOrEmpty(version) ? null : (name, version);
    }

    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private sealed record Section(uint Rva, uint Size, byte[] Bytes);
    private sealed class Image(uint imageBase, uint importsRva, uint importsSize, List<Section> code, List<Section> data)
    {
        internal IReadOnlyList<Section> Code => code;
        internal static Image? Read(PEReader pe, FileStream stream)
        {
            var headers = pe.PEHeaders; var optional = headers.PEHeader;
            if (optional is null || optional.Magic != PEMagic.PE32 || headers.CoffHeader.Machine != Machine.I386 ||
                !headers.CoffHeader.Characteristics.HasFlag(Characteristics.Dll) || headers.SectionHeaders.Length is < 1 or > 96 ||
                optional.ImageBase is 0 or > uint.MaxValue || optional.SizeOfHeaders <= 0 || optional.SizeOfHeaders > stream.Length || optional.SizeOfImage <= 0) return null;
            var code = new List<Section>(); var data = new List<Section>();
            var rawRanges = new List<(long Start, long End)>(); var virtualRanges = new List<(long Start, long End)>();
            int codeBytes = 0, dataBytes = 0;
            foreach (var section in headers.SectionHeaders)
            {
                if (section.VirtualAddress < optional.SizeOfHeaders || section.VirtualSize < 0 || section.SizeOfRawData < 0 || section.PointerToRawData < 0) return null;
                long end = (long)section.PointerToRawData + section.SizeOfRawData;
                long virtualEnd = (long)section.VirtualAddress + Math.Max(section.VirtualSize, section.SizeOfRawData);
                if (virtualEnd > optional.SizeOfImage || (ulong)optional.ImageBase + (ulong)virtualEnd > uint.MaxValue ||
                    section.SizeOfRawData > 0 && (section.PointerToRawData < optional.SizeOfHeaders || end > stream.Length)) return null;
                if (section.SizeOfRawData > 0)
                {
                    if (rawRanges.Any(range => section.PointerToRawData < range.End && end > range.Start)) return null;
                    rawRanges.Add((section.PointerToRawData, end));
                }
                if (virtualEnd > section.VirtualAddress)
                {
                    if (virtualRanges.Any(range => section.VirtualAddress < range.End && virtualEnd > range.Start)) return null;
                    virtualRanges.Add((section.VirtualAddress, virtualEnd));
                }
                bool executable = section.SectionCharacteristics.HasFlag(SectionCharacteristics.MemExecute);
                bool isCode = executable && section.SectionCharacteristics.HasFlag(SectionCharacteristics.ContainsCode);
                bool isData = !executable && section.SectionCharacteristics.HasFlag(SectionCharacteristics.ContainsInitializedData) &&
                    section.Name is not (".rsrc" or ".reloc" or ".pdata" or ".debug");
                if (!isCode && !isData) continue;
                if (isCode) { if (section.SizeOfRawData > MaximumCodeBytes - codeBytes) return null; codeBytes += section.SizeOfRawData; }
                else { if (section.SizeOfRawData > MaximumDataBytes - dataBytes) return null; dataBytes += section.SizeOfRawData; }
                int size = Math.Min(section.VirtualSize, section.SizeOfRawData);
                var bytes = new byte[size]; stream.Position = section.PointerToRawData; stream.ReadExactly(bytes);
                (isCode ? code : data).Add(new((uint)section.VirtualAddress, (uint)size, bytes));
            }
            return new((uint)optional.ImageBase, checked((uint)optional.ImportTableDirectory.RelativeVirtualAddress),
                checked((uint)optional.ImportTableDirectory.Size), code, data);
        }

        internal HashSet<uint>? ConstructorSlots()
        {
            if (importsRva == 0 || importsSize < 20 || importsSize > 128 * 20) return null;
            var slots = new HashSet<uint>(); int thunks = 0;
            for (uint offset = 0; offset <= importsSize - 20; offset += 20)
            {
                var descriptor = DataRva(checked(importsRva + offset), 20);
                if (descriptor is null) return null;
                uint lookup = U32(descriptor, 0), name = U32(descriptor, 12), first = U32(descriptor, 16);
                if (descriptor.All(value => value == 0)) return slots;
                string? module = StringRva(name, 64);
                if (module is null) return null;
                if (!module.Equals(Module, StringComparison.OrdinalIgnoreCase)) continue;
                if (first == 0) return null;
                if (lookup == 0) lookup = first;
                bool terminated = false;
                for (uint index = 0; index < 4096; index++)
                {
                    if (++thunks > 4096) return null;
                    var thunk = DataRva(checked(lookup + index * 4), 4);
                    if (thunk is null) return null;
                    uint entry = U32(thunk, 0);
                    if (entry == 0) { terminated = true; break; }
                    if ((entry & 0x80000000) != 0) continue;
                    string? symbol = StringRva(checked(entry + 2), 128);
                    if (symbol is null) return null;
                    if (symbol != Constructor) continue;
                    uint slot = checked(first + index * 4);
                    if (DataRva(slot, 4) is null || !slots.Add(checked(imageBase + slot)) || slots.Count > 8) return null;
                }
                if (!terminated) return null;
            }
            return null; // A bounded, terminated import table is required.
        }

        internal uint? Data32(uint address)
        {
            if (address < imageBase) return null;
            var bytes = DataRva(address - imageBase, 4);
            return bytes is null ? null : U32(bytes, 0);
        }
        internal string? DataString(uint address, int maximum) => address < imageBase ? null : StringRva(address - imageBase, maximum);
        internal bool InlineString(uint address)
        {
            if (address < imageBase) return false;
            var bytes = DataRva(address - imageBase, 24);
            if (bytes is null || U32(bytes, 20) != 15) return false;
            uint length = U32(bytes, 16);
            return length is > 0 and <= 15 && bytes[length] == 0 && bytes.Take((int)length).All(value => value is >= 0x20 and <= 0x7e);
        }
        private byte[]? DataRva(uint rva, int size)
        {
            if (size < 0) return null;
            foreach (var section in data)
                if (rva >= section.Rva && (ulong)rva + (uint)size <= (ulong)section.Rva + section.Size)
                    return section.Bytes.AsSpan(checked((int)(rva - section.Rva)), size).ToArray();
            return null;
        }
        private string? StringRva(uint rva, int maximum)
        {
            foreach (var section in data)
            {
                if (rva < section.Rva || (ulong)rva >= (ulong)section.Rva + section.Size) continue;
                int start = checked((int)(rva - section.Rva)), available = Math.Min(maximum + 1, section.Bytes.Length - start);
                for (int length = 0; length < available; length++)
                {
                    byte value = section.Bytes[start + length];
                    if (value == 0) return Encoding.ASCII.GetString(section.Bytes, start, length);
                    if (value is < 0x20 or > 0x7e) return null;
                }
            }
            return null;
        }
    }
}
