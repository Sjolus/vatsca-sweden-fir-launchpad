using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VatscaUpdateChecker.Models;
using Microsoft.Win32.SafeHandles;

namespace VatscaUpdateChecker.Services;

/// <summary>
/// Compares complete, already-installed GNG packages. This service never installs an update.
/// Unknown, modified, referenced and staged files are deliberately retained.
/// </summary>
public static class GngCleanupService
{
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private static readonly Regex Generation = new(
        @"^ESAA-Sweden_(\d{14})-(\d{6})-(\d{4})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PluginKey = new(@"^Plugin\d+$", RegexOptions.CultureInvariant);
    private static readonly Regex UpdateOnly = new(@"update[ _-]*only", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex FullPackageName = new(
        @"^ESAA-Full-Package_(\d{14}-\d{6}-\d{4})(?: \(\d+\))?\.zip$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ReferenceExtensions = new(Paths)
        { ".sct", ".ese", ".rwy", ".prf", ".asr", ".dll" };
    private static readonly Encoding ProfileEncoding = CreateProfileEncoding();
    private const string ManifestName = "cleanup-manifest.json";
    private const long MaxEntryLength = 512L * 1024 * 1024;
    private const long MaxPackageLength = 2L * 1024 * 1024 * 1024;

    private sealed record PackageFile(string Path, long Length, string Hash);
    private sealed record Package(string Hash, Dictionary<string, PackageFile> Files, HashSet<string> Plugins);
    private sealed record BackupEntry(string RelativePath, long Length, string Hash);
    private sealed record BackupManifest(int Version, string OriginalRoot, DateTime CreatedUtc, List<BackupEntry> Entries);

    // Tests supply isolated storage, a synthetic process probe and a uniquely named gate.
    // Production callers cannot replace any of these operation boundaries.
    internal sealed record OperationContext(string BackupRoot, Action RequireClosed, Func<IDisposable> AcquireLock, Action<string> RequireNoPendingUpdate);

    private static OperationContext ProductionContext() => new(BackupBase(), RequireEuroScopeClosed, () =>
    {
        if (!MaintenanceLock.TryAcquire(out var lease))
            throw new InvalidOperationException("Another Launchpad maintenance operation is active. Wait for it to finish, then try again.");
        return lease!;
    }, GngUpdateService.RequireNoPendingUpdate);

    public static GngCleanupPlan BuildPlan(string root, string oldZip, string newZip, string? selectedProfile = null,
        IEnumerable<string>? protectedPaths = null)
        => BuildPlan(root, oldZip, newZip, ProductionContext(), selectedProfile, protectedPaths);

    internal static GngCleanupPlan BuildPlan(string root, string oldZip, string newZip, OperationContext context,
        string? selectedProfile = null, IEnumerable<string>? protectedPaths = null)
    {
        root = ExistingDirectory(root, "EuroScope data folder");
        context.RequireNoPendingUpdate(root);
        var protectedLocations = NormalizeProtectedPaths(protectedPaths);
        oldZip = ExistingFile(oldZip, "Previous complete GNG ZIP");
        newZip = ExistingFile(newZip, "Current complete GNG ZIP");
        if (Paths.Equals(oldZip, newZip))
            throw new InvalidOperationException("Select two different complete GNG ZIP packages.");

        var oldPackage = ReadPackage(oldZip);
        var newPackage = ReadPackage(newZip);
        if (oldPackage.Hash == newPackage.Hash)
            throw new InvalidOperationException("The previous and current packages have identical contents.");
        RequireNewerGeneration(oldPackage, newPackage);

        // A downloaded ZIP does not mean its data is installed. Verify every SCT/ESE file
        // in the current package. RWYs can legitimately change during EuroScope use.
        foreach (var file in newPackage.Files.Values.Where(f => IsGenerated(f.Path) &&
                     !Path.GetExtension(f.Path).Equals(".rwy", StringComparison.OrdinalIgnoreCase)))
        {
            var installed = ContainedPath(root, file.Path);
            EnsureNoReparse(installed);
            if (!File.Exists(installed) || HashFile(installed) != file.Hash)
                throw new InvalidOperationException(
                    $"The current package is not fully installed: {file.Path} is missing or differs from the ZIP. " +
                    "Install the package first, then review cleanup again. Cleanup does not install updates.");
        }

        var selected = ResolveSelectedProfile(root, selectedProfile);
        if (selected is not null && !selected.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected profile is outside the EuroScope data folder. Its external references cannot be checked for cleanup.");
        var references = ReadLocalReferences(root);
        var modifiedGenerations = new HashSet<string>(Paths);
        foreach (var source in oldPackage.Files.Values.Where(f => IsGenerated(f.Path) &&
                     !Path.GetExtension(f.Path).Equals(".rwy", StringComparison.OrdinalIgnoreCase)))
        {
            var localSource = ContainedPath(root, source.Path);
            EnsureNoReparse(localSource);
            if (File.Exists(localSource) && HashFile(localSource) != source.Hash)
                modifiedGenerations.Add(Path.GetFileNameWithoutExtension(source.Path));
        }
        var candidates = new List<GngCleanupCandidate>();
        foreach (var old in oldPackage.Files.Values.OrderBy(f => f.Path, Paths))
        {
            if (newPackage.Files.ContainsKey(old.Path) || IsStaged(old.Path)) continue;
            var kind = CandidateKind(old.Path, oldPackage.Plugins);
            if (kind is null) continue;

            var local = ContainedPath(root, old.Path);
            EnsureNoReparse(local);
            if (!File.Exists(local)) continue;
            var info = new FileInfo(local);
            var hash = HashFile(local);
            string? blocked = null;
            if (Paths.Equals(local, selected))
                blocked = "This is the selected EuroScope profile.";
            else if (IsProtected(local, protectedLocations))
                blocked = "This file belongs to another configured application or protected data folder.";
            else if (hash != old.Hash)
                blocked = "The local file differs from the previous package; preserving local changes.";
            else if (IsGenerated(old.Path) && modifiedGenerations.Contains(Path.GetFileNameWithoutExtension(old.Path)))
                blocked = "A local SCT/ESE companion differs from the previous package; preserving the entire modified generation.";
            else if (FindReference(old.Path, references) is { } referencedBy)
                blocked = $"Referenced by {referencedBy}; retained profiles and radar screens must remain usable.";

            candidates.Add(new(old.Path, info.Length,
                $"{kind} supplied by the previous package and absent from the current complete package.",
                blocked, hash, old.Hash));
        }

        // EuroScope creates RWY settings locally; official ZIPs normally contain only
        // SCT/ESE. Their generation name is usable provenance only while BOTH retired
        // source files still match the old package. No other local-only file is inferred.
        foreach (var oldSector in oldPackage.Files.Values.Where(f => IsGenerated(f.Path) &&
                     Path.GetExtension(f.Path).Equals(".sct", StringComparison.OrdinalIgnoreCase)))
        {
            var stem = Path.GetFileNameWithoutExtension(oldSector.Path);
            var esePath = stem + ".ese";
            var rwyPath = stem + ".rwy";
            if (oldPackage.Files.ContainsKey(rwyPath) || newPackage.Files.ContainsKey(rwyPath) ||
                newPackage.Files.ContainsKey(oldSector.Path) || newPackage.Files.ContainsKey(esePath)) continue;
            var localRwy = ContainedPath(root, rwyPath);
            EnsureNoReparse(localRwy);
            if (!File.Exists(localRwy)) continue;
            var oldEse = oldPackage.Files[esePath];
            string? blocked = null;
            foreach (var source in new[] { oldSector, oldEse })
            {
                var localSource = ContainedPath(root, source.Path);
                EnsureNoReparse(localSource);
                if (!File.Exists(localSource) || HashFile(localSource) != source.Hash)
                {
                    blocked = "The retired SCT/ESE source files are missing or modified; this local runway file cannot be safely attributed.";
                    break;
                }
            }
            if (blocked is null && FindReference(rwyPath, references) is { } referencedBy)
                blocked = $"Referenced generation used by {referencedBy}; its runway settings must be retained.";
            if (IsProtected(localRwy, protectedLocations))
                blocked = "This file belongs to another configured application or protected data folder.";
            candidates.Add(new(rwyPath, new FileInfo(localRwy).Length,
                "Local runway settings for the retired package generation. EuroScope created this file; the matching SCT/ESE identify its generation.",
                blocked, HashFile(localRwy), "runtime-companion:" + oldSector.Hash + oldEse.Hash));
        }

        var warnings = new List<string>
        {
            "Only previous-package files and verified same-generation local runway settings are considered. Custom and unknown files are retained.",
            "Updated Plugin folders are always retained. Promote staged plugin updates separately with EuroScope closed.",
            "All local profiles and radar screens protect their file references, including profiles listed for cleanup."
        };
        return new(root, oldZip, newZip, selected, candidates.OrderBy(c => c.RelativePath, Paths).ToArray(), warnings,
            oldPackage.Hash, newPackage.Hash, protectedLocations);
    }

    public static GngCleanupResult ArchiveSelected(GngCleanupPlan plan, IEnumerable<string> selectedRelativePaths)
        => ArchiveSelected(plan, selectedRelativePaths, ProductionContext());

    internal static GngCleanupResult ArchiveSelected(GngCleanupPlan plan, IEnumerable<string> selectedRelativePaths,
        OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(selectedRelativePaths);
        using var maintenance = context.AcquireLock();
        context.RequireNoPendingUpdate(plan.Root);
        context.RequireClosed();
        var selected = selectedRelativePaths.Select(NormalizeRelative).Distinct(Paths).ToList();
        if (selected.Count == 0) return new(null, Array.Empty<string>(), Array.Empty<string>());

        // Re-read both archives, installed generations and every local reference. A preview
        // never authorizes a file whose state or package provenance has since changed.
        var fresh = BuildPlan(plan.Root, plan.OldZip, plan.NewZip, context, plan.SelectedProfile, plan.ProtectedPaths);
        if (fresh.OldZipHash != plan.OldZipHash || fresh.NewZipHash != plan.NewZipHash)
            throw new InvalidOperationException("A selected ZIP changed after preview. Review cleanup again.");

        var before = plan.Candidates.ToDictionary(c => c.RelativePath, Paths);
        var now = fresh.Candidates.ToDictionary(c => c.RelativePath, Paths);
        var pending = new List<GngCleanupCandidate>();
        var skipped = new List<string>();
        foreach (var relative in selected)
        {
            if (!before.TryGetValue(relative, out var preview) || !preview.IsEligible)
                skipped.Add($"{relative}: was not eligible in the preview.");
            else if (!now.TryGetValue(relative, out var current))
                skipped.Add($"{relative}: is no longer a cleanup candidate.");
            else if (!current.IsEligible)
                skipped.Add($"{relative}: {current.BlockReason}");
            else if (current.LocalHash != preview.LocalHash || current.PackageHash != preview.PackageHash || current.Length != preview.Length)
                skipped.Add($"{relative}: changed since preview.");
            else pending.Add(current);
        }
        if (pending.Count == 0) return new(null, Array.Empty<string>(), skipped);

        // Keep replacement sectors and selected source files stable for the entire operation.
        // Retired sector companions also stay pinned, including after our own rename, so an
        // external editor cannot invalidate generation-wide preservation between file moves.
        context.RequireClosed();
        using var pinned = PinArchiveFiles(fresh, pending);

        var backupBase = ResolveLocalPath(context.BackupRoot);
        EnsureOutsideRoot(fresh.Root, backupBase);
        if (!Paths.Equals(Path.GetPathRoot(fresh.Root), Path.GetPathRoot(backupBase)))
            throw new InvalidOperationException("The backup and EuroScope folders must be on the same volume. Cross-volume cleanup is not supported.");
        EnsureNoReparse(backupBase);
        using var backupParents = new DirectoryGuard(backupBase, createMissing: true);
        var backup = Path.Combine(backupBase, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
        EnsureOutsideRoot(fresh.Root, backup);
        using var backupDirectory = new DirectoryGuard(backup, createMissing: true);
        var manifest = new BackupManifest(1, fresh.Root, DateTime.UtcNow,
            pending.Select(c => new BackupEntry(c.RelativePath, c.Length, c.LocalHash)).ToList());
        // Write the recovery inventory before the first move. If interrupted, Restore checks
        // which listed files actually reached the backup and verifies their hashes.
        WriteManifest(backup, manifest);

        var moved = new List<string>();
        foreach (var candidate in pending)
        {
            try
            {
                context.RequireClosed();
                if (FindReference(candidate.RelativePath, ReadLocalReferences(fresh.Root)) is { } referencedBy)
                {
                    skipped.Add($"{candidate.RelativePath}: now referenced by {referencedBy}.");
                    continue;
                }
                var destination = ContainedPath(backup, candidate.RelativePath);
                pinned.Move(candidate.RelativePath, destination);
                moved.Add(candidate.RelativePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                skipped.Add($"{candidate.RelativePath}: {ex.Message}");
            }
        }
        return new(backup, moved, skipped);
    }

    public static GngCleanupResult Restore(string backupFolder, string expectedRoot, IEnumerable<string>? protectedPaths = null)
        => Restore(backupFolder, expectedRoot, protectedPaths, ProductionContext());

    internal static GngCleanupResult Restore(string backupFolder, string expectedRoot, IEnumerable<string>? protectedPaths,
        OperationContext context)
    {
        using var maintenance = context.AcquireLock();
        context.RequireClosed();
        expectedRoot = ExistingDirectory(expectedRoot, "Reviewed EuroScope data folder");
        var protectedLocations = NormalizeProtectedPaths(protectedPaths);
        var backup = ExistingDirectory(backupFolder, "Cleanup backup folder");
        var backupBase = ResolveLocalPath(context.BackupRoot);
        if (!Paths.Equals(Path.GetDirectoryName(backup), backupBase))
            throw new InvalidOperationException("Select a backup directly inside the Launchpad CleanupBackups folder.");
        var manifestPath = ContainedPath(backup, ManifestName);
        EnsureNoReparse(manifestPath);
        if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 16 * 1024 * 1024)
            throw new InvalidOperationException("The cleanup backup manifest is missing or invalid.");
        BackupManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath))
                ?? throw new InvalidOperationException("The cleanup backup manifest is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The cleanup backup manifest is invalid.", ex);
        }
        if (manifest.Version != 1 || manifest.Entries is null || manifest.Entries.Count > 50000 ||
            string.IsNullOrWhiteSpace(manifest.OriginalRoot) || !Path.IsPathFullyQualified(manifest.OriginalRoot))
            throw new InvalidOperationException("The cleanup backup manifest has an unsupported format.");
        var root = ExistingDirectory(manifest.OriginalRoot, "Original EuroScope data folder");
        if (!Paths.Equals(root, expectedRoot))
            throw new InvalidOperationException("This backup belongs to a different EuroScope data folder. Nothing was restored.");
        EnsureOutsideRoot(root, backup);
        var entries = new HashSet<string>(Paths);
        foreach (var item in manifest.Entries)
        {
            if (item is null) throw new InvalidOperationException("The cleanup backup manifest contains an empty file entry.");
            var relative = NormalizeRelative(item.RelativePath);
            if (!entries.Add(relative) || !IsRestorable(relative) || item.Length < 0 || item.Length > MaxEntryLength ||
                item.Hash is null || !Regex.IsMatch(item.Hash, @"\A[0-9A-F]{64}\z"))
                throw new InvalidOperationException("The cleanup backup manifest contains an invalid file entry.");
        }

        var restored = new List<string>();
        var skipped = new List<string>();
        foreach (var item in manifest.Entries)
        {
            try
            {
                context.RequireClosed();
                var source = ContainedPath(backup, item.RelativePath);
                var destination = ContainedPath(root, item.RelativePath);
                if (IsProtected(destination, protectedLocations))
                {
                    skipped.Add($"{item.RelativePath}: belongs to another configured application or protected data folder.");
                    continue;
                }
                EnsureNoReparse(source);
                EnsureNoReparse(destination);
                if (!File.Exists(source))
                {
                    skipped.Add($"{item.RelativePath}: not present in the backup (possibly already restored).");
                    continue;
                }
                if (File.Exists(destination) || Directory.Exists(destination))
                {
                    skipped.Add($"{item.RelativePath}: a destination already exists; nothing was overwritten.");
                    continue;
                }
                MoveVerifiedFile(source, destination, item.Length, item.Hash);
                restored.Add(item.RelativePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                skipped.Add($"{item.RelativePath}: {ex.Message}");
            }
        }
        return new(backup, restored, skipped);
    }

    private static Package ReadPackage(string zipPath)
    {
        if (!Path.GetExtension(zipPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Package comparison supports complete GNG ZIP files only.");
        if (UpdateOnly.IsMatch(Path.GetFileName(zipPath)))
            throw new InvalidOperationException("Update Only packages cannot prove which files were removed. Select complete Full Package ZIPs.");
        var namedPackage = FullPackageName.Match(Path.GetFileName(zipPath));
        if (!namedPackage.Success)
            throw new InvalidOperationException("Keep the original AeroNav ESAA-Full-Package_<timestamp>-<AIRAC>-<revision>.zip filename. Renamed or unverified package types cannot be used for cleanup.");

        using var file = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > MaxPackageLength)
            throw new InvalidOperationException("The ZIP exceeds the supported GNG package size.");
        var packageHash = HashStream(file);
        file.Position = 0;
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        if (zip.Entries.Count == 0 || zip.Entries.Count > 50000)
            throw new InvalidOperationException("The package has an invalid number of ZIP entries.");
        var raw = new List<(string Path, ZipArchiveEntry Entry)>();
        var names = new HashSet<string>(Paths);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            // Unix symlinks and Windows reparse entries must never enter a filesystem plan.
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The ZIP contains a symbolic link or reparse entry.");
            var name = NormalizeRelative(entry.FullName.TrimEnd('/', '\\'));
            if (!names.Add(name))
                throw new InvalidOperationException($"The ZIP contains a duplicate path: {name}.");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            total = checked(total + entry.Length);
            if (entry.Length > MaxEntryLength || total > MaxPackageLength)
                throw new InvalidOperationException("The ZIP exceeds the supported GNG package size.");
            raw.Add((name, entry));
        }
        if (raw.Count == 0) throw new InvalidOperationException("The ZIP contains no files.");

        // Accept one optional common wrapper, while rejecting ambiguous/multi-package roots.
        string? wrapper = null;
        if (raw.All(e => e.Path.Contains('\\')))
        {
            var first = raw[0].Path.Split('\\')[0];
            if (raw.All(e => Paths.Equals(e.Path.Split('\\')[0], first))) wrapper = first;
        }
        if (wrapper is not null && UpdateOnly.IsMatch(wrapper))
            throw new InvalidOperationException("The ZIP identifies itself as Update Only; complete Full Package ZIPs are required.");

        var files = new Dictionary<string, PackageFile>(Paths);
        var plugins = new HashSet<string>(Paths);
        foreach (var item in raw)
        {
            var relative = wrapper is null ? item.Path : item.Path[(wrapper.Length + 1)..];
            if (files.ContainsKey(relative)) throw new InvalidOperationException($"Duplicate package path: {relative}.");
            using var stream = item.Entry.Open();
            var hash = HashPackageEntry(stream, item.Entry.Length);
            files.Add(relative, new(relative, item.Entry.Length, hash));
            if (IsRootProfile(relative))
            {
                if (item.Entry.Length > 16 * 1024 * 1024)
                    throw new InvalidOperationException("A package profile is too large to safely inspect.");
                using var profile = item.Entry.Open();
                using var reader = new StreamReader(profile, ProfileEncoding, detectEncodingFromByteOrderMarks: true);
                while (reader.ReadLine() is { } line)
                {
                    var parts = line.Split('\t', 3);
                    if (parts.Length == 3 && parts[0] == "Plugins" && PluginKey.IsMatch(parts[1]))
                    {
                        var name = Path.GetFileName(parts[2].Trim().Trim('"').Replace('/', '\\'));
                        if (Path.GetExtension(name).Equals(".dll", StringComparison.OrdinalIgnoreCase)) plugins.Add(name);
                    }
                }
            }
        }
        // Reject file/directory collisions even when directory entries were omitted.
        foreach (var relative in files.Keys)
        {
            var parent = Path.GetDirectoryName(relative);
            while (!string.IsNullOrEmpty(parent))
            {
                if (files.ContainsKey(parent)) throw new InvalidOperationException("The ZIP has conflicting file and directory paths.");
                parent = Path.GetDirectoryName(parent);
            }
        }
        var generated = files.Keys.Where(IsGenerated).ToList();
        var sectors = generated.Where(p => Path.GetExtension(p).Equals(".sct", StringComparison.OrdinalIgnoreCase)).ToList();
        if (!files.Keys.Any(IsRootProfile) || sectors.Count == 0 ||
            !files.Keys.Any(p => IsPluginPath(p) && !IsStaged(p)))
            throw new InvalidOperationException("The ZIP does not look like a complete GNG package: root profiles, generated sectors and ESAA plugin DLLs are required.");
        foreach (var generatedFile in generated)
        {
            var stem = Path.GetFileNameWithoutExtension(generatedFile);
            if (!files.ContainsKey(stem + ".sct") || !files.ContainsKey(stem + ".ese"))
                throw new InvalidOperationException($"The ZIP has an incomplete sector generation: {stem}. Matching SCT and ESE files are required.");
        }
        if (sectors.Any(p => !Paths.Equals(Path.GetFileNameWithoutExtension(p), "ESAA-Sweden_" + namedPackage.Groups[1].Value)))
            throw new InvalidOperationException("The sector generation does not match the original Full Package ZIP filename. Select the original complete archive.");
        return new(packageHash, files, plugins);
    }

    private static Dictionary<string, string> ReadLocalReferences(string root)
    {
        var references = new Dictionary<string, string>(Paths);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            EnsureNoReparse(directory);
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                EnsureNoReparse(child);
                pending.Push(child);
            }
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var extension = Path.GetExtension(file);
                var isProfile = extension.Equals(".prf", StringComparison.OrdinalIgnoreCase);
                if (!isProfile && !extension.Equals(".asr", StringComparison.OrdinalIgnoreCase)) continue;
                EnsureNoReparse(file);
                if (new FileInfo(file).Length > 16 * 1024 * 1024)
                    throw new InvalidOperationException($"A profile or radar screen is too large to safely inspect: {Path.GetRelativePath(root, file)}.");
                foreach (var line in ReadReferenceLines(file))
                {
                    string? value = null;
                    if (isProfile)
                    {
                        var fields = line.Split('\t', 3);
                        // Credentials belong to LastSession and are never collected or logged.
                        // Other three-column values ending in a file extension are path fields.
                        if (fields.Length == 3 && !fields[0].Equals("LastSession", StringComparison.OrdinalIgnoreCase))
                            value = fields[2];
                    }
                    else
                    {
                        var colon = line.IndexOf(':');
                        if (colon >= 0 && line[..colon].Equals("SECTORFILE", StringComparison.OrdinalIgnoreCase))
                            value = line[(colon + 1)..];
                    }
                    if (value is null) continue;
                    value = value.Trim().Trim('"').Replace('/', '\\');
                    if (!ReferenceExtensions.Contains(Path.GetExtension(value))) continue;
                    if (Path.GetExtension(value).Equals(".asr", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetExtension(value).Equals(".prf", StringComparison.OrdinalIgnoreCase))
                    {
                        var linked = ResolveLocalPath(Path.IsPathFullyQualified(value) ? value : Path.Combine(root, value));
                        if (!linked.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || !File.Exists(linked))
                            throw new InvalidOperationException(
                                $"{Path.GetRelativePath(root, file)} refers to \"{value}\". " +
                                "That profile or radar screen is missing or outside the EuroScope data folder, so cleanup cannot check which files it uses. " +
                                "Review this reference before trying again.");
                        EnsureNoReparse(linked);
                    }
                    // A basename match deliberately protects ambiguous relative/absolute paths,
                    // including references from profiles outside the main ES*.prf picker.
                    var basename = Path.GetFileName(value);
                    if (!string.IsNullOrWhiteSpace(basename))
                        references.TryAdd(basename, Path.GetRelativePath(root, file));
                    // Existing Windows short names can identify the same long package path.
                    // Resolve metadata only; never read contents from an external reference.
                    var referenced = ResolveLocalPath(Path.IsPathFullyQualified(value) ? value : Path.Combine(root, value));
                    references.TryAdd(Path.GetFileName(referenced), Path.GetRelativePath(root, file));
                }
            }
        }
        return references;
    }

    private static IEnumerable<string> ReadReferenceLines(string path)
    {
        // Archive pins may hold DELETE access for an eventual rename. They still deny
        // other writers and deleters, while this reader permits that existing handle.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var reader = new StreamReader(stream, ProfileEncoding, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line) yield return line;
    }

    private static string? CandidateKind(string relative, HashSet<string> knownPlugins)
    {
        if (IsGenerated(relative)) return "Old generated sector file";
        if (IsRootProfile(relative)) return "Retired package profile";
        if (IsPluginPath(relative) && knownPlugins.Contains(Path.GetFileName(relative))) return "Retired profile-loaded plugin";
        return null;
    }

    private static string? FindReference(string relative, Dictionary<string, string> references)
    {
        if (references.TryGetValue(Path.GetFileName(relative), out var source)) return source;
        if (IsGenerated(relative))
        {
            // EuroScope resolves ESE/RWY companions from its SCT generation, even when
            // only the SCT appears explicitly in a profile or radar-screen path field.
            var stem = Path.GetFileNameWithoutExtension(relative);
            foreach (var extension in new[] { ".sct", ".ese", ".rwy" })
                if (references.TryGetValue(stem + extension, out source)) return source;
        }
        return null;
    }

    private static void RequireNewerGeneration(Package oldPackage, Package newPackage)
    {
        static (long Timestamp, int Airac, int Revision) Latest(Package package)
        {
            return package.Files.Keys.Where(p => IsGenerated(p) &&
                    Path.GetExtension(p).Equals(".sct", StringComparison.OrdinalIgnoreCase))
                .Select(p => Generation.Match(Path.GetFileNameWithoutExtension(p)))
                .Select(m => (Timestamp: long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                    Airac: int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                    Revision: int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)))
                .OrderByDescending(g => g.Timestamp).First();
        }
        var old = Latest(oldPackage);
        var current = Latest(newPackage);
        if (current.Timestamp <= old.Timestamp || current.Airac < old.Airac ||
            (current.Airac == old.Airac && current.Revision < old.Revision))
            throw new InvalidOperationException("The current package must have a newer sector generation than the previous package. Check the ZIP selection order.");
    }

