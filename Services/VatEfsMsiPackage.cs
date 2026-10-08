using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VatscaUpdateChecker.Services;

internal sealed record VatEfsMsiFile(string RelativePath, long Length);
internal sealed record VatEfsMsiComponent(string Id, string? KeyPath, IReadOnlyList<VatEfsMsiFile> Files);
internal sealed record VatEfsMsiPackage(string ProductCode, string Version, IReadOnlyList<VatEfsMsiComponent> Components);

/// <summary>Reads MSI tables only. Unknown destinations and installer actions require adapter review.</summary>
internal static class VatEfsMsiPackageReader
{
    private static readonly HashSet<string> AllowedActions = new(StringComparer.Ordinal)
    {
        "WixSchedFirewallExceptionsInstall|1|WixFirewallCA|SchedFirewallExceptionsInstall",
        "WixSchedFirewallExceptionsUninstall|1|WixFirewallCA|SchedFirewallExceptionsUninstall",
        "WixRollbackFirewallExceptionsInstall|3329|WixFirewallCA|ExecFirewallExceptions",
        "WixExecFirewallExceptionsInstall|3073|WixFirewallCA|ExecFirewallExceptions",
        "WixRollbackFirewallExceptionsUninstall|3329|WixFirewallCA|ExecFirewallExceptions",
        "WixExecFirewallExceptionsUninstall|3073|WixFirewallCA|ExecFirewallExceptions",
        "WixUIValidatePath|65|WixUIWixca|ValidatePath", "WixUIPrintEula|65|WixUIWixca|PrintEula"
    };

    internal static VatEfsMsiPackage Read(string path)
    {
        Check(MsiOpenDatabaseW(path, IntPtr.Zero, out uint database));
        try
        {
            var tables = Rows(database, "SELECT `Name` FROM `_Tables`", 1).Select(r => r[0]).ToHashSet(StringComparer.Ordinal);
            List<string[]> Table(string name, string columns, int count) => tables.Contains(name)
                ? Rows(database, "SELECT " + columns + " FROM `" + name + "`", count) : [];
            var result = Parse(
                Table("Property", "`Property`, `Value`", 2),
                Table("Directory", "`Directory`, `Directory_Parent`, `DefaultDir`", 3),
                Table("Component", "`Component`, `ComponentId`, `Directory_`, `Attributes`, `KeyPath`", 5),
                Table("File", "`File`, `Component_`, `FileName`, `FileSize`", 4),
                Table("CustomAction", "`Action`, `Type`, `Source`, `Target`", 4),
                Table("Environment", "`Environment`, `Name`, `Value`, `Component_`", 4),
                Table("Registry", "`Registry`, `Root`, `Key`, `Name`, `Value`, `Component_`", 6),
                Table("WixFirewallException", "`WixFirewallException`, `Name`, `RemoteAddresses`, `Port`, `Protocol`, `Program`, `Attributes`, `Profile`, `Component_`", 9),
                Table("Upgrade", "`UpgradeCode`, `VersionMin`, `VersionMax`, `Attributes`, `ActionProperty`", 5));
            foreach (string table in new[] { "ServiceInstall", "ServiceControl", "RemoveFile", "MoveFile", "DuplicateFile", "SelfReg", "IniFile", "RemoveIniFile", "ODBCDataSource", "ODBCDriver", "ODBCTranslator", "BindImage" })
                if (tables.Contains(table) && Rows(database, "SELECT * FROM `" + table + "`", 1).Count != 0)
                    throw new InvalidDataException("This VatEFS MSI contains unreviewed installation actions. Use its official installer manually.");
            Check(MsiGetSummaryInformationW(database, null, 0, out uint summary));
            try
            {
                var template = new StringBuilder(1024); uint length = (uint)template.Capacity;
                Check(MsiSummaryInfoGetPropertyW(summary, 7, out _, out _, IntPtr.Zero, template, ref length));
                if (template.ToString() != "x64;1033") throw new InvalidDataException("Only the reviewed x64 VatEFS MSI is supported.");
            }
            finally { MsiCloseHandle(summary); }
            return result;
        }
        finally { MsiCloseHandle(database); }
    }

