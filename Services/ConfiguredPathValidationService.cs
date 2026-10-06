using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text.RegularExpressions;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

public sealed record ConfiguredPathEntry(string Name, bool IsDirectory, long Length = 0, bool IsReparsePoint = false);
public sealed record ConfiguredBinaryMetadata(bool IsDll, string ProductName = "", string FileDescription = "",
    string OriginalFilename = "", string InternalName = "", string ProductVersion = "", bool IsInstaller = false);

/// <summary>Only filesystem and PE metadata probes; no registry, credentials, client loading or process execution.</summary>
public sealed class ConfiguredPathValidationEnvironment
{
    public Func<string, ConfiguredPathEntry?> ReadPath { get; init; } = ReadEntry;
    public Func<string, IReadOnlyList<ConfiguredPathEntry>> EnumerateDirectory { get; init; } = Enumerate;
    public Func<string, ConfiguredBinaryMetadata> ReadBinary { get; init; } = ConfiguredPathValidationService.ReadBinaryMetadata;
    internal const int MaximumEntries = 2048;

    private static ConfiguredPathEntry? ReadEntry(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            bool directory = attributes.HasFlag(FileAttributes.Directory);
            return new(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), directory,
                directory ? 0 : new FileInfo(path).Length, attributes.HasFlag(FileAttributes.ReparsePoint));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    private static IReadOnlyList<ConfiguredPathEntry> Enumerate(string path)
    {
        var result = new List<ConfiguredPathEntry>();
        foreach (var child in Directory.EnumerateFileSystemEntries(path))
        {
            if (result.Count == MaximumEntries) throw new IOException("Directory inspection limit reached.");
            if (ReadEntry(child) is { } entry) result.Add(entry);
        }
        return result;
    }
}

public sealed class ConfiguredPathValidationService
{
    private readonly ConfiguredPathValidationEnvironment _environment;
    private static readonly Regex SectorName = new(@"^ESAA-Sweden_\d{14}-\d{6}-\d{4}\.(sct|ese)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public ConfiguredPathValidationService(ConfiguredPathValidationEnvironment? environment = null) =>
        _environment = environment ?? new();

    public IReadOnlyList<ConfiguredPathValidation> Validate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Enum.GetValues<ConfiguredPathKind>().Select(kind => Validate(kind, PathFor(kind, settings))).ToArray();
    }

    public static string PathFor(ConfiguredPathKind kind, AppSettings settings) => (kind switch
    {
        ConfiguredPathKind.EuroScopeExecutable => settings.EuroscopeExePath,
        ConfiguredPathKind.EuroScopeDataFolder => settings.EuroscopeDataPath,
        ConfiguredPathKind.TrackAudioExecutable => settings.TrackAudioExePath,
        ConfiguredPathKind.VacsExecutable => settings.VacsExePath,
        ConfiguredPathKind.VatisExecutable => settings.VatisExePath,
        ConfiguredPathKind.VatEfsFolder => settings.VatEfsPath,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    }) ?? "";

    public static bool NeedsAttention(ConfiguredPathValidation result, AppSettings original) =>
        result.Status is PathValidationStatus.Warning or PathValidationStatus.Invalid &&
        !string.IsNullOrWhiteSpace(result.Path) &&
        !result.Path.Trim().Equals(PathFor(result.Kind, original).Trim(), StringComparison.OrdinalIgnoreCase);

