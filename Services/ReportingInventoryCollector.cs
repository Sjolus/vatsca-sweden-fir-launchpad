using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

// Reads only the explicitly configured EuroScope roots and a fixed plugin catalogue.
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
        string? euroscopeDataFolder, string? euroscopeExecutableFolder)
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
                    ScanIfPresent(Path.Combine(esaa, "Plugins"), lease);
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
        return result.OrderBy(item => item.Key).Select(item => item.Value).ToArray();

        void ScanIfPresent(string path, PathLease lease)
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
                        lease.Open(child, directory: false);
                        if (new FileInfo(child).Length > 64L * 1024 * 1024) { complete = false; continue; }
                        var info = FileVersionInfo.GetVersionInfo(child);
                        found[component].Add(PublicVersion(info.ProductVersion) ?? PublicVersion(info.FileVersion));
                    }
                }
            }
        }
    }

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

        internal void Open(string path, bool directory)
        {
            const uint ReadAttributes = 0x80, ShareRead = 1, ShareWrite = 2, OpenExisting = 3,
                BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
            var handle = CreateFile(path, ReadAttributes, directory ? ShareRead | ShareWrite : ShareRead,
                IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Cannot safely inspect this configured path."); }
            if (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 ||
                ((info.Attributes & (uint)FileAttributes.Directory) != 0) != directory)
            { handle.Dispose(); throw new IOException("The configured path is not an ordinary file or directory."); }
            _handles.Add(handle);
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