    internal static VatEfsMsiPackage Parse(IReadOnlyList<string[]> properties, IReadOnlyList<string[]> directories,
        IReadOnlyList<string[]> components, IReadOnlyList<string[]> files, IReadOnlyList<string[]> actions,
        IReadOnlyList<string[]> environment, IReadOnlyList<string[]> registry, IReadOnlyList<string[]> firewall,
        IReadOnlyList<string[]> upgrades)
    {
        if (properties.Count > 256 || directories.Count > 512 || components.Count > 8192 || files.Count > 8192)
            throw new InvalidDataException("The VatEFS MSI exceeds the supported layout limits.");
        var props = properties.ToDictionary(r => r[0], r => r[1], StringComparer.Ordinal);
        string Property(string name) => props.GetValueOrDefault(name, "");
        string product = Property("ProductCode"), version = Property("ProductVersion");
        if (!Guid.TryParseExact(product, "B", out _) || Property("ProductName") != "VatEFS" ||
            Property("Manufacturer") != "Martin Insulander" || Property("ALLUSERS") != "1" ||
            !Property("UpgradeCode").Equals(VatEfsInstallationProbe.UpgradeCode, StringComparison.OrdinalIgnoreCase) ||
            !IsVersion(version) || Property("WIXUI_INSTALLDIR") != "INSTALLDIR" || Property("ProductLanguage") != "1033")
            throw new InvalidDataException("The MSI identity, version or installation scope is not the supported VatEFS package.");
        if (actions.Any(row => !AllowedActions.Contains(string.Join('|', row))))
            throw new InvalidDataException("The VatEFS MSI contains an unreviewed custom action.");
        if (environment.Count != 1 || string.Join('|', environment[0]) != "Environment|=-*PATH|[INSTALLDIR];[~]|ApplicationFiles")
            throw new InvalidDataException("The VatEFS MSI changes an unexpected environment setting.");
        if (registry.Any(r => r[1] != "2" || r[2] != @"Software\VatEFS" || r[3] != "FirewallRuleInstalled" || r[4] != "#1" || r[5] != "FirewallRules") || registry.Count > 1)
            throw new InvalidDataException("The VatEFS MSI changes an unexpected registry setting.");
        if (firewall.Count > 1 || firewall.Any(r => string.Join('|', r) != "EfsFirewallException|VatEFS Backend|*||6|[INSTALLDIR]efs.exe|0|2147483647|FirewallRules") ||
            firewall.Count != registry.Count)
            throw new InvalidDataException("The VatEFS MSI contains an unreviewed firewall rule.");
        if (upgrades.Count != 3 || upgrades.Any(r => !r[0].Equals(VatEfsInstallationProbe.UpgradeCode, StringComparison.OrdinalIgnoreCase)) ||
            !upgrades.Any(r => r[1] == "" && r[2] == version && r[3] == "513" && r[4] == "WIX_UPGRADE_DETECTED") ||
            !upgrades.Any(r => r[1] == version && r[2] == "" && r[3] == "2" && r[4] == "WIX_DOWNGRADE_DETECTED") ||
            !upgrades.Any(r => r[1] == "0.0.0" && r[2] == version && r[3] == "256" && r[4] == "OLDERVERSIONBEINGUPGRADED"))
            throw new InvalidDataException("The VatEFS MSI upgrade rules have not been reviewed.");
        var dirs = directories.ToDictionary(r => r[0], r => r, StringComparer.Ordinal);
        if (!dirs.TryGetValue("INSTALLDIR", out var install) || install[1] != "ProgramFiles64Folder" || install[2] != "VatEFS" ||
            !dirs.TryGetValue("ProgramFiles64Folder", out var program) || program[1] != "TARGETDIR" || program[2] != "." ||
            !dirs.TryGetValue("TARGETDIR", out var target) || target[1] != "" || target[2] != "SourceDir")
            throw new InvalidDataException("The VatEFS MSI does not use the supported Program Files destination.");
        string RelativeDirectory(string name, int depth = 0)
        {
            if (name == "INSTALLDIR") return "";
            if (depth > 24 || !dirs.TryGetValue(name, out var row)) throw new InvalidDataException("Invalid VatEFS MSI directory tree.");
            return Path.Combine(RelativeDirectory(row[1], depth + 1), FileName(row[2]));
        }
        foreach (string name in dirs.Keys.Where(d => d is not ("TARGETDIR" or "ProgramFiles64Folder"))) _ = RelativeDirectory(name);
        var componentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var componentNames = new HashSet<string>(StringComparer.Ordinal);
        var fileIds = new HashSet<string>(StringComparer.Ordinal);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<VatEfsMsiComponent>();
        long total = 0;
        foreach (var row in components)
        {
            if (!Guid.TryParseExact(row[1], "B", out _) || !componentIds.Add(row[1]) || !componentNames.Add(row[0]) ||
                !int.TryParse(row[3], CultureInfo.InvariantCulture, out int attributes) || attributes is not (256 or 260))
                throw new InvalidDataException("The VatEFS MSI component identity or scope is unsupported.");
            string directory = RelativeDirectory(row[2]);
            var owned = files.Where(f => f[1] == row[0]).ToArray();
            if (attributes == 260)
            {
                if (row[0] != "FirewallRules" || !row[1].Equals("{3A7C9B42-F1D8-4E05-82B6-93AF0C571D2E}", StringComparison.OrdinalIgnoreCase) ||
                    owned.Length != 0 || registry.Count != 1 || registry[0][0] != row[4] || directory != "")
                    throw new InvalidDataException("Unsupported VatEFS registry component.");
                result.Add(new(row[1], null, []));
                continue;
            }
            if (owned.Length == 0) throw new InvalidDataException("The VatEFS MSI contains an unsupported empty component.");
            var converted = new List<VatEfsMsiFile>();
            string? keyPath = null;
            foreach (var file in owned)
            {
                string relative = Path.Combine(directory, FileName(file[2]));
                if (!fileIds.Add(file[0]) || !destinations.Add(relative) || !long.TryParse(file[3], out long length) || length < 0 ||
                    (total = checked(total + length)) > 4L * 1024 * 1024 * 1024)
                    throw new InvalidDataException("The VatEFS MSI file inventory is invalid or too large.");
                converted.Add(new(relative, length));
                if (row[4] == file[0]) keyPath = relative;
            }
            if (keyPath == null) throw new InvalidDataException("The VatEFS MSI has no verifiable file component key.");
            result.Add(new(row[1], keyPath, converted));
        }
        if (fileIds.Count != files.Count || new[] { "VatEFS.dll", "efs.exe", "README.txt" }.Any(name => !destinations.Contains(name)) ||
            !result.Any(c => c.Id.Equals(VatEfsInstallationProbe.ReadmeComponent, StringComparison.OrdinalIgnoreCase) && c.KeyPath == "README.txt"))
            throw new InvalidDataException("The VatEFS MSI is missing required application components.");
        return new(product, version, result);
    }

