using System.Diagnostics;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

internal sealed record ReportingPluginMetadata(ReportingDllProduct Product, string? Version,
    string? FileVersion, ReportingDllVersionSource Source);

// Identification is separate from discovery: unrecognised DLLs remain in the
// inventory. Only fixed public identities and bounded versions leave this reader.
internal static class ReportingPluginVersionReader
{
    private const int MaximumDataBytes = 8 * 1024 * 1024;

    internal static ReportingPluginMetadata Read(string path, FileVersionInfo info)
    {
        var product = Product(info.ProductName);
        string? resource = ReportingInventoryCollector.PluginVersion(info);
        string? fileVersion = ReportingInventoryCollector.NormalizePluginVersion(info.FileVersion);
        if (fileVersion is not null && !fileVersion.Split('-')[0].Any(c => c is >= '1' and <= '9')) fileVersion = null;
        var registration = ReportingPluginRegistrationReader.Read(path);
        if (registration is not null)
        {
            if (registration.Product != ReportingDllProduct.Unknown) product = registration.Product;
            // A proven but unsupported registration label is unknown, not a
            // stable release inferred from VERSIONINFO. Unresolved code layouts
            // still use the independent metadata/embedded-label fallback below.
            return new(product, registration.Version, fileVersion, registration.Version is not null
                ? ReportingDllVersionSource.Registration : ReportingDllVersionSource.Unknown);
        }
        // These products publish a distinctive complete beta label in initialized
        // PE data. Do not reinterpret their Windows resource numbering scheme.
        string? embedded = product is ReportingDllProduct.Topsky or ReportingDllProduct.GroundRadar
            ? ReadQualifiedLabel(path) : null;
        return new(product, embedded ?? resource, fileVersion,
            embedded is not null ? ReportingDllVersionSource.Embedded : resource is not null
                ? ReportingDllVersionSource.Resource : ReportingDllVersionSource.Unknown);
    }

    internal static ReportingPluginMetadata RecognizeFingerprint(ReportingPluginMetadata metadata, string sha256)
    {
        // Exact public binaries provide identity when their VERSIONINFO is empty
        // or still contains a template name. This table never filters discovery
        // and never substitutes a package release for the DLL's own version.
        var product = sha256 switch
        {
            // https://github.com/Vatsim-Scandinavia/ESAA-Sectorfile/tree/b4ff5fb8c62924007dda28149d70dcf735cc9c89/Plugins
            "c52e88db309d4af2af4b580273f0b2b4db173335c2e15906cf3325feb45f0735" => ReportingDllProduct.Vch,
            "f7fb42d454c366badfb4a333968fe5218ad536d90d76409c71b58605cc7d4d09" => ReportingDllProduct.Vatiris,
            "3285882dcf3b6e677feb6ab66afb88e2e3fb5c910d09462d64513fad0a042d7c" => ReportingDllProduct.VatcanBookings,
            "bf51c081ec59f9a92cf570894e9617222e49a3645fec9a07ae270402f302aa80" => ReportingDllProduct.Cdm,
            // https://github.com/AndyTWF/afv-euroscope-bridge/releases/tag/1.3.0
            "e6f248dad87ba2ca3b677fbd2e560958d9b7d7645b79a05d0a90545814f5d43b" => ReportingDllProduct.AfvEuroscopeBridge,
            _ => metadata.Product
        };
        return metadata with { Product = product };
    }

    private static ReportingDllProduct Product(string? value) => value?.Trim() switch
    {
        "TopSky plugin for EuroScope" => ReportingDllProduct.Topsky,
        "Ground Radar plugin for EuroScope" => ReportingDllProduct.GroundRadar,
        "MAESTRO plugin for EuroScope" => ReportingDllProduct.Maestro,
        "RDF Plugin for EuroScope" => ReportingDllProduct.Rdf,
        "Arrival Load Ballancer" or "Arrival Load Balancer" => ReportingDllProduct.ArrivalLoadBalancer,
        _ => ReportingDllProduct.Unknown
    };

    private static string? ReadQualifiedLabel(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 64L * 1024 * 1024) return null;
            using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (!pe.PEHeaders.CoffHeader.Characteristics.HasFlag(System.Reflection.PortableExecutable.Characteristics.Dll) ||
                pe.PEHeaders.SectionHeaders.Length > 96) return null;
            var labels = new HashSet<string>(StringComparer.Ordinal);
            int remaining = MaximumDataBytes;
            foreach (var section in pe.PEHeaders.SectionHeaders)
            {
                // Compiler-initialized strings live in .data/.rdata; never scan
                // instructions, resource text, debug directories or appended data.
                if (section.Name is not (".data" or ".rdata") ||
                    !section.SectionCharacteristics.HasFlag(SectionCharacteristics.ContainsInitializedData) ||
                    section.SectionCharacteristics.HasFlag(SectionCharacteristics.MemExecute)) continue;
                int size = section.SizeOfRawData;
                if (size < 0 || size > remaining || section.PointerToRawData < 0 ||
                    (long)section.PointerToRawData + size > stream.Length) return null;
                remaining -= size;
                stream.Position = section.PointerToRawData;
                byte[] bytes = new byte[size]; stream.ReadExactly(bytes);
                for (int start = 0, end = 0; end < bytes.Length; end++)
                {
                    if (bytes[end] != 0) continue;
                    int length = end - start;
                    if (length is >= 10 and <= 48 && bytes[start] is >= (byte)'0' and <= (byte)'9')
                    {
                        string text = Encoding.ASCII.GetString(bytes, start, length);
                        var match = Regex.Match(text, @"\A([0-9]{1,3}\.[0-9]{1,3}(?:\.[0-9]{1,3})?) (alpha|beta|rc) ([0-9]{1,3})\z",
                            RegexOptions.CultureInvariant);
                        if (match.Success)
                            labels.Add($"{match.Groups[1].Value}-{match.Groups[2].Value}.{match.Groups[3].Value}");
                        if (labels.Count > 1) return null;
                    }
                    start = end + 1;
                }
            }
            return labels.SingleOrDefault();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException or
            ArgumentException or NotSupportedException or System.Security.SecurityException)
        { return null; }
    }
}
