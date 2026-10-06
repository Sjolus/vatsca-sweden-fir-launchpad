using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace VatscaUpdateChecker.Services;

/// <summary>A read-only proof of the registered MSI's actual filesystem destinations.</summary>
internal sealed record EuroScopeMsiFootprint(string ProductCode, string Scope, string InstallRoot,
    string DataRoot, IReadOnlyList<string> ComponentPaths, string Fingerprint)
{
    internal const string DataDirectoryProperty = "_06CD3D45E96241158182F927C827F3F0";
    internal const string UpgradeCode = EuroScopePolicy.UpgradeCode;

    public static EuroScopeMsiFootprint Read(AtcMsiRegistration registration, string installRoot,
        string roamingAppData, string windowsDirectory) => Read(registration, installRoot, roamingAppData,
            windowsDirectory, new EuroScopeMsiFootprintEnvironment());

    internal static EuroScopeMsiFootprint Read(AtcMsiRegistration registration, string installRoot,
        string roamingAppData, string windowsDirectory, EuroScopeMsiFootprintEnvironment environment)
    {
        if (!Guid.TryParseExact(registration.ProductCode, "B", out _) || !registration.WindowsInstaller ||
            registration.Name != "EuroScope" || registration.Publisher != "Gergely Csernák")
            throw new InvalidDataException("The EuroScope MSI identity is not supported.");
        string root = FullPath(installRoot), roaming = FullPath(roamingAppData), windows = FullPath(windowsDirectory);
        string data = Path.Combine(roaming, "EuroScope"), fonts = Path.Combine(windows, "Fonts");
        if (Within(windows, root) || Same(root, roaming))
            throw new InvalidDataException("The EuroScope installation root is not a safe dedicated program folder.");
        foreach (string path in new[] { root, data, fonts }) RejectReparse(path);
        int[] contexts = registration.Scope switch
        {
            "AllUsers" => [4], "CurrentUser" => [1, 2],
            _ => throw new InvalidDataException("The EuroScope MSI installation scope is not supported.")
        };
        string? sid = registration.Scope == "CurrentUser" ? environment.CurrentUserSid() : null;
        if (registration.Scope == "CurrentUser" && string.IsNullOrWhiteSpace(sid))
            throw new InvalidDataException("The current Windows user could not be identified.");
        var candidates = contexts.Select(context => (Context: context,
            Package: environment.ProductProperty(registration.ProductCode, sid, context, "LocalPackage")))
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Package)).ToArray();
        if (candidates.Length != 1)
            throw new InvalidDataException("The exact EuroScope MSI context is missing or ambiguous. Use Windows' installer manually.");
        var selected = candidates[0];
        string cache = FullPath(selected.Package!);
        if (!Within(Path.Combine(windows, "Installer"), cache) || !cache.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The registered EuroScope MSI is outside the Windows Installer cache.");
        RejectReparse(cache);
        using var input = new FileStream(cache, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length == 0 || input.Length > 128L * 1024 * 1024)
            throw new InvalidDataException("The cached EuroScope MSI has an unsupported size.");
        string hash = Convert.ToHexString(SHA256.HashData(input));
        var metadata = environment.PackageMetadata(cache);
        if (!string.Equals(metadata.ProductCode, registration.ProductCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(metadata.UpgradeCode, UpgradeCode, StringComparison.OrdinalIgnoreCase) ||
            metadata.ProductName != "EuroScope" || metadata.Manufacturer != "Gergely Csernák" ||
            !SameVersion(metadata.Version, registration.Version))
            throw new InvalidDataException("The cached MSI does not match the reviewed EuroScope registration and product family.");
        if (metadata.HasUnboundedActions)
            throw new InvalidDataException("This EuroScope MSI contains removal or executable actions that Launchpad has not reviewed. Use Windows' installer manually.");
        if (metadata.Components.Count == 0 || metadata.Components.Count > 4096)
            throw new InvalidDataException("The EuroScope MSI component inventory is missing or too large.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var evidence = new List<string>
        {
            registration.ProductCode.ToUpperInvariant(), registration.Scope, selected.Context.ToString(), sid ?? "",
            root.ToUpperInvariant(), data.ToUpperInvariant(), cache.ToUpperInvariant(), hash,
            metadata.Version, metadata.UpgradeCode.ToUpperInvariant()
        };
        foreach (var component in metadata.Components.OrderBy(component => component.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (!Guid.TryParseExact(component.Id, "B", out _) || !seen.Add(component.Id))
                throw new InvalidDataException("The EuroScope MSI contains an untracked or duplicate component.");
            // Registry/ODBC key paths are not filesystem paths. A component combining such a key
            // with files is unsupported because its file directory cannot be proven from that key.
            if ((component.Attributes & (4 | 32)) != 0)
            {
                if (component.Files.Count != 0)
                    throw new InvalidDataException("A EuroScope MSI file component uses a non-file key path. Its destination needs manual review.");
                evidence.Add(component.Id.ToUpperInvariant() + "|non-file|" + component.Attributes);
                continue;
            }
            var key = environment.ComponentPath(registration.ProductCode, component.Id, sid, selected.Context);
            if (key.State != 3 || string.IsNullOrWhiteSpace(key.Path))
                throw new InvalidDataException("A registered EuroScope component destination could not be proven. Use Windows' installer manually.");
            string keyPath = FullPath(key.Path);
            CheckDestination(keyPath);
            string directory;
            if (component.KeyFile is null) directory = keyPath;
            else
            {
                string name = FileName(component.KeyFile);
                if (!Path.GetFileName(keyPath).Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    !component.Files.Contains(component.KeyFile, StringComparer.Ordinal))
                    throw new InvalidDataException("A registered EuroScope component path does not match its cached MSI file.");
                directory = Path.GetDirectoryName(keyPath)!;
            }
            paths.Add(keyPath);
            evidence.Add(component.Id.ToUpperInvariant() + "|" + component.Attributes + "|" + keyPath.ToUpperInvariant());
            foreach (string file in component.Files.Order(StringComparer.OrdinalIgnoreCase))
            {
                string path = FullPath(Path.Combine(directory, FileName(file)));
                CheckDestination(path);
                paths.Add(path);
                evidence.Add(component.Id.ToUpperInvariant() + "|file|" + path.ToUpperInvariant());
            }
        }
        if (!paths.Any(path => Same(path, Path.Combine(root, "EuroScope.exe"))))
            throw new InvalidDataException("The MSI footprint does not include the configured EuroScope executable.");
        return new(registration.ProductCode, registration.Scope, root, data,
            paths.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', evidence)))));

        void CheckDestination(string path)
        {
            if (!Within(root, path) && !Within(data, path) && !Within(fonts, path))
                throw new InvalidDataException("A registered EuroScope component points outside the reviewed program folder, your EuroScope AppData folder and Windows Fonts. Use Windows' installer manually.");
            RejectReparse(path);
        }
    }

    private static bool SameVersion(string left, string right) => Version.TryParse(left, out var a) &&
        Version.TryParse(right, out var b) && a.Major == b.Major && a.Minor == b.Minor &&
        Math.Max(a.Build, 0) == Math.Max(b.Build, 0) && Math.Max(a.Revision, 0) == Math.Max(b.Revision, 0);
    private static string FileName(string value)
    {
        string name = value.Split('|').Last();
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.EndsWith('.') || name.EndsWith(' ')) throw new InvalidDataException("The MSI contains an invalid file name.");
        return name;
    }
    private static string FullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.Length > 32700 || path.AsSpan(2).Contains(':') || path.Contains('"'))
            throw new InvalidDataException("The MSI footprint requires local absolute filesystem paths.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (SameRoot(full)) throw new InvalidDataException("A drive root is not a supported MSI component destination.");
        return full;
    }
    private static bool SameRoot(string path) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path)!), path, StringComparison.OrdinalIgnoreCase);
    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static bool Within(string root, string path) => Same(root, path) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void RejectReparse(string path)
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked MSI cache or component paths are not supported.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}