    public ConfiguredPathValidation Validate(ConfiguredPathKind kind, string? path)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        string selected = path?.Trim() ?? "";
        ConfiguredPathValidation Result(PathValidationStatus status, string message) => new(kind, selected, status, message);
        if (selected.Length == 0) return Result(PathValidationStatus.NotSelected, "Optional — no location selected.");
        if (selected.Contains('"') || selected.StartsWith('\'') || selected.EndsWith('\''))
            return Result(PathValidationStatus.Invalid, "Remove quotation marks around the location, or choose it with Browse.");
        if (!Path.IsPathFullyQualified(selected))
            return Result(PathValidationStatus.Invalid, "Choose the full file or folder location with Browse, starting with a drive letter such as C:\\.");
        if (selected.StartsWith(@"\\?\", StringComparison.Ordinal) || selected.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            selected.IndexOfAny(['<', '>', '|', '*', '?', '\0']) >= 0 || selected.AsSpan(2).Contains(':'))
            return Result(PathValidationStatus.Invalid, "This is not a normal file or folder location. Choose it with Browse.");
        if (selected.StartsWith(@"\\", StringComparison.Ordinal))
            return Result(PathValidationStatus.Warning, "Network locations cannot be checked here. Only keep it if you recognise this location.");
        try
        {
            string full = Path.GetFullPath(selected);
            var entry = _environment.ReadPath(full);
            bool folder = kind is ConfiguredPathKind.EuroScopeDataFolder or ConfiguredPathKind.VatEfsFolder;
            if (entry == null)
                return Result(PathValidationStatus.Invalid, folder ? "This folder was not found. Choose an existing folder." : "This file was not found. Choose the installed application's program file.");
            if (entry.IsDirectory != folder)
                return Result(PathValidationStatus.Invalid, folder ? "Choose the containing folder, not a file." : "Choose the application's .exe file inside this folder.");
            if (entry.IsReparsePoint)
                return Result(PathValidationStatus.Warning, "This is a link or redirected location. Its contents were not checked.");
            if (kind == ConfiguredPathKind.EuroScopeDataFolder) return ValidateGng(full, Result);
            if (kind == ConfiguredPathKind.VatEfsFolder)
            {
                full = Path.Combine(full, "VatEFS.dll");
                entry = _environment.ReadPath(full);
                if (entry == null || entry.IsDirectory)
                    return Result(PathValidationStatus.Invalid, "VatEFS.dll was not found here. Choose the folder containing the VatEFS plugin.");
                if (entry.IsReparsePoint)
                    return Result(PathValidationStatus.Warning, "VatEFS.dll is a link or redirected file. Its identity was not verified.");
            }
            else if (!Path.GetExtension(full).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                return Result(PathValidationStatus.Invalid, "Choose the installed application's .exe file, not a shortcut, package or other file.");
            return ValidateBinary(kind, full, _environment.ReadBinary(full), Result);
        }
        catch (InvalidDataException)
        {
            return Result(PathValidationStatus.Invalid, "This is not a valid Windows application or plugin file. Choose the installed copy again.");
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result(PathValidationStatus.Invalid, "This location cannot be used. Choose a normal file or folder with Browse.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or System.ComponentModel.Win32Exception)
        {
            return Result(PathValidationStatus.Warning, "This location could not be fully checked. Check access and try again. Only keep it if you recognise this location.");
        }
    }

    private ConfiguredPathValidation ValidateGng(string folder, Func<PathValidationStatus, string, ConfiguredPathValidation> result)
    {
        var entries = Entries(folder);
        if (Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)).Equals("ESAA", StringComparison.OrdinalIgnoreCase))
            return result(PathValidationStatus.Invalid, "Choose the parent EuroScope data folder containing ESAA and the .prf/.sct files, not ESAA itself.");
        bool esaa = entries.Any(e => e.IsDirectory && Equal(e.Name, "ESAA"));
        bool profiles = entries.Any(e => !e.IsDirectory && !e.IsReparsePoint && e.Length > 0 && e.Name.StartsWith("ESAA ", StringComparison.OrdinalIgnoreCase) && Equal(Path.GetExtension(e.Name), ".prf"));
        var sectors = entries.Where(e => !e.IsDirectory && !e.IsReparsePoint && e.Length > 0 && SectorName.IsMatch(e.Name)).ToArray();
        bool pair = sectors.Any(s => Equal(Path.GetExtension(s.Name), ".sct") &&
            sectors.Any(e => Equal(e.Name, Path.ChangeExtension(s.Name, ".ese"))));
        bool plugins = false;
        if (esaa)
        {
            var esaaEntry = entries.First(e => e.IsDirectory && Equal(e.Name, "ESAA"));
            if (esaaEntry.IsReparsePoint) return result(PathValidationStatus.Warning, "The ESAA package folder is redirected. Package contents were not verified.");
            string pluginFolder = Path.Combine(folder, "ESAA", "Plugins");
            var pluginEntry = _environment.ReadPath(pluginFolder);
            if (pluginEntry is { IsDirectory: true, IsReparsePoint: false })
                plugins = Entries(pluginFolder).Any(e => !e.IsDirectory && !e.IsReparsePoint && e.Length > 0 && Equal(Path.GetExtension(e.Name), ".dll"));
        }
        if (esaa && profiles && pair && plugins)
            return result(PathValidationStatus.Verified, "Swedish GNG package files found. Profile references and readiness to control are not checked.");
        if (profiles || sectors.Length > 0 || esaa && plugins)
            return result(PathValidationStatus.Warning, "Some Swedish package files were found, but the usual profiles, matching sector files and plugins are incomplete. Keep only if this custom setup is intended.");
        if (entries.Any(e => !e.IsDirectory && Equal(e.Name, "EuroScope.exe")))
            return result(PathValidationStatus.Invalid, "This looks like the EuroScope program folder. Choose the separate data folder containing the Swedish GNG package.");
        return result(PathValidationStatus.Invalid, "No Swedish GNG package files were found. Choose the data folder containing ESAA, Swedish .prf profiles and sector files.");
    }

    private IReadOnlyList<ConfiguredPathEntry> Entries(string path)
    {
        var entries = _environment.EnumerateDirectory(path);
        if (entries.Count > ConfiguredPathValidationEnvironment.MaximumEntries) throw new IOException("Directory inspection limit reached.");
        return entries;
    }

    private static ConfiguredPathValidation ValidateBinary(ConfiguredPathKind kind, string path, ConfiguredBinaryMetadata binary,
        Func<PathValidationStatus, string, ConfiguredPathValidation> result)
    {
        var (name, expectedFile, products) = kind switch
        {
            ConfiguredPathKind.EuroScopeExecutable => ("EuroScope", "EuroScope.exe", new[] { "EuroScope", "EuroScope Application" }),
            ConfiguredPathKind.TrackAudioExecutable => ("TrackAudio", "trackaudio.exe", new[] { "TrackAudio" }),
            ConfiguredPathKind.VacsExecutable => ("VACS", "vacs-client.exe", new[] { "vacs", "vacs-client", "VACS Client" }),
            ConfiguredPathKind.VatisExecutable => ("vATIS", "vATIS.exe", new[] { "vATIS" }),
            _ => ("VatEFS", "VatEFS.dll", new[] { "VatEFS" })
        };
        if (binary.IsDll != (kind == ConfiguredPathKind.VatEfsFolder))
            return result(PathValidationStatus.Invalid, kind == ConfiguredPathKind.VatEfsFolder ? "VatEFS.dll is not a Windows plugin DLL." : "This is a supporting DLL, not the application's program file.");
        if (binary.IsInstaller || LooksLikeInstaller(Path.GetFileName(path)) || LooksLikeInstaller(binary.OriginalFilename) ||
            LooksLikeInstaller(binary.InternalName) || Regex.IsMatch(binary.FileDescription, @"\b(setup|installer|uninstaller|updater)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return result(PathValidationStatus.Invalid, "This is an installer or updater. Choose the installed " + name + " application instead.");
        bool product = products.Any(p => Equal(p, binary.ProductName.Trim()));
        bool description = products.Any(p => Equal(p, binary.FileDescription.Trim()));
        string original = Path.GetFileName(binary.OriginalFilename.Trim());
        bool originalMatches = Equal(original, expectedFile) ||
            kind == ConfiguredPathKind.VatisExecutable && Equal(original, "vATIS.dll");
        if (!product && !string.IsNullOrWhiteSpace(binary.ProductName))
            return result(PathValidationStatus.Invalid, "The file identifies a different application. Choose the installed " + name + " copy.");
        if (!product && !(description && originalMatches))
        {
            if (!Equal(Path.GetFileName(path), expectedFile) || (!string.IsNullOrWhiteSpace(original) && !originalMatches))
                return result(PathValidationStatus.Invalid, "This file does not identify itself as " + name + ". Choose the installed copy again.");
            return result(PathValidationStatus.Warning, "The filename matches " + name + ", but its file details are incomplete. Only keep it if you recognise this copy.");
        }
        if (!Equal(Path.GetFileName(path), expectedFile))
            return result(PathValidationStatus.Warning, name + " was recognized under a different filename. Keep only if this is your intended copy.");
        if (kind == ConfiguredPathKind.EuroScopeExecutable && binary.ProductVersion.Trim() != EuroScopePolicy.SupportedVersion)
            return result(PathValidationStatus.Warning, "EuroScope was recognized, but the supported Swedish version " + EuroScopePolicy.SupportedVersion + " was not confirmed. Use Manage to review the version.");
        if (kind == ConfiguredPathKind.VacsExecutable && !binary.ProductVersion.StartsWith("2.", StringComparison.Ordinal))
            return result(PathValidationStatus.Warning, "VACS was recognized, but this version may need manual management. This location can still be kept.");
        return result(PathValidationStatus.Verified, name + " file identified. Installation/update support is checked separately.");
    }

    private static bool Equal(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);
    private static bool LooksLikeInstaller(string value) => Regex.IsMatch(Path.GetFileNameWithoutExtension(value.Trim()),
        @"^(setup|install|uninstall|update)$|(?:^|[-_ .])(setup|installer|uninstaller|updater)(?:$|[-_ .])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Read fixed-size PE structures and a bounded overlay prefix. FileVersionInfo reads resources,
    // not executable code; holding this stream prevents normal replacement while metadata is read.
    internal static ConfiguredBinaryMetadata ReadBinaryMetadata(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 512L * 1024 * 1024) throw new IOException("Binary inspection limit reached.");
        byte[] Read(long offset, int length)
        {
            if (offset < 0 || length < 0 || offset > stream.Length - length) throw new InvalidDataException("Truncated PE.");
            stream.Position = offset;
            var bytes = new byte[length]; stream.ReadExactly(bytes); return bytes;
        }
        var dos = Read(0, 64);
        if (dos[0] != 'M' || dos[1] != 'Z') throw new InvalidDataException("Missing DOS header.");
        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(dos.AsSpan(60));
        if (peOffset < 64 || peOffset > 1024 * 1024) throw new InvalidDataException("Invalid PE header location.");
        var header = Read(peOffset, 24);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x4550) throw new InvalidDataException("Missing PE signature.");
        int machine = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
        int sections = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
        int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(20));
        int characteristics = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(22));
        if (machine is not (0x14c or 0x8664 or 0xaa64) || sections is < 1 or > 96 || optionalSize is < 96 or > 4096 || (characteristics & 2) == 0)
            throw new InvalidDataException("Invalid PE layout.");
        var optional = Read(peOffset + 24L, optionalSize);
        int magic = BinaryPrimitives.ReadUInt16LittleEndian(optional);
        int directories = magic switch { 0x10b => 96, 0x20b => 112, _ => throw new InvalidDataException("Unsupported PE image.") };
        if (optionalSize < directories) throw new InvalidDataException("Truncated optional header.");
        if (optionalSize >= directories + 24 && BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(directories + 20)) > 8 * 1024 * 1024)
            throw new IOException("Version resource inspection limit reached.");
        var table = Read(peOffset + 24L + optionalSize, sections * 40);
        long end = peOffset + 24L + optionalSize + table.Length;
        long headerEnd = end;
        var ranges = new List<(long Start, long End)>();
        for (int i = 0; i < sections; i++)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i * 40 + 16));
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i * 40 + 20));
            if (size == 0) continue;
            long sectionEnd = (long)offset + size;
            if (sectionEnd > stream.Length || offset < headerEnd || ranges.Any(r => offset < r.End && sectionEnd > r.Start))
                throw new InvalidDataException("Invalid PE section.");
            ranges.Add((offset, sectionEnd));
            end = Math.Max(end, sectionEnd);
        }
        var overlay = Read(end, (int)Math.Min(65536, stream.Length - end));
        // NSIS firstheader: https://github.com/kichik/nsis/blob/v311/Source/exehead/fileform.h
        bool installer = overlay.AsSpan().IndexOf(new byte[] { 0xEF, 0xBE, 0xAD, 0xDE, (byte)'N', (byte)'u', (byte)'l', (byte)'l', (byte)'s', (byte)'o', (byte)'f', (byte)'t', (byte)'I', (byte)'n', (byte)'s', (byte)'t' }) >= 0;
        // Velopack Setup's patched bundle descriptor is inside its PE, not its overlay.
        // Same native format as VatisFreshInstaller; validate its embedded ZIP range too.
        var marker = Convert.FromHexString("94F0B17B6893E02937EB34EF53AAE7D42B54F5707EF5D6F57854983E5E94ED7D");
        var prefix = Read(0, (int)Math.Min(end, 8L * 1024 * 1024));
        int search = 0;
        while (search < prefix.Length && prefix.AsSpan(search).IndexOf(marker) is var found && found >= 0)
        {
            int position = search + found;
            if (position >= 16)
            {
                long offset = BinaryPrimitives.ReadInt64LittleEndian(prefix.AsSpan(position - 16));
                long length = BinaryPrimitives.ReadInt64LittleEndian(prefix.AsSpan(position - 8));
                if (offset >= end && offset >= position + marker.Length && length >= 4 && offset <= stream.Length - length &&
                    BinaryPrimitives.ReadUInt32LittleEndian(Read(offset, 4)) == 0x04034b50)
                    installer = true;
            }
            search = position + marker.Length;
        }
        var version = FileVersionInfo.GetVersionInfo(path);
        return new((characteristics & 0x2000) != 0, version.ProductName ?? "", version.FileDescription ?? "",
            version.OriginalFilename ?? "", version.InternalName ?? "", version.ProductVersion ?? "", installer);
    }
}
