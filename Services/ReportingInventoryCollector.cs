using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

// Reads only explicitly configured EuroScope/VatEFS/SAAMAN locations. The legacy summary
// has fixed component IDs; the separately consented DLL snapshot includes every DLL.
// It does not discover apps, read profiles/settings, query registrations or load DLLs.
public static class ReportingInventoryCollector
{
    private const int MaximumEntries = 256, MaximumDirectories = 64, MaximumDepth = 2;
    private static readonly IReadOnlyDictionary<string, ReportingInventoryComponent> Plugins =
        new Dictionary<string, ReportingInventoryComponent>(StringComparer.OrdinalIgnoreCase)
        {
            ["TopSky.dll"] = ReportingInventoryComponent.Topsky,
            ["RDF.dll"] = ReportingInventoryComponent.Rdf,
            ["RDFPlugin.dll"] = ReportingInventoryComponent.Rdf,
            ["VatEFS.dll"] = ReportingInventoryComponent.VatefsPlugin,
            ["AfvEuroScopeBridge.dll"] = ReportingInventoryComponent.AfvEuroscopeBridge
        };

    public static IReadOnlyList<ReportingInventoryEntry> Collect(IEnumerable<ReportingAppObservation> observations,
        string? euroscopeDataFolder, string? euroscopeExecutableFolder, string? vatEfsFolder = null)
    {
        ArgumentNullException.ThrowIfNull(observations);
        var result = Enum.GetValues<ReportingInventoryComponent>().ToDictionary(component => component,
            component => Entry(component, ReportingInventoryStatus.Unavailable));
        // Inputs are already observed by the normal version checker. Unknown metadata
        // cannot become text fields in a report, even if a vendor supplies unusual text.
        foreach (var group in observations.Take(17).Where(item => item is not null && Enum.IsDefined(item.Component) &&
            item.Component <= ReportingInventoryComponent.Vatefs).GroupBy(item => item.Component))
        {
            if (group.Count() != 1) continue;
            var item = group.Single();
            if (!Enum.IsDefined(item.Status)) continue;
            var version = PublicVersion(item.Version);
            result[item.Component] = Entry(item.Component,
                item.Status == ReportingInventoryStatus.Known && version is null ? ReportingInventoryStatus.Unknown : item.Status,
                item.Status == ReportingInventoryStatus.Known ? version : null);
        }

        var found = Plugins.Values.Distinct().ToDictionary(component => component, _ => new List<string?>());
        bool configuredVatEfs = !string.IsNullOrWhiteSpace(vatEfsFolder);
        bool recognized = false, complete = true;
        var scanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in new[] { (Path: euroscopeDataFolder, Data: true), (Path: euroscopeExecutableFolder, Data: false) })
        {
            if (string.IsNullOrWhiteSpace(candidate.Path)) continue;
            try
            {
                using var lease = PathLease.OpenRoot(candidate.Path);
                string root = lease.Root;
                if (candidate.Data)
                {
                    string esaa = Path.Combine(root, "ESAA");
                    if (!Exists(esaa)) { complete = false; continue; }
                    lease.Open(esaa, directory: true);
                    recognized = true;
                    ScanIfPresent(Path.Combine(esaa, "Plugins"), lease, gngPlugins: true);
                    ScanIfPresent(Path.Combine(root, "Plugins"), lease);
                }
                else
                {
                    string executable = Path.Combine(root, "EuroScope.exe");
                    if (!Exists(executable)) { complete = false; continue; }
                    lease.Open(executable, directory: false);
                    recognized = true;
                    ScanIfPresent(Path.Combine(root, "Plugins"), lease);
                }
            }
            catch (Exception error) when (IsReadError(error)) { complete = false; }
        }
        foreach (var component in found.Keys)
        {
            var versions = found[component].Distinct(StringComparer.Ordinal).ToArray();
            result[component] = !recognized || !complete || versions.Length > 1
                ? Entry(component, ReportingInventoryStatus.Unavailable)
                : versions.Length == 0 ? Entry(component, ReportingInventoryStatus.Absent)
                : versions[0] is null ? Entry(component, ReportingInventoryStatus.Unknown)
                : Entry(component, ReportingInventoryStatus.Known, versions[0]);
        }
        // Profile configuration uses this exact DLL, which may live beside the
        // VatEFS backend outside EuroScope. Other copies do not override it, and
        // an inaccessible configured folder cannot spoil unrelated plugin data.
        if (configuredVatEfs)
            result[ReportingInventoryComponent.VatefsPlugin] = ReadConfiguredVatEfs(vatEfsFolder!);
        return result.OrderBy(item => item.Key).Select(item => item.Value).ToArray();