internal sealed record EuroScopeMsiComponent(string Id, int Attributes, string? KeyFile, IReadOnlyList<string> Files);
internal sealed record EuroScopeMsiMetadata(string ProductCode, string UpgradeCode, string ProductName,
    string Manufacturer, string Version, IReadOnlyList<EuroScopeMsiComponent> Components, bool HasUnboundedActions);
internal sealed record EuroScopeMsiComponentPath(int State, string? Path);

/// <summary>Every native probe is replaceable in the synthetic console harness.</summary>
internal sealed class EuroScopeMsiFootprintEnvironment
{
    public Func<string> CurrentUserSid { get; init; } = () =>
    {
        using var user = WindowsIdentity.GetCurrent();
        return user.User?.Value ?? throw new InvalidOperationException("Windows user identity is unavailable.");
    };
    public Func<string, string?, int, string, string?> ProductProperty { get; init; } = EuroScopeMsiFootprintNative.ProductProperty;
    public Func<string, EuroScopeMsiMetadata> PackageMetadata { get; init; } = EuroScopeMsiFootprintNative.PackageMetadata;
    public Func<string, string, string?, int, EuroScopeMsiComponentPath> ComponentPath { get; init; } = EuroScopeMsiFootprintNative.ComponentPath;
}

internal static class EuroScopeMsiFootprintNative
{
    internal static string? ProductProperty(string product, string? sid, int context, string property)
    {
        var buffer = new StringBuilder(32768); uint length = (uint)buffer.Capacity;
        uint result = MsiGetProductInfoExW(product, sid, context, property, buffer, ref length);
        if (result is 1605 or 1608) return null; // Unknown product/property in this exact context.
        if (result != 0) throw new IOException("Windows Installer could not read the exact EuroScope installation context (" + result + ").");
        return buffer.ToString();
    }
    internal static EuroScopeMsiComponentPath ComponentPath(string product, string component, string? sid, int context)
    {
        var buffer = new StringBuilder(32768); uint length = (uint)buffer.Capacity;
        int state = MsiGetComponentPathExW(product, component, sid, context, buffer, ref length);
        return new(state, buffer.ToString());
    }