    internal static bool IsVersion(string value) => value.Length <= 32 && Version.TryParse(value, out var version) &&
        version.Revision == -1 && version.Major <= 255 && version.Minor <= 255 && version.Build is >= 0 and <= 65535;

    private static string FileName(string value)
    {
        string name = value.Split('|').Last();
        if (name.Length is 0 or > 255 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(name.Split('.')[0], StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("The MSI contains an unsafe file or directory name.");
        return name;
    }

    internal static string? ProductProperty(string product, string property)
    {
        var value = new StringBuilder(32768); uint length = (uint)value.Capacity;
        uint code = MsiGetProductInfoExW(product, null, 4, property, value, ref length);
        if (code is 1605 or 1608) return null;
        Check(code); return value.ToString();
    }

    internal static string? ComponentPath(string product, string component)
    {
        var path = new StringBuilder(32768); uint length = (uint)path.Capacity;
        return MsiGetComponentPathExW(product, component, null, 4, path, ref length) == 3 ? path.ToString() : null;
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
                uint status = MsiViewFetch(view, out uint record);
                if (status == 259) return rows;
                Check(status);
                try
                {
                    if (rows.Count >= 8192) throw new InvalidDataException("The VatEFS MSI table exceeds the supported limit.");
                    var row = new string[columns];
                    for (uint index = 1; index <= columns; index++)
                    {
                        var text = new StringBuilder(32768); uint length = (uint)text.Capacity;
                        Check(MsiRecordGetStringW(record, index, text, ref length)); row[index - 1] = text.ToString();
                    }
                    rows.Add(row);
                }
                finally { MsiCloseHandle(record); }
            }
        }
        finally { MsiViewClose(view); MsiCloseHandle(view); }
    }

    private static void Check(uint result)
    {
        if (result != 0) throw new IOException("The VatEFS MSI could not be inspected read-only (" + result + ").");
    }

    [DllImport("msi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern uint MsiOpenDatabaseW(string path, IntPtr mode, out uint database);
    [DllImport("msi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern uint MsiDatabaseOpenViewW(uint database, string query, out uint view);
    [DllImport("msi.dll", ExactSpelling = true)] private static extern uint MsiViewExecute(uint view, uint record);
    [DllImport("msi.dll", ExactSpelling = true)] private static extern uint MsiViewFetch(uint view, out uint record);
    [DllImport("msi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern uint MsiRecordGetStringW(uint record, uint field, StringBuilder value, ref uint length);
    [DllImport("msi.dll", ExactSpelling = true)] private static extern uint MsiViewClose(uint view);
    [DllImport("msi.dll", ExactSpelling = true)] private static extern uint MsiCloseHandle(uint handle);
    [DllImport("msi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern uint MsiGetSummaryInformationW(uint database, string? path, uint count, out uint summary);
    [DllImport("msi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern uint MsiSummaryInfoGetPropertyW(uint summary, uint property, out uint type, out int number, IntPtr time, StringBuilder text, ref uint length);
    [DllImport("msi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern uint MsiGetProductInfoExW(string product, string? sid, int context, string property, StringBuilder value, ref uint length);
    [DllImport("msi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern int MsiGetComponentPathExW(string product, string component, string? sid, int context, StringBuilder path, ref uint length);
}