        void ScanIfPresent(string path, PathLease lease, bool gngPlugins = false)
        {
            if (!scanned.Add(path)) return;
            if (!Exists(path)) return;
            lease.Open(path, directory: true);
            var pending = new Queue<(string Path, int Depth)>(); pending.Enqueue((path, 0));
            int entries = 0, directories = 1;
            while (pending.TryDequeue(out var directory))
            {
                foreach (string child in Directory.EnumerateFileSystemEntries(directory.Path))
                {
                    if (++entries > MaximumEntries) { complete = false; return; }
                    // GNG promotes these exact staging locations into ESAA/Plugins.
                    // Their archived/staged copies are not another installed version.
                    if (gngPlugins && directory.Depth == 0 &&
                        (Path.GetFileName(child).Equals("Updated Plugin", StringComparison.OrdinalIgnoreCase) ||
                         Path.GetFileName(child).Equals("Updated Plugins", StringComparison.OrdinalIgnoreCase))) continue;
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { complete = false; continue; }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (directory.Depth >= MaximumDepth || ++directories > MaximumDirectories) { complete = false; continue; }
                        lease.Open(child, directory: true);
                        pending.Enqueue((child, directory.Depth + 1));
                    }
                    else if (Plugins.TryGetValue(Path.GetFileName(child), out var component))
                    {
                        if (configuredVatEfs && component == ReportingInventoryComponent.VatefsPlugin) continue;
                        lease.Open(child, directory: false);
                        if (new FileInfo(child).Length > 64L * 1024 * 1024) { complete = false; continue; }
                        var version = ReportingPluginVersionReader.Read(child, FileVersionInfo.GetVersionInfo(child)).Version;
                        // The legacy summary keeps its original version grammar.
                        // Additional public DLL formats belong to the detailed snapshot.
                        found[component].Add(ReportingWire.Version(version) ? version : null);
                    }
                }
            }
        }
    }

    private static ReportingInventoryEntry ReadConfiguredVatEfs(string folder)
    {
        const ReportingInventoryComponent component = ReportingInventoryComponent.VatefsPlugin;
        try
        {
            using var lease = PathLease.OpenRoot(folder);
            string path = Path.Combine(lease.Root, "VatEFS.dll");
            if (!Exists(path)) return Entry(component, ReportingInventoryStatus.Absent);
            lease.Open(path, directory: false);
            if (new FileInfo(path).Length > 64L * 1024 * 1024)
                return Entry(component, ReportingInventoryStatus.Unavailable);
            var version = ReportingPluginVersionReader.Read(path, FileVersionInfo.GetVersionInfo(path)).Version;
            if (!ReportingWire.Version(version)) version = null;
            return Entry(component, version is null ? ReportingInventoryStatus.Unknown : ReportingInventoryStatus.Known, version);
        }
        catch (Exception error) when (IsReadError(error))
        {
            return Entry(component, ReportingInventoryStatus.Unavailable);
        }
    }

    public static ReportingDllInventory CollectDllInventory(string? euroscopeDataFolder, string? euroscopeExecutableFolder,
        string? vatEfsFolder = null, string? saamanDllPath = null) =>
        CollectDllInventory(euroscopeDataFolder, euroscopeExecutableFolder, vatEfsFolder, new(), saamanDllPath);

    internal sealed record DllScanLimits(int Entries = 4096, int Directories = 128, int Depth = 4,
        long FileBytes = 64L * 1024 * 1024, long TotalBytes = 256L * 1024 * 1024,
        int Groups = ReportingWire.MaximumDllEntries, int JsonBytes = ReportingWire.MaximumDllInventoryBytes);

    internal static ReportingDllInventory CollectDllInventory(string? euroscopeDataFolder, string? euroscopeExecutableFolder,
        string? vatEfsFolder, DllScanLimits limits, string? saamanDllPath = null)
    {
        if (limits.Entries is < 1 or > 4096 || limits.Directories is < 1 or > 128 || limits.Depth is < 0 or > 4 ||
            limits.FileBytes is < 1 or > 64L * 1024 * 1024 || limits.TotalBytes is < 1 or > 256L * 1024 * 1024 ||
            limits.Groups is < 1 or > ReportingWire.MaximumDllEntries || limits.JsonBytes is < 128 or > ReportingWire.MaximumDllInventoryBytes)
            throw new ArgumentOutOfRangeException(nameof(limits));
        var found = new Dictionary<(string Hash, ReportingDllRole Role), ReportingDllEntry>();
        var scannedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scannedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directoryIdentities = new HashSet<NativeFileIdentity>();
        var fileIdentities = new HashSet<(NativeFileIdentity Identity, ReportingDllRole Role)>();
        int entries = 0, directories = 0, unreadable = 0;
        long bytes = 0;
        bool recognized = false, partial = false, stopped = false;
        foreach (var candidate in new[] { (Path: euroscopeDataFolder, Data: true), (Path: euroscopeExecutableFolder, Data: false) })
        {
            if (stopped || string.IsNullOrWhiteSpace(candidate.Path)) continue;
            try
            {
                using var lease = PathLease.OpenRoot(candidate.Path);
                string root = lease.Root;
                if (candidate.Data)
                {
                    string esaa = Path.Combine(root, "ESAA");
                    if (!Exists(esaa)) { partial = true; continue; }
                    lease.Open(esaa, directory: true);
                    recognized = true;
                    Scan(Path.Combine(esaa, "Plugins"), lease, gngPlugins: true);
                    Scan(Path.Combine(root, "Plugins"), lease);
                }
                else
                {
                    string executable = Path.Combine(root, "EuroScope.exe");
                    if (!Exists(executable)) { partial = true; continue; }
                    using var executableLease = lease.OpenFile(executable);
                    recognized = true;
                    Scan(Path.Combine(root, "Plugins"), lease);
                }
            }
            catch (Exception error) when (IsReadError(error)) { partial = true; }
        }
        if (!stopped && !string.IsNullOrWhiteSpace(vatEfsFolder))
        {
            try
            {
                using var lease = PathLease.OpenRoot(vatEfsFolder);
                recognized = true;
                string dll = Path.Combine(lease.Root, "VatEFS.dll");
                // This explicit file is included without walking the backend's dependencies.
                if (Exists(dll) && !scannedFiles.Contains(dll))
                {
                    if (++entries > limits.Entries) Stop();
                    else ReadDll(dll, ReportingDllRole.Current, lease);
                }
            }
            catch (Exception error) when (IsReadError(error)) { partial = true; }
        }
        if (!stopped && !string.IsNullOrWhiteSpace(saamanDllPath))
        {
            try
            {
                if (!Path.IsPathFullyQualified(saamanDllPath) ||
                    !Path.GetExtension(saamanDllPath).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("An explicit local DLL path is required.");
                string dll = Path.GetFullPath(saamanDllPath);
                using var lease = PathLease.OpenRoot(Path.GetDirectoryName(dll)!);
                recognized = true;
                // The selected DLL may be outside EuroScope. Never walk its
                // sibling files or read the associated SAAMAN settings/profile.
                if (Exists(dll) && !scannedFiles.Contains(dll))
                {
                    if (++entries > limits.Entries) Stop();
                    else ReadDll(dll, ReportingDllRole.Current, lease);
                }
            }
            catch (Exception error) when (IsReadError(error)) { partial = true; }
        }
        return new(!recognized ? ReportingDllInventoryStatus.Unavailable : partial ? ReportingDllInventoryStatus.Partial : ReportingDllInventoryStatus.Complete,
            found.Values.OrderBy(item => item.Role).ThenBy(item => item.Sha256, StringComparer.Ordinal).ToArray(), unreadable);

        void Scan(string path, PathLease lease, bool gngPlugins = false)
        {
            if (stopped || !Exists(path)) return;
            if (!scannedDirectories.Add(path)) return;
            if (directories == limits.Directories) { Stop(); return; }
            if (!directoryIdentities.Add(lease.OpenDirectory(path))) return;
            directories++;
            var pending = new Queue<(string Path, int Depth, ReportingDllRole Role)>();
            pending.Enqueue((path, 0, ReportingDllRole.Current));
            while (!stopped && pending.TryDequeue(out var directory))
            {
                try
                {
                    foreach (string child in Directory.EnumerateFileSystemEntries(directory.Path))
                    {
                        if (stopped) break;
                        if (++entries > limits.Entries) { Stop(); break; }
                        bool dll = Path.GetExtension(child).Equals(".dll", StringComparison.OrdinalIgnoreCase);
                        try
                        {
                            var attributes = File.GetAttributes(child);
                            if ((attributes & FileAttributes.ReparsePoint) != 0) { partial = true; continue; }
                            if ((attributes & FileAttributes.Directory) != 0)
                            {
                                if (directory.Depth >= limits.Depth) { partial = true; continue; }
                                if (!scannedDirectories.Add(child)) continue;
                                if (directories == limits.Directories) { Stop(); break; }
                                if (!directoryIdentities.Add(lease.OpenDirectory(child))) continue;
                                directories++;
                                var role = directory.Role;
                                if (gngPlugins && directory.Depth == 0 && IsGngStagingName(Path.GetFileName(child))) role = ReportingDllRole.Staged;
                                pending.Enqueue((child, directory.Depth + 1, role));
                            }
                            else if (dll) ReadDll(child, directory.Role, lease);
                        }
                        catch (Exception error) when (IsReadError(error))
                        { partial = true; if (dll) unreadable = Math.Min(4096, unreadable + 1); }
                    }
                }
                catch (Exception error) when (IsReadError(error)) { partial = true; }
            }
        }

        void ReadDll(string path, ReportingDllRole role, PathLease lease)
        {
            if (stopped || !scannedFiles.Add(path)) return;
            try
            {
                using var pinnedFile = lease.OpenFile(path);
                // Different spellings/short paths and hard links can identify
                // the same physical file. Its staged/current contexts stay distinct.
                if (!fileIdentities.Add((PathLease.Identity(pinnedFile), role))) return;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > limits.FileBytes) { partial = true; return; }
                if (stream.Length > limits.TotalBytes - bytes) { Stop(); return; }
                bytes += stream.Length;
                string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                var key = (hash, role);
                bool duplicate = found.TryGetValue(key, out var previous);
                ReportingDllEntry next;
                if (duplicate)
                {
                    if (previous!.Copies == 4096) { Stop(); return; }
                    next = previous! with { Copies = previous.Copies + 1 };
                }
                else
                {
                    if (found.Count == limits.Groups) { Stop(); return; }
                    var metadata = ReportingPluginVersionReader.Read(path, FileVersionInfo.GetVersionInfo(path));
                    metadata = ReportingPluginVersionReader.RecognizeFingerprint(metadata, hash);
                    next = new(hash, metadata.Product, role, 1, metadata.Version, metadata.FileVersion, metadata.Source);
                }
                found[key] = next;
                // Reserve the longest status and counter representations so later
                // partial-read information cannot make an accepted snapshot too big.
                if (ReportingWire.Bytes(new ReportingDllInventory(ReportingDllInventoryStatus.Unavailable, found.Values.ToArray(), 4096)).Length > limits.JsonBytes)
                {
                    if (duplicate) found[key] = previous!; else found.Remove(key);
                    Stop();
                }
            }
            catch (Exception error) when (IsReadError(error))
            { partial = true; unreadable = Math.Min(4096, unreadable + 1); }
        }

        void Stop() { partial = true; stopped = true; }
    }

    private static bool IsGngStagingName(string name) => name.Equals("Updated Plugin", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Updated Plugins", StringComparison.OrdinalIgnoreCase);

    internal static string? PluginVersion(FileVersionInfo info)
    {
        string? product = NormalizePluginVersion(info.ProductVersion), file = NormalizePluginVersion(info.FileVersion);
        // Nonempty unsupported text can contain release qualifiers. A numeric
        // fallback must not turn an unrecognised beta/private build into stable.
        if ((!string.IsNullOrWhiteSpace(info.ProductVersion) && product is null) ||
            (!string.IsNullOrWhiteSpace(info.FileVersion) && file is null)) return null;
        product = NonzeroVersion(product); file = NonzeroVersion(file);
        string? version = product ?? file;
        if (product is not null && file?.Contains('-') == true && !product.Contains('-')) return null;
        bool qualifiedBuild = info.IsPreRelease || info.IsPrivateBuild || info.IsSpecialBuild || info.IsDebug;
        // A complete resource string is an observed DLL version, not a claim
        // that the vendor considers this a stable public release.
        if (version is not null) return version;
        // Fixed numeric fields are useful when strings are absent, but neither
        // explicit zero placeholders nor build flags establish a public version.
        if (qualifiedBuild || !string.IsNullOrWhiteSpace(info.ProductVersion) ||
            !string.IsNullOrWhiteSpace(info.FileVersion)) return null;
        return FixedVersion(info.ProductMajorPart, info.ProductMinorPart, info.ProductBuildPart, info.ProductPrivatePart) ??
            FixedVersion(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
    }

    internal static string? NormalizePluginVersion(string? value)
    {
        if (value is null || value.Length > 64) return null;
        value = value.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        var qualified = Regex.Match(value,
            @"\A([0-9]{1,6}(?:\.[0-9]{1,6}){1,3})[ \t]+(alpha|beta|rc|preview|dev)[ \t]+([0-9]{1,6}(?:\.[0-9]{1,6})?)\z",
            RegexOptions.CultureInvariant);
        if (qualified.Success) value = $"{qualified.Groups[1].Value}-{qualified.Groups[2].Value}.{qualified.Groups[3].Value}";
        if (Regex.IsMatch(value, @"\A[0-9]{1,6}(?:[ \t]*,[ \t]*[0-9]{1,6}){1,3}\z", RegexOptions.CultureInvariant))
            value = Regex.Replace(value, @"[ \t]*,[ \t]*", ".", RegexOptions.CultureInvariant);
        return ReportingWire.Version(value) ? value : null;
    }

    private static string? NonzeroVersion(string? value) => value is not null &&
        value.Split('-')[0].Any(character => character is >= '1' and <= '9') ? value : null;
    private static string? FixedVersion(int major, int minor, int build, int revision) =>
        major < 0 || minor < 0 || build < 0 || revision < 0 ? null :
        NonzeroVersion(FormattableString.Invariant($"{major}.{minor}.{build}.{revision}"));

    internal static string? PublicVersion(string? value)
    {
        if (value is null || value.Length > 64) return null;
        if (value.StartsWith('v')) value = value[1..];
        if (ReportingWire.Version(value)) return value;
        var package = Regex.Match(value, @"\A([0-9]{4})/([0-9]{2}) {1,2}rev\.([0-9]{1,6})\z", RegexOptions.CultureInvariant);
        return package.Success ? $"{package.Groups[1].Value}.{int.Parse(package.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)}.{int.Parse(package.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture)}" : null;
    }

    private static ReportingInventoryEntry Entry(ReportingInventoryComponent component, ReportingInventoryStatus status, string? version = null) =>
        new(component, status, ReportingWire.InventorySource(component), version);
    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    private static bool IsReadError(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException or
        NotSupportedException or System.Security.SecurityException or System.ComponentModel.Win32Exception;

    private readonly record struct NativeFileIdentity(uint Volume, uint IndexHigh, uint IndexLow);

    // Opening each ancestor itself (not its reparse target) and holding it without
    // delete sharing prevents replacement while metadata is being inspected.
    // The DLL handle also denies writes; GetVersionInfo reads resource metadata only.
    private sealed class PathLease : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = [];
        internal string Root { get; private init; } = "";
        internal static PathLease OpenRoot(string path)
        {
            if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
                throw new IOException("A local configured directory is required.");
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            string volume = Path.GetPathRoot(full) ?? throw new IOException("Missing volume.");
            if (full.Length <= volume.Length || full.AsSpan(2).Contains(':') ||
                new DriveInfo(volume).DriveType is not (DriveType.Fixed or DriveType.Removable))
                throw new IOException("Invalid local configured directory.");
            var lease = new PathLease { Root = full };
            try
            {
                string current = volume;
                lease.Open(current, directory: true);
                foreach (string part in full[volume.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
                { current = Path.Combine(current, part); lease.Open(current, directory: true); }
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }

        internal void Open(string path, bool directory) => _handles.Add(OpenHandle(path, directory));
        internal SafeFileHandle OpenFile(string path) => OpenHandle(path, directory: false);
        internal NativeFileIdentity OpenDirectory(string path)
        {
            var handle = OpenHandle(path, directory: true);
            try
            {
                var identity = Identity(handle); _handles.Add(handle); return identity;
            }
            catch { handle.Dispose(); throw; }
        }
        internal static NativeFileIdentity Identity(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandle(handle, out var info)) throw new IOException("Cannot verify this configured file identity.");
            return new(info.Volume, info.IndexHigh, info.IndexLow);
        }

        private static SafeFileHandle OpenHandle(string path, bool directory)
        {
            const uint ReadAttributes = 0x80, ReadData = 1, ShareRead = 1, ShareWrite = 2, OpenExisting = 3,
                BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
            // Attribute access alone can succeed on an exclusively locked file;
            // require readable bytes before treating missing metadata as unknown.
            var handle = CreateFile(path, directory ? ReadAttributes : ReadAttributes | ReadData,
                directory ? ShareRead | ShareWrite : ShareRead,
                IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Cannot safely inspect this configured path."); }
            if (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 ||
                ((info.Attributes & (uint)FileAttributes.Directory) != 0) != directory)
            { handle.Dispose(); throw new IOException("The configured path is not an ordinary file or directory."); }
            return handle;
        }
        public void Dispose() { for (int i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose(); }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
    }
}
