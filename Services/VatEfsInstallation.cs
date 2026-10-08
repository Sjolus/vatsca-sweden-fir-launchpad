using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace VatscaUpdateChecker.Services;

internal sealed record VatEfsInstallation(bool PluginFound, string? Version, string Detail);

internal sealed record VatEfsMsiRegistration(string ProductCode, string Name, string Publisher,
    string Version, string? ReadmeComponentPath);

/// <summary>Reads the registered package version without loading a plugin or starting its backend.</summary>
internal static class VatEfsInstallationProbe
{
    internal const string UpgradeCode = "{F3D63D12-9678-4E60-B1C1-FFC0590FEF01}";
    internal const string ReadmeComponent = "{12345678-1234-1234-1234-222222222222}";
    private const int MachineContext = 4;
    private const int MaximumProducts = 32;
    private const string UnknownVersion = "Plugin found. Its VatEFS package version could not be identified; check the release notes before updating.";

    internal static VatEfsInstallation Read(string configuredFolder) =>
        Read(configuredFolder, ReadRegistrations, File.Exists, HasNoReparsePoints);

    internal static VatEfsInstallation Read(string configuredFolder,
        Func<IReadOnlyList<VatEfsMsiRegistration>> registrations,
        Func<string, bool> fileExists, Func<string, bool> unredirectedPath)
    {
        if (string.IsNullOrWhiteSpace(configuredFolder))
            return new(false, null, "Choose the folder containing VatEFS.dll in App settings to identify your installation.");

        bool found = false;
        try
        {
            string root = NormalizeFolder(configuredFolder);
            string plugin = Path.Combine(root, "VatEFS.dll");
            found = fileExists(plugin);
            if (!found) return new(false, null, "VatEFS.dll was not found in the configured folder.");

            string backend = Path.Combine(root, "efs.exe");
            if (!fileExists(backend) || !unredirectedPath(plugin) || !unredirectedPath(backend))
                return new(true, null, UnknownVersion);

            var products = registrations();
            if (products.Count > MaximumProducts) return new(true, null, UnknownVersion);
            var matches = new List<VatEfsMsiRegistration>();
            foreach (var product in products)
            {
                if (string.IsNullOrWhiteSpace(product.ReadmeComponentPath)
                    || !Path.IsPathFullyQualified(product.ReadmeComponentPath)
                    || !string.Equals(Path.GetFileName(product.ReadmeComponentPath), "README.txt", StringComparison.OrdinalIgnoreCase))
                    continue;

                string readme = Path.GetFullPath(product.ReadmeComponentPath);
                if (!string.Equals(NormalizeFolder(Path.GetDirectoryName(readme)!), root, StringComparison.OrdinalIgnoreCase)
                    || !fileExists(readme) || !unredirectedPath(readme))
                    continue;
                if (!Guid.TryParseExact(product.ProductCode, "B", out _)
                    || !string.Equals(product.Name, "VatEFS", StringComparison.Ordinal)
                    || !string.Equals(product.Publisher, "Martin Insulander", StringComparison.Ordinal)
                    || !IsPackageVersion(product.Version))
                    return new(true, null, UnknownVersion);
                matches.Add(product);
            }

            return matches.Count == 1
                ? new(true, matches[0].Version, "Version read from the Windows Installer registration for this VatEFS folder.")
                : new(true, null, UnknownVersion);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException
            or ArgumentException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            return new(found, null, found
                ? "Plugin found, but its Windows Installer registration could not be read. The installed VatEFS package version is unknown."
                : "The configured VatEFS folder could not be checked. Review its path in App settings.");
        }
    }

    private static bool IsPackageVersion(string value) => value.Length <= 32
        && SoftwareVersion.TryParse(value, out var version) && !version.IsPrerelease && !value.Contains('+')
        && System.Version.TryParse(value, out var windowsVersion) && windowsVersion.Revision == -1
        && windowsVersion.Major <= 255 && windowsVersion.Minor <= 255 && windowsVersion.Build <= 65535;

    private static string NormalizeFolder(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new ArgumentException("An ordinary absolute installation folder is required.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static bool HasNoReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }

    internal static IReadOnlyList<VatEfsMsiRegistration> ReadRegistrations()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var products = new List<VatEfsMsiRegistration>();
        for (uint index = 0; index <= MaximumProducts; index++)
        {
            var code = new StringBuilder(39);
            uint result = MsiEnumRelatedProductsW(UpgradeCode, 0, index, code);
            if (result == 259) return products; // ERROR_NO_MORE_ITEMS
            if (result != 0 || index == MaximumProducts)
                throw new IOException("VatEFS Windows Installer registration could not be enumerated.");

            string product = code.ToString();
            string name = Property(product, "InstalledProductName");
            string publisher = Property(product, "Publisher");
            string version = Property(product, "VersionString");
            var path = new StringBuilder(32768);
            uint length = (uint)path.Capacity;
            int state = MsiGetComponentPathExW(product, ReadmeComponent, null, MachineContext, path, ref length);
            products.Add(new(product, name, publisher, version, state == 3 ? path.ToString() : null));
        }
        throw new IOException("VatEFS Windows Installer registration could not be enumerated.");
    }

    private static string Property(string product, string property)
    {
        var value = new StringBuilder(512);
        uint length = (uint)value.Capacity;
        if (MsiGetProductInfoExW(product, null, MachineContext, property, value, ref length) != 0)
            throw new IOException("VatEFS Windows Installer registration could not be read.");
        return value.ToString();
    }

    // Queries only: do not use APIs that advertise, configure, repair or provide a component.
    [DllImport("msi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint MsiEnumRelatedProductsW(string upgradeCode, uint reserved, uint index, StringBuilder productCode);
    [DllImport("msi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint MsiGetProductInfoExW(string product, string? sid, int context, string property, StringBuilder value, ref uint length);
    [DllImport("msi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MsiGetComponentPathExW(string product, string component, string? sid, int context, StringBuilder path, ref uint length);
}