    private static bool IsGenerated(string relative)
    {
        if (relative.Contains('\\')) return false;
        var extension = Path.GetExtension(relative);
        if (!extension.Equals(".sct", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".ese", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".rwy", StringComparison.OrdinalIgnoreCase)) return false;
        var match = Generation.Match(Path.GetFileNameWithoutExtension(relative));
        return match.Success && DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMddHHmmss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    private static bool IsRootProfile(string path) => !path.Contains('\\') &&
        Path.GetExtension(path).Equals(".prf", StringComparison.OrdinalIgnoreCase);
    private static bool IsPluginPath(string path) => path.StartsWith("ESAA\\Plugins\\", StringComparison.OrdinalIgnoreCase) &&
        Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase);
    private static bool IsStaged(string path) => path.Split('\\').Any(p => p.StartsWith("Updated Plugin", StringComparison.OrdinalIgnoreCase));
    private static bool IsRestorable(string path) => !IsStaged(path) && (IsGenerated(path) || IsRootProfile(path) || IsPluginPath(path));

    private static string NormalizeRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("A file path is empty.");
        path = path.Replace('/', '\\');
        if (Path.IsPathRooted(path)) throw new InvalidOperationException("Absolute paths are not allowed in cleanup entries.");
        var parts = path.Split('\\');
        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new InvalidOperationException("An archive or cleanup entry contains an unsafe file path.");
        }
        return string.Join('\\', parts);
    }

    private static string ContainedPath(string root, string relative)
    {
        relative = NormalizeRelative(relative);
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A cleanup path escaped its permitted folder.");
        return path;
    }

    private static string ExistingDirectory(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException($"{label} is not configured.");
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException($"{label} must be a full local path.");
        path = ResolveLocalPath(path);
        if (Paths.Equals(path, Path.GetPathRoot(path))) throw new InvalidOperationException($"{label} cannot be a drive root.");
        EnsureNoReparse(path);
        if (!Directory.Exists(path)) throw new InvalidOperationException($"{label} does not exist.");
        return path;
    }

    private static string ExistingFile(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException($"Select the {label}.");
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException($"{label} must be a full local path.");
        path = ResolveLocalPath(path);
        EnsureNoReparse(path);
        if (!File.Exists(path)) throw new InvalidOperationException($"{label} does not exist.");
        return path;
    }

    private static string? ResolveSelectedProfile(string root, string? selected)
    {
        if (string.IsNullOrWhiteSpace(selected)) return null;
        return ResolveLocalPath(Path.IsPathFullyQualified(selected) ? selected : Path.Combine(root, selected));
    }

    private static void EnsureNoReparse(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Cleanup does not follow symbolic links, junctions or other reparse points.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void EnsureOutsideRoot(string root, string backup)
    {
        if (Paths.Equals(root, backup) || backup.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cleanup backups must be outside the EuroScope data folder.");
    }

    private static string[] NormalizeProtectedPaths(IEnumerable<string>? paths)
    {
        var result = new HashSet<string>(Paths);
        foreach (var path in paths ?? [])
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
                throw new InvalidOperationException("A configured protected path is not a full local path. Check application paths before cleanup.");
            result.Add(ResolveLocalPath(path));
        }
        return result.ToArray();
    }

    private static bool IsProtected(string path, IEnumerable<string> protectedPaths) => protectedPaths.Any(p =>
        Paths.Equals(path, p) || path.StartsWith(p.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ||
        p.StartsWith(path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

    private static string ResolveLocalPath(string path)
    {
        if (path.Replace('/', '\\').Split('\\').Any(part => part is not "." and not ".." && (part.EndsWith(' ') || part.EndsWith('.'))))
            throw new InvalidOperationException("A cleanup path contains an ambiguous Windows path component.");
        path = PreservationPath.Normalize(path);
        EnsureNoReparse(path);
        var resolved = PreservationPath.Resolve(path);
        EnsureNoReparse(resolved);
        return resolved;
    }

    private static string BackupBase() => Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VatscaUpdateChecker", "CleanupBackups"));

    private static void RequireEuroScopeClosed() => EuroScopeProcessGuard.RequireClosed();

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return HashStream(stream);
    }
    private static string HashStream(Stream stream) => Convert.ToHexString(SHA256.HashData(stream));

    private static string HashPackageEntry(Stream stream, long expectedLength)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long length = 0;
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            length += count;
            if (length > expectedLength || length > MaxEntryLength)
                throw new InvalidOperationException("A ZIP entry expands beyond its declared size.");
            hash.AppendData(buffer, 0, count);
        }
        if (length != expectedLength)
            throw new InvalidOperationException("A ZIP entry does not match its declared size.");
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static PinnedFiles PinArchiveFiles(GngCleanupPlan plan, IReadOnlyList<GngCleanupCandidate> pending)
    {
        var oldPackage = ReadPackage(plan.OldZip);
        var newPackage = ReadPackage(plan.NewZip);
        if (oldPackage.Hash != plan.OldZipHash || newPackage.Hash != plan.NewZipHash)
            throw new InvalidOperationException("A selected ZIP changed after preview. Review cleanup again.");
        var files = new Dictionary<string, (long Length, string Hash, bool Move)>(Paths);
        foreach (var item in newPackage.Files.Values.Where(f => IsGenerated(f.Path) &&
                     !Path.GetExtension(f.Path).Equals(".rwy", StringComparison.OrdinalIgnoreCase)))
            files.Add(item.Path, (item.Length, item.Hash, false));
        var retiredGenerations = pending.Where(c => IsGenerated(c.RelativePath))
            .Select(c => Path.GetFileNameWithoutExtension(c.RelativePath)).ToHashSet(Paths);
        var inferredGenerations = pending.Where(c => c.PackageHash.StartsWith("runtime-companion:", StringComparison.Ordinal))
            .Select(c => Path.GetFileNameWithoutExtension(c.RelativePath)).ToHashSet(Paths);
        foreach (var item in oldPackage.Files.Values.Where(f => IsGenerated(f.Path) &&
                     !Path.GetExtension(f.Path).Equals(".rwy", StringComparison.OrdinalIgnoreCase) &&
                     retiredGenerations.Contains(Path.GetFileNameWithoutExtension(f.Path))))
        {
            if (inferredGenerations.Contains(Path.GetFileNameWithoutExtension(item.Path)) ||
                File.Exists(ContainedPath(plan.Root, item.Path)))
                files[item.Path] = (item.Length, item.Hash, false);
        }
        foreach (var item in pending) files[item.RelativePath] = (item.Length, item.LocalHash, true);
        var pinned = new PinnedFiles();
        try
        {
            foreach (var item in files)
                pinned.Add(item.Key, new VerifiedSource(ContainedPath(plan.Root, item.Key),
                    item.Value.Length, item.Value.Hash, item.Value.Move));
            return pinned;
        }
        catch { pinned.Dispose(); throw; }
    }

    private sealed class PinnedFiles : IDisposable
    {
        private readonly Dictionary<string, VerifiedSource> _files = new(Paths);
        public void Add(string relative, VerifiedSource source) => _files.Add(relative, source);
        public void Move(string relative, string destination) => _files[relative].Move(destination);
        public void Dispose() { foreach (var file in _files.Values) file.Dispose(); _files.Clear(); }
    }

    private sealed class VerifiedSource : IDisposable
    {
        private readonly DirectoryGuard _parents;
        private readonly FileStream _stream;
        public VerifiedSource(string source, long expectedLength, string expectedHash, bool allowMove)
        {
            _parents = new DirectoryGuard(Path.GetDirectoryName(source)!, createMissing: false);
            SafeFileHandle? handle = null;
            try
            {
                handle = CreateFile(source, 0x80000000 | (allowMove ? 0x00010000u : 0u), 1,
                    IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
                if (handle.IsInvalid) throw NativeIOException("Could not exclusively open the file for cleanup");
                CheckHandleType(handle, directory: false);
                _stream = new FileStream(handle, FileAccess.Read);
                if (_stream.Length != expectedLength || HashStream(_stream) != expectedHash)
                    throw new InvalidOperationException("A file changed after preview or a backup differs from its manifest; it was retained.");
            }
            catch { handle?.Dispose(); _parents.Dispose(); throw; }
        }
        public void Move(string destination)
        {
            using var destinationParents = new DirectoryGuard(Path.GetDirectoryName(destination)!, createMissing: true);
            RenameHandle(_stream.SafeFileHandle, destination);
        }
        public void Dispose() { _stream.Dispose(); _parents.Dispose(); }
    }

    // Identity-preserving Windows rename: the file whose bytes are hashed is the file
    // renamed. No pathname-based delete/move is used after validation. These APIs also
    // refuse cross-volume renames; there is deliberately no copy-and-delete fallback.
    private static void MoveVerifiedFile(string source, string destination, long expectedLength, string expectedHash)
    {
        using var file = new VerifiedSource(source, expectedLength, expectedHash, allowMove: true);
        file.Move(destination);
    }

    private static void RenameHandle(SafeFileHandle handle, string destination)
    {
        // Use the documented Win32 absolute-name form. Both ancestor chains remain
        // locked, so the target cannot be redirected by renaming a parent directory.
        var name = Encoding.Unicode.GetBytes(destination);
        // FILE_RENAME_INFO is naturally aligned: BOOLEAN/flags at 0, HANDLE at 4/8,
        // DWORD byte length at 8/16, followed by WCHAR name at 12/20 (x86/x64).
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        var bufferSize = nameOffset + name.Length + sizeof(char);
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            Marshal.Copy(new byte[bufferSize], 0, buffer, bufferSize);
            // ReplaceIfExists remains false: collisions are always preserved.
            // RootDirectory remains NULL for the fully-qualified Win32 destination.
            Marshal.WriteInt32(buffer, lengthOffset, name.Length);
            Marshal.Copy(name, 0, IntPtr.Add(buffer, nameOffset), name.Length);
            if (!SetFileInformationByHandle(handle, 3, buffer, (uint)bufferSize))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 17)
                    throw new IOException("The backup and EuroScope folders are on different volumes. The file was retained; cross-volume cleanup is not supported.");
                throw new IOException("Could not rename the verified file: " + new Win32Exception(error).Message);
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Locks directory identities against renaming while a move resolves its paths.</summary>
    private sealed class DirectoryGuard : IDisposable
    {
        private readonly List<SafeFileHandle> _handles = new();
        public DirectoryGuard(string directory, bool createMissing)
        {
            var chain = new Stack<string>();
            for (var path = Path.GetFullPath(directory); !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
                chain.Push(path);
            try
            {
                foreach (var path in chain)
                {
                    // The parent is already held, so a newly created child cannot be
                    // redirected through an ancestor rename between creation and opening.
                    if (createMissing && !Directory.Exists(path)) Directory.CreateDirectory(path);
                    var handle = CreateFile(path, 0x00000080, 3, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
                    if (handle.IsInvalid)
                    {
                        var error = NativeIOException("Could not lock a cleanup directory");
                        handle.Dispose();
                        throw error;
                    }
                    _handles.Add(handle);
                    CheckHandleType(handle, directory: true);
                }
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            for (var i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose();
            _handles.Clear();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public uint Attributes;
        public uint ReparseTag;
    }

    private static void CheckHandleType(SafeFileHandle handle, bool directory)
    {
        if (!GetFileInformationByHandleEx(handle, 9, out var info, (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
            throw NativeIOException("Could not inspect a cleanup handle");
        if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 ||
            ((info.Attributes & (uint)FileAttributes.Directory) != 0) != directory)
            throw new InvalidOperationException("A cleanup path is a reparse point or changed its file type; it was retained.");
    }

    private static IOException NativeIOException(string message) =>
        new(message + ": " + new Win32Exception(Marshal.GetLastWin32Error()).Message);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, uint sharing, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, IntPtr info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
        out FileAttributeTagInfo info, uint size);

    private static Encoding CreateProfileEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    }

    private static void WriteManifest(string backup, BackupManifest manifest)
    {
        var path = ContainedPath(backup, ManifestName);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, manifest, new JsonSerializerOptions { WriteIndented = true });
        stream.Flush(flushToDisk: true);
    }
}