    internal static EuroScopeMsiMetadata PackageMetadata(string path)
    {
        Check(MsiOpenDatabaseW(path, IntPtr.Zero, out uint database)); // MSIDBOPEN_READONLY. Never install/configure/provide components.
        try
        {
            var properties = Rows(database, "SELECT `Property`, `Value` FROM `Property`", 2)
                .ToDictionary(row => row[0], row => row[1], StringComparer.Ordinal);
            var files = Rows(database, "SELECT `File`, `Component_`, `FileName` FROM `File`", 3);
            if (files.Select(file => file[0]).Distinct(StringComparer.Ordinal).Count() != files.Count)
                throw new InvalidDataException("Duplicate MSI file identifiers.");
            var rawComponents = Rows(database, "SELECT `Component`, `ComponentId`, `Attributes`, `KeyPath` FROM `Component`", 4);
            var knownComponents = rawComponents.Select(component => component[0]).ToHashSet(StringComparer.Ordinal);
            if (files.Any(file => !knownComponents.Contains(file[1]))) throw new InvalidDataException("MSI files refer to unknown components.");
            var components = rawComponents.Select(component =>
            {
                int attributes = int.Parse(component[2], System.Globalization.CultureInfo.InvariantCulture);
                var owned = files.Where(file => file[1] == component[0]).ToArray();
                var key = owned.SingleOrDefault(file => file[0] == component[3]);
                if (component[3].Length != 0 && (attributes & (4 | 32)) == 0 && key == null)
                    throw new InvalidDataException("An MSI component has an unknown filesystem key path.");
                return new EuroScopeMsiComponent(component[1], attributes, key?[2], owned.Select(file => file[2]).ToArray());
            }).ToArray();
            // This proof cannot account for executable custom actions, wildcard cleanup, services,
            // self-registration or file moves. Unknown layouts stay a manual installer operation.
            bool unbounded = Rows(database, "SELECT `Type` FROM `CustomAction`", 1)
                .Any(row => row[0] is not ("19" or "51" or "307")) ||
                Rows(database, "SELECT `FileName` FROM `RemoveFile`", 1).Any(row => row[0].Length != 0) ||
                Rows(database, "SELECT `File_` FROM `SelfReg`", 1).Count != 0 ||
                Rows(database, "SELECT `ServiceInstall` FROM `ServiceInstall`", 1).Count != 0 ||
                Rows(database, "SELECT `FileKey` FROM `MoveFile`", 1).Count != 0;
            string Field(string name) => properties.TryGetValue(name, out var value) ? value : "";
            return new(Field("ProductCode"), Field("UpgradeCode"), Field("ProductName"), Field("Manufacturer"),
                Field("ProductVersion"), components, unbounded);
        }
        finally { MsiCloseHandle(database); }
    }

    private static List<string[]> Rows(uint database, string query, int columns)
    {
        Check(MsiDatabaseOpenViewW(database, query, out uint view));
        try
        {
            Check(MsiViewExecute(view, 0));
            var rows = new List<string[]>();
            while (true)
            {
                uint result = MsiViewFetch(view, out uint record);
                if (result == 259) break;
                Check(result);
                try
                {
                    if (rows.Count >= 8192) throw new InvalidDataException("The cached MSI table is too large.");
                    var row = new string[columns];
                    for (uint column = 1; column <= columns; column++)
                    {
                        var buffer = new StringBuilder(32768); uint length = (uint)buffer.Capacity;
                        Check(MsiRecordGetStringW(record, column, buffer, ref length));
                        row[column - 1] = buffer.ToString();
                    }
                    rows.Add(row);
                }
                finally { MsiCloseHandle(record); }
            }
            return rows;
        }
        finally { MsiViewClose(view); MsiCloseHandle(view); }
    }
    private static void Check(uint result)
    {
        if (result != 0) throw new IOException("The cached EuroScope MSI could not be inspected read-only (" + result + ").");
    }

    [DllImport("msi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint MsiGetProductInfoExW(string product, string? sid, int context, string property, StringBuilder value, ref uint length);
    [DllImport("msi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MsiGetComponentPathExW(string product, string component, string? sid, int context, StringBuilder path, ref uint length);
    [DllImport("msi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint MsiOpenDatabaseW(string path, IntPtr mode, out uint database);
    [DllImport("msi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint MsiDatabaseOpenViewW(uint database, string query, out uint view);
    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiViewExecute(uint view, uint record);
    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiViewFetch(uint view, out uint record);
    [DllImport("msi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint MsiRecordGetStringW(uint record, uint field, StringBuilder value, ref uint length);
    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiViewClose(uint view);
    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiCloseHandle(uint handle);
}
