using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

/// <summary>Reviewed GNG package installation. Downloads/authentication are handled separately.</summary>
public static partial class GngUpdateService
{
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private static readonly Regex PackageName = new(@"^ESAA-(Full-Package|Update-Only)_(\d{14})-(\d{6})-(\d{4})(?: \(\d+\))?\.zip$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SectorName = new(@"^ESAA-Sweden_(\d{14})-(\d{6})-(\d{4})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PluginKey = new(@"^Plugin(\d+)(.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Encoding PrfEncoding = CreateEncoding();
    private const string ManifestName = "update-manifest.json";
    private const string PendingName = "pending-update.json";
    private const string CurrentName = "current-installation.json";
    private const long MaxFile = 512L * 1024 * 1024;
    private const long MaxTotal = 2L * 1024 * 1024 * 1024;
    private sealed record PackageEntry(string Path, string ZipName, long Length, string Hash, bool Promoted = false);
    private sealed record Package(string Hash, string Generation, Dictionary<string, PackageEntry> Entries, HashSet<string> ProfilePlugins);
    private sealed record SavedFile(string RelativePath, string? BeforeHash, long BeforeLength, string AfterHash, long AfterLength);
    private sealed record Journal(int Format, string DataFolder, string PackageName, string Generation, string PackageHash, string Status, List<SavedFile> Files);
    private sealed record Installation(string BackupFolder, string PackagePath, string PackageHash, string Generation, List<SavedFile> Files,
        string? CompletePackagePath = null, string? CompletePackageHash = null);
    private sealed record Pending(string BackupFolder);

    internal sealed record OperationContext(string StorageRoot, Action RequireClosed, Func<IDisposable> AcquireLock);
    private static OperationContext ProductionContext() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VatscaUpdateChecker", "Gng"), RequireClosed,
        () => MaintenanceLock.TryAcquire(out var lease) ? lease! : throw new InvalidOperationException("Another Launchpad installation, update or removal is in progress. Finish it before changing GNG."));

    // Validate the original archive before requesting its matching reference.
    // Destination/profile compatibility is reviewed only after both downloads finish.
    public static string ValidatePackage(string zipPath) => FormatVersion(ReadPackage(CanonicalPath(zipPath)).Generation);

    public static GngUpdatePlan BuildPlan(string zipPath, string dataFolder, IEnumerable<string>? protectedPaths = null, string? fullPackagePath = null, bool preserveListLayout = true) =>
        BuildPlan(zipPath, dataFolder, protectedPaths, ProductionContext(), fullPackagePath, preserveListLayout);

    internal static GngUpdatePlan BuildPlan(string zipPath, string dataFolder, IEnumerable<string>? protectedPaths, OperationContext context, string? fullPackagePath = null, bool preserveListLayout = true)
    {
        var root = ExistingDirectory(dataFolder);
        var protections = ValidateRoots(root, protectedPaths, context);
        var pending = GetPendingUpdate(root, context);
        if (pending is not null) throw new InvalidOperationException("An unfinished GNG update requires recovery first: " + pending);
        zipPath = CanonicalPath(zipPath);
        var package = ReadPackage(zipPath);
        Package? reference = null;
        if (fullPackagePath is not null)
        {
            fullPackagePath = CanonicalPath(fullPackagePath);
            if (!IsCompletePackage(fullPackagePath)) throw new InvalidOperationException("The reference ZIP must be an original Full Package.");
            reference = ReadPackage(fullPackagePath);
            if (!SamePackageVersion(package.Generation, reference.Generation))
                throw new InvalidOperationException("The Full reference package must have the same AIRAC, package number and revision as the package being installed. Their build timestamps may differ.");
            if (IsCompletePackage(zipPath) && reference.Hash != package.Hash)
                throw new InvalidOperationException("A Full Package installation already supplies its complete reference. Remove the additional, different reference ZIP.");
        }
        var dependencies = new List<GngUpdateDependency>();
        foreach (var plugin in package.ProfilePlugins.Where(p => !package.Entries.ContainsKey(p)))
        {
            var existing = Contained(root, plugin);
            EnsureNoReparse(existing);
            if (!File.Exists(existing)) throw new InvalidOperationException("The Update-Only package needs an installed plugin that is missing: " + plugin + ". Use a Full Package instead.");
            if (new FileInfo(existing).Length > MaxFile) throw new InvalidOperationException("An installed plugin needed by the Update-Only package exceeds the supported size.");
            var snapshot = Snapshot(existing);
            dependencies.Add(new(existing, snapshot.Hash, snapshot.Length));
        }
        var current = ReadInstallation(root, context);
        var installedGenerations = Directory.EnumerateFiles(root, "ESAA-Sweden_*.sct")
            .Select(Path.GetFileNameWithoutExtension).Where(s => s is not null && SectorName.IsMatch(s)).Cast<string>().ToList();
        if (PackageName.Match(Path.GetFileName(zipPath)).Groups[1].Value.Equals("Update-Only", StringComparison.OrdinalIgnoreCase) &&
            (!installedGenerations.Any(g => File.Exists(Contained(root, g + ".ese"))) || !Directory.EnumerateFiles(root, "ES*.prf").Any()))
            throw new InvalidOperationException("Use a Full Package for a new installation. Update-Only ZIPs require an existing GNG sector pair and profile.");
        if (current is not null) installedGenerations.Add(current.Generation);
        foreach (var installed in installedGenerations) RequireNewer(installed, package.Generation);

        var previous = current?.Files.ToDictionary(f => f.RelativePath, Paths);
        var previousPackage = VerifiedCachedPackage(current);
        var knownPlugins = previousPackage?.ProfilePlugins ?? new HashSet<string>(Paths);
        var warnings = new List<string>
        {
            "Package defaults, radar screens and shared plugin/settings files will be refreshed. Review every replacement; these files may contain your customizations.",
            "Existing LastSession sign-in fields, personal Local/Hoppie files, LoginProfiles and custom plugins are kept. Files absent from the package are not deleted.",
            "When the package ships RDF, profiles use its RDF DLL and remove old AFV/RDF entries, including external copies. External files remain untouched; custom RDF display permissions are kept.",
            "Every replaced file is backed up before installation. Close EuroScope; a failed installation will attempt rollback."
        };
        warnings.Add(preserveListLayout
            ? "List positions and whether each list is shown are kept where their settings can be matched safely. Columns, items, sorting and other settings come from the new package."
            : "List layout preservation is off. Installed list settings will use the new package's positions and visibility defaults.");
        if (previousPackage is null)
            warnings.Add("No verified previous package is available. Unrecognized existing plugins are kept; review their compatibility after updating.");
        if (dependencies.Count > 0)
            warnings.Add("This Update-Only package reuses " + dependencies.Count + " installed plugin DLL(s) it does not contain. A Full Package is needed for a complete installation or cleanup comparison.");
        if (reference is not null && !IsCompletePackage(zipPath))
            warnings.Add("The matching Full Package will be cached intact as a reference; only the Update-Only ZIP is installed. Cleanup still verifies that the Full reference's replacement sectors are installed; matching AIRAC/revision alone does not prove that.");
        if (installedGenerations.Any(g => Paths.Equals(g, package.Generation)))
            warnings.Add("This generation is already present. This is a repair/reinstall preview; missing package files will be restored and package defaults refreshed.");
        foreach (var localProfile in Directory.EnumerateFiles(root, "ES*.prf"))
            if (!package.Entries.ContainsKey(Path.GetFileName(localProfile)))
                warnings.Add("Existing profile is not supplied by this package and will not be upgraded: " + Path.GetFileName(localProfile) + ". Select a profile supplied by the new package afterward; review old profiles separately in cleanup.");
        var files = new List<GngUpdateFile>();
        using var input = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Hash(input) != package.Hash) throw new IOException("The downloaded package changed while building its preview.");
        input.Position = 0;
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        foreach (var entry in package.Entries.Values.OrderBy(f => f.Path, Paths))
        {
            var path = Contained(root, entry.Path);
            RequireUnprotected(path, protections);
            EnsureNoReparse(path);
            for (var parent = Path.GetDirectoryName(path); parent is not null && !Paths.Equals(parent, root); parent = Path.GetDirectoryName(parent))
                if (File.Exists(parent)) throw new IOException("A file occupies a folder required by the package: " + Path.GetRelativePath(root, parent));
            if (Directory.Exists(path)) throw new IOException("A folder conflicts with a package file: " + entry.Path);
            if (File.Exists(path) && new FileInfo(path).Length > MaxFile) throw new IOException("An existing package destination exceeds the supported file size: " + entry.Path);
            var before = File.Exists(path) ? Snapshot(path) : ((string Hash, long Length)?)null;
            string action = before is null ? "Add" : "Replace package default";
            string detail = before is null ? "New package file." : "The package version replaces this file; its original bytes will be backed up.";
            if (entry.Promoted) { action = "Promote plugin"; detail = "Promote the DLL from the package's Updated Plugin folder into its active plugin location; the existing DLL is backed up."; }
            byte[]? merged = null;
            var afterHash = entry.Hash;
            var afterLength = entry.Length;
            if (before is not null && IsPersonal(entry.Path))
            {
                action = "Keep personal file";
                detail = "Existing personal override/login file is preserved byte for byte.";
                afterHash = before.Value.Hash;
                afterLength = before.Value.Length;
            }
            else if (Path.GetExtension(entry.Path).Equals(".prf", StringComparison.OrdinalIgnoreCase))
            {
                if (before?.Length > 4 * 1024 * 1024 || entry.Length > 4 * 1024 * 1024) throw new IOException("A profile is too large to merge safely.");
                using var stream = archive.GetEntry(entry.ZipName)!.Open();
                using var reader = new StreamReader(stream, PrfEncoding, detectEncodingFromByteOrderMarks: true);
                merged = MergeProfile(before is null ? "" : File.ReadAllText(path, PrfEncoding), reader.ReadToEnd(), root, knownPlugins);
                afterHash = Hash(merged);
                afterLength = merged.Length;
                action = before is null ? "Add profile" : "Merge profile";
                detail = before is null ? "New package profile. Package sign-in fields are omitted; review your controller profile before use." : "Refresh package sector/settings references and shipped plugins; keep all LastSession fields, personal keys and custom plugin/display entries.";
                if (before is null) warnings.Add("New profile needs controller settings review: " + entry.Path + ". Sign-in details and custom plugins are not copied from other profiles.");
            }
            if (action != "Keep personal file" && before?.Hash == afterHash) { action = "Unchanged"; detail = "Already matches the planned result."; }
            else if (before is not null && previous is not null && previous.TryGetValue(entry.Path, out var baseline) && baseline.AfterHash != before.Value.Hash && action == "Replace package default")
            {
                action = "Replace customized default";
                detail = "Changed since the previous managed install. Installing refreshes this package default; your current bytes will be backed up.";
                warnings.Add("Customized package default will be replaced: " + entry.Path);
            }
            files.Add(new(entry.Path, action, detail) { BeforeHash = before?.Hash, BeforeLength = before?.Length ?? 0, AfterHash = afterHash, AfterLength = afterLength, MergedBytes = merged });
        }
        if (preserveListLayout) ApplyListLayouts(root, package, archive, files, protections, dependencies);
        foreach (var unchanged in files.Where(f => !f.WritesFile))
            warnings.Remove("Customized package default will be replaced: " + unchanged.RelativePath);
        if (package.ProfilePlugins.Any(IsRdfPlugin))
            foreach (var settings in new[] { "TopSkySettingsLocal.txt", @"Plugins\TopSkySettingsLocal.txt", @"ESAA\Plugins\TopSkySettingsLocal.txt" }.Select(p => Contained(root, p)))
            {
                EnsureNoReparse(settings);
                if (!File.Exists(settings)) continue;
                if (new FileInfo(settings).Length > 4 * 1024 * 1024) throw new IOException("A local TopSky settings file is too large to check safely.");
                if (File.ReadLines(settings, PrfEncoding).Any(line => Regex.IsMatch(line, @"^\s*RDF_Mode\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
                    warnings.Add("A preserved local TopSky file overrides RDF_Mode: " + Path.GetRelativePath(root, settings) + ". Review RDF/TopSky compatibility after updating; Launchpad does not change this preference.");
            }
        return new(Path.GetFileName(zipPath), FormatVersion(package.Generation), root, zipPath, files, warnings)
            { PackageHash = package.Hash, Generation = package.Generation, ProtectedPaths = protections, Dependencies = dependencies, PreserveListLayout = preserveListLayout,
                ReferencePackagePath = fullPackagePath, ReferencePackageHash = reference?.Hash, ReferencePackageName = fullPackagePath is null ? null : Path.GetFileName(fullPackagePath) };
    }

    public static GngUpdateResult Install(GngUpdatePlan plan, IProgress<string>? progress = null) => Install(plan, progress, ProductionContext());

    internal static GngUpdateResult Install(GngUpdatePlan plan, IProgress<string>? progress, OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(plan);
        using var maintenance = context.AcquireLock();
        context.RequireClosed();
        var root = ExistingDirectory(plan.DataFolder);
        ValidateRoots(root, plan.ProtectedPaths, context);
        var backupBase = BackupBase(root, context);
        using var rootGuard = new DirectoryGuard(root, false);
        using var parents = new DirectoryGuard(backupBase, true);
        using var updateLock = new FileStream(Path.Combine(backupBase, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        progress?.Report("Rechecking the package and the files shown in the preview…");
        var fresh = BuildPlan(plan.PackagePath, root, plan.ProtectedPaths, context, plan.ReferencePackagePath, plan.PreserveListLayout);
        if (Fingerprint(fresh) != Fingerprint(plan)) throw new InvalidOperationException("The package or local files changed after preview. Review a fresh preview before installing.");
        var writePaths = fresh.Files.Where(f => f.WritesFile).Select(f => CanonicalPath(Contained(root, f.RelativePath))).ToHashSet(Paths);
        // Inputs which are also replacements are checked by their reviewed before hashes during backup/move.
        // Keeping those handles open here would prevent the installer's own verified moves.
        using var dependencyGuard = new DependencyGuard(fresh.Dependencies.Where(d => d.Hash is not null && !writePaths.Contains(d.Path)));
        var previousComplete = VerifiedCompletePackagePath(ReadInstallation(root, context));
        var transaction = Path.Combine(backupBase, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        using var transactionGuard = new DirectoryGuard(transaction, true);
        var writes = fresh.Files.Where(f => f.WritesFile).ToArray();
        var journal = new Journal(1, root, fresh.PackageName, fresh.Generation, fresh.PackageHash, "Prepared",
            writes.Select(f => new SavedFile(f.RelativePath, f.BeforeHash, f.BeforeLength, f.AfterHash, f.AfterLength)).ToList());
        var cache = Contained(transaction, "package\\" + fresh.PackageName);
        var completeCache = IsCompletePackage(fresh.PackagePath) ? cache : fresh.ReferencePackagePath is null ? null :
            Contained(transaction, "complete-package\\" + fresh.ReferencePackageName);
        var completeHash = IsCompletePackage(fresh.PackagePath) ? fresh.PackageHash : fresh.ReferencePackageHash;
        progress?.Report("Staging the package and backing up every replacement…");
        CopyVerified(fresh.PackagePath, cache, new FileInfo(fresh.PackagePath).Length, fresh.PackageHash);
        if (completeCache is not null && !Paths.Equals(completeCache, cache))
            CopyVerified(fresh.ReferencePackagePath!, completeCache, new FileInfo(fresh.ReferencePackagePath!).Length, fresh.ReferencePackageHash!);
        var currentFile = Path.Combine(backupBase, CurrentName);
        if (File.Exists(currentFile))
        {
            var snapshot = Snapshot(currentFile);
            CopyVerified(currentFile, Path.Combine(transaction, "previous-installation.json"), snapshot.Length, snapshot.Hash);
        }
        using (var archive = ZipFile.OpenRead(cache))
        {
            var package = ReadPackage(cache);
            foreach (var file in writes)
            {
                context.RequireClosed();
                var stage = Contained(transaction, "stage\\" + file.RelativePath);
                using var stageParents = new DirectoryGuard(Path.GetDirectoryName(stage)!, true);
                using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (file.MergedBytes is not null) output.Write(file.MergedBytes);
                    else { using var content = archive.GetEntry(package.Entries[file.RelativePath].ZipName)!.Open(); CopyBounded(content, output, file.AfterLength); }
                    output.Flush(true);
                }
                if (Snapshot(stage).Hash != file.AfterHash) throw new IOException("Staged file verification failed: " + file.RelativePath);
                if (file.BeforeHash is not null)
                    CopyVerified(Contained(root, file.RelativePath), Contained(transaction, "originals\\" + file.RelativePath), file.BeforeLength, file.BeforeHash);
            }
        }
        WriteJson(Path.Combine(transaction, ManifestName), journal);
        WriteJson(Path.Combine(backupBase, PendingName), new Pending(transaction));
        try
        {
            foreach (var file in writes)
            {
                context.RequireClosed();
                progress?.Report("Installing " + file.RelativePath);
                var destination = Contained(root, file.RelativePath);
                RequireUnprotected(destination, fresh.ProtectedPaths);
                if (file.BeforeHash is not null)
                    MoveVerified(destination, Contained(transaction, "displaced\\" + file.RelativePath), file.BeforeLength, file.BeforeHash);
                MoveVerified(Contained(transaction, "stage\\" + file.RelativePath), destination, file.AfterLength, file.AfterHash);
            }
            var allFiles = fresh.Files.Select(f => new SavedFile(f.RelativePath, f.BeforeHash, f.BeforeLength, f.AfterHash, f.AfterLength)).ToList();
            WriteJson(currentFile, new Installation(transaction, cache, fresh.PackageHash, fresh.Generation, allFiles, completeCache, completeHash));
            WriteJson(Path.Combine(transaction, ManifestName), journal with { Status = "Complete" });
            DeleteMarker(backupBase);
            return new(transaction, writes.Length, Array.Empty<string>()) { CachedPackagePath = cache, CompletePackagePath = completeCache, PreviousCompletePackagePath = previousComplete };
        }
        catch (Exception ex)
        {
            progress?.Report("Installation failed; restoring the files changed by this update…");
            var recovery = Recover(transaction, journal, progress, fresh.ProtectedPaths, context);
            throw new IOException(recovery.Count == 0
                ? "Installation failed and the original files were restored. " + ex.Message
                : "Installation stopped; recovery needs attention. Backup: " + transaction + ". " + string.Join("; ", recovery), ex);
        }
    }

    public static GngUpdateResult Restore(string backupFolder, string expectedRoot, IEnumerable<string>? protectedPaths = null, IProgress<string>? progress = null) =>
        Restore(backupFolder, expectedRoot, protectedPaths, progress, ProductionContext());

    internal static GngUpdateResult Restore(string backupFolder, string expectedRoot, IEnumerable<string>? protectedPaths, IProgress<string>? progress, OperationContext context)
    {
        using var maintenance = context.AcquireLock();
        context.RequireClosed();
        var root = ExistingDirectory(expectedRoot);
        var protections = ValidateRoots(root, protectedPaths, context);
        var backup = ExistingDirectory(backupFolder);
        var journal = ReadJournal(backup);
        if (journal.Status == "Restored") throw new InvalidOperationException("This update backup has already been restored.");
        if (!Paths.Equals(CanonicalPath(journal.DataFolder), root) || !Paths.Equals(Path.GetDirectoryName(backup), BackupBase(root, context)))
            throw new InvalidOperationException("This backup does not belong to the reviewed EuroScope data folder.");
        foreach (var file in journal.Files) RequireUnprotected(Contained(root, file.RelativePath), protections);
        // Validate previous metadata before changing any installed files.
        var previous = Path.Combine(backup, "previous-installation.json");
        if (File.Exists(previous)) ValidateInstallation(ReadJson<Installation>(previous), root, context);
        using var rootGuard = new DirectoryGuard(root, false);
        using var guard = new DirectoryGuard(BackupBase(root, context), false);
        using var updateLock = new FileStream(Path.Combine(BackupBase(root, context), "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = ReadInstallation(root, context);
        var pending = GetPendingUpdate(root, context);
        if (pending is not null && !Paths.Equals(pending, backup)) throw new InvalidOperationException("Recover the pending update before restoring another backup.");
        if (pending is null && (current is null || !Paths.Equals(current.BackupFolder, backup)))
            throw new InvalidOperationException("Restore the most recent managed update first.");
        WriteJson(Path.Combine(BackupBase(root, context), PendingName), new Pending(backup));
        var skipped = Recover(backup, journal, progress, protections, context);
        return new(backup, journal.Files.Count - skipped.Count, skipped)
            { CachedPackagePath = GetCachedPackage(root, context), CompletePackagePath = VerifiedCompletePackagePath(ReadInstallation(root, context)) };
    }

    public static string? GetPendingUpdate(string dataFolder) => GetPendingUpdate(dataFolder, ProductionContext());

    /// <summary>Blocks use of partially updated data without requiring a configured folder for legacy/manual setups.</summary>
    public static void RequireNoPendingUpdate(string? dataFolder) => RequireNoPendingUpdate(dataFolder, ProductionContext());
    internal static void RequireNoPendingUpdate(string? dataFolder, OperationContext context)
    {
        if (string.IsNullOrWhiteSpace(dataFolder)) return;
        // Read-only checks must recognize an existing installation through a junction/short-path alias.
        // Mutation entry points still reject reparse paths instead of changing files through them.
        var root = PreservationPath.Resolve(dataFolder);
        var pending = GetPendingUpdate(root, context);
        if (pending is not null)
            throw new InvalidOperationException("This EuroScope data folder has an unfinished GNG update. Restore its pending update before launching EuroScope or checking the installed GNG version. Backup: " + pending);
    }

    internal static string? GetPendingUpdate(string dataFolder, OperationContext context)
    {
        if (string.IsNullOrWhiteSpace(dataFolder)) return null;
        var backupBase = BackupBase(CanonicalPath(dataFolder), context);
        var marker = Path.Combine(backupBase, PendingName);
        EnsureNoReparse(marker);
        if (Directory.Exists(marker)) throw new InvalidOperationException("GNG recovery metadata is damaged: a folder occupies the pending-update marker. Keep the recovery folder for review: " + backupBase);
        if (!File.Exists(marker)) return null;
        try
        {
            var pending = CanonicalPath(ReadJson<Pending>(marker).BackupFolder);
            if (!Paths.Equals(Path.GetDirectoryName(pending), backupBase) || !Directory.Exists(pending)) throw new InvalidOperationException();
            return pending;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or JsonException)
        { throw new InvalidOperationException("GNG recovery metadata is damaged. Keep the backup folder and repair recovery before installing another package: " + backupBase, ex); }
    }

    public static string? GetCachedPackage(string dataFolder) => GetCachedPackage(dataFolder, ProductionContext());
    internal static string? GetCachedPackage(string dataFolder, OperationContext context)
    {
        if (string.IsNullOrWhiteSpace(dataFolder)) return null;
        var installation = ReadInstallation(CanonicalPath(dataFolder), context);
        return VerifiedCachedPackage(installation) is not null ? installation!.PackagePath : null;
    }

    public static string GetBackupRoot(string dataFolder) => BackupBase(CanonicalPath(dataFolder), ProductionContext());

    private static Package? VerifiedCachedPackage(Installation? installation)
    {
        if (installation is null) return null;
        try
        {
            EnsureNoReparse(installation.PackagePath);
            if (!File.Exists(installation.PackagePath)) return null;
            var package = ReadPackage(installation.PackagePath);
            return package.Hash == installation.PackageHash && Paths.Equals(package.Generation, installation.Generation) ? package : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
    private static string? VerifiedCompletePackagePath(Installation? installation)
    {
        if (installation is null) return null;
        if (installation.CompletePackagePath is null)
            return IsCompletePackage(installation.PackagePath) && VerifiedCachedPackage(installation) is not null ? installation.PackagePath : null;
        try
        {
            EnsureNoReparse(installation.CompletePackagePath);
            if (!File.Exists(installation.CompletePackagePath)) return null;
            var complete = ReadPackage(installation.CompletePackagePath);
            return IsCompletePackage(installation.CompletePackagePath) && complete.Hash == installation.CompletePackageHash &&
                SamePackageVersion(complete.Generation, installation.Generation) ? installation.CompletePackagePath : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
    private static bool SamePackageVersion(string first, string second)
    {
        var a = SectorName.Match(first); var b = SectorName.Match(second);
        return a.Success && b.Success && a.Groups[2].Value == b.Groups[2].Value && a.Groups[3].Value == b.Groups[3].Value;
    }
    private static bool IsCompletePackage(string path) => PackageName.Match(Path.GetFileName(path)).Groups[1].Value.Equals("Full-Package", StringComparison.OrdinalIgnoreCase);

    private static List<string> Recover(string backup, Journal journal, IProgress<string>? progress, IReadOnlyList<string> protections, OperationContext context)
    {
        var skipped = new List<string>();
        foreach (var file in journal.Files.AsEnumerable().Reverse())
        {
            try
            {
                context.RequireClosed();
                progress?.Report("Restoring " + file.RelativePath);
                var destination = Contained(journal.DataFolder, file.RelativePath);
                RequireUnprotected(destination, protections);
                EnsureNoReparse(destination);
                if (Directory.Exists(destination)) throw new IOException("A folder occupies the original file path.");
                var current = File.Exists(destination) ? Snapshot(destination) : ((string Hash, long Length)?)null;
                if (current?.Hash == file.BeforeHash && current is not null) continue;
                if (current is not null && (current.Value.Hash != file.AfterHash || current.Value.Length != file.AfterLength))
                    throw new IOException("Changed since installation; the current file was retained.");
                if (file.BeforeHash is not null)
                {
                    var original = Contained(backup, "originals\\" + file.RelativePath);
                    var restoreStage = Contained(backup, "restore-stage-" + Guid.NewGuid().ToString("N") + "\\" + file.RelativePath);
                    CopyVerified(original, restoreStage, file.BeforeLength, file.BeforeHash);
                    if (current is not null)
                        MoveVerified(destination, Contained(backup, "removed-" + Guid.NewGuid().ToString("N") + "\\" + file.RelativePath), file.AfterLength, file.AfterHash);
                    MoveVerified(restoreStage, destination, file.BeforeLength, file.BeforeHash);
                }
                else if (current is not null)
                    MoveVerified(destination, Contained(backup, "removed-" + Guid.NewGuid().ToString("N") + "\\" + file.RelativePath), file.AfterLength, file.AfterHash);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            { skipped.Add(file.RelativePath + ": " + ex.Message); }
        }
        var backupBase = BackupBase(journal.DataFolder, context);
        if (skipped.Count == 0)
        {
            var previous = Path.Combine(backup, "previous-installation.json");
            var currentPath = Path.Combine(backupBase, CurrentName);
            if (File.Exists(previous)) WriteJson(currentPath, ValidateInstallation(ReadJson<Installation>(previous), journal.DataFolder, context));
            else { EnsureNoReparse(currentPath); File.Delete(currentPath); }
            WriteJson(Path.Combine(backup, ManifestName), journal with { Status = "Restored" });
            DeleteMarker(backupBase);
        }
        else WriteJson(Path.Combine(backup, ManifestName), journal with { Status = "Recovery required" });
        return skipped;
    }

    private static Package ReadPackage(string path)
    {
        var name = PackageName.Match(Path.GetFileName(path));
        if (!name.Success || !DateTime.TryParseExact(name.Groups[2].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new InvalidOperationException("Choose an original ESAA Full-Package or Update-Only ZIP, retaining its AeroNav filename.");
        var generation = "ESAA-Sweden_" + name.Groups[2].Value + "-" + name.Groups[3].Value + "-" + name.Groups[4].Value;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaxTotal) throw new InvalidOperationException("The compressed package exceeds supported size limits.");
        var packageHash = Hash(input); input.Position = 0;
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        if (zip.Entries.Count is 0 or > 50000) throw new InvalidOperationException("The ZIP has an unsupported number of entries.");
        var raw = new List<(string Path, ZipArchiveEntry Entry)>();
        var names = new HashSet<string>(Paths);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Package links/reparse entries are not supported.");
            var relative = Normalize(entry.FullName.TrimEnd('/', '\\'));
            if (!names.Add(relative)) throw new InvalidOperationException("The ZIP contains duplicate paths.");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            total = checked(total + entry.Length);
            if (entry.Length > MaxFile || total > MaxTotal) throw new InvalidOperationException("The package exceeds supported size limits.");
            raw.Add((relative, entry));
        }
        if (raw.Count == 0) throw new InvalidOperationException("The ZIP contains no files.");
        string? wrapper = null;
        if (raw.All(f => f.Path.Contains('\\')))
        {
            var first = raw[0].Path.Split('\\')[0];
            if (raw.All(f => Paths.Equals(f.Path.Split('\\')[0], first))) wrapper = first;
        }
        var files = new Dictionary<string, PackageEntry>(Paths);
        foreach (var item in raw)
        {
            var relative = wrapper is null ? item.Path : item.Path[(wrapper.Length + 1)..];
            using var stream = item.Entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536]; long length = 0; int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                length += read;
                if (length > item.Entry.Length || length > MaxFile) throw new InvalidOperationException("A ZIP entry exceeds its declared size.");
                hash.AppendData(buffer, 0, read);
            }
            if (length != item.Entry.Length) throw new InvalidOperationException("A ZIP entry is incomplete.");
            files.Add(relative, new(relative, item.Entry.FullName, length, Convert.ToHexString(hash.GetHashAndReset())));
        }
        foreach (var relative in files.Keys)
            for (var parent = Path.GetDirectoryName(relative); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                if (files.ContainsKey(parent)) throw new InvalidOperationException("The ZIP contains conflicting file and directory paths.");
        if (!files.ContainsKey(generation + ".sct") || !files.ContainsKey(generation + ".ese") ||
            !files.Keys.Any(p => !p.Contains('\\') && Path.GetExtension(p).Equals(".prf", StringComparison.OrdinalIgnoreCase)) ||
            (IsCompletePackage(path) && !files.Keys.Any(p => p.StartsWith("ESAA\\Plugins\\", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(p).Equals(".dll", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("The ZIP must contain matching SCT/ESE files, root profiles and ESAA plugin DLLs for its named generation.");
        if (files.Keys.Any(p => !p.Contains('\\') && SectorName.IsMatch(Path.GetFileNameWithoutExtension(p)) && !Paths.Equals(Path.GetFileNameWithoutExtension(p), generation)))
            throw new InvalidOperationException("The ZIP contains a sector generation that differs from its filename.");
        var promoted = new Dictionary<string, PackageEntry>(Paths);
        foreach (var file in files.Values)
        {
            var parts = file.Path.Split('\\');
            if (parts.Length != 4 || !Paths.Equals(parts[0], "ESAA") || !Paths.Equals(parts[1], "Plugins") ||
                !(Paths.Equals(parts[2], "Updated Plugin") || Paths.Equals(parts[2], "Updated Plugins")) ||
                !Path.GetExtension(parts[3]).Equals(".dll", StringComparison.OrdinalIgnoreCase)) continue;
            var destination = "ESAA\\Plugins\\" + parts[3];
            if (!promoted.TryAdd(destination, file with { Path = destination, Promoted = true }))
                throw new InvalidOperationException("The package has ambiguous staged plugin DLLs for the same active destination.");
        }
        foreach (var file in promoted.Values)
        {
            var original = files.Values.Single(f => f.ZipName == file.ZipName).Path;
            files.Remove(original);
            files[file.Path] = file;
        }
        foreach (var relative in files.Keys)
            for (var parent = Path.GetDirectoryName(relative); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                if (files.ContainsKey(parent)) throw new InvalidOperationException("Staged plugin promotion conflicts with a package directory.");
        var profilePlugins = new HashSet<string>(Paths);
        foreach (var profile in files.Values.Where(f => Path.GetExtension(f.Path).Equals(".prf", StringComparison.OrdinalIgnoreCase)))
        {
            if (profile.Length > 4 * 1024 * 1024) throw new InvalidOperationException("A packaged profile is too large to validate.");
            using var content = zip.GetEntry(profile.ZipName)!.Open();
            using var reader = new StreamReader(content, PrfEncoding, detectEncodingFromByteOrderMarks: true);
            var profileLines = Lines(reader.ReadToEnd());
            var fields = profileLines.Select(l => l.Split('\t', 3)).Where(p => p.Length == 3).ToList();
            if (fields.GroupBy(p => p[0] + "\t" + p[1], StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
                throw new InvalidOperationException("A packaged profile contains duplicate field keys: " + profile.Path);
            var sectors = fields.Where(p => p[0].Equals("Settings", StringComparison.OrdinalIgnoreCase) && p[1].Equals("sector", StringComparison.OrdinalIgnoreCase)).ToList();
            if (sectors.Count != 1 || !Paths.Equals(PackageRelativeReference(sectors[0][2]), generation + ".sct"))
                throw new InvalidOperationException("A packaged profile does not select the package's sector generation: " + profile.Path);
            var plugins = ParsePlugins(profileLines);
            ValidateAudioPlugins(plugins);
            foreach (var plugin in plugins)
            {
                var relative = NormalizePackagePluginPath(plugin.Path);
                if (IsCompletePackage(path) && !files.ContainsKey(relative)) throw new InvalidOperationException("A packaged profile loads a DLL missing from the Full Package: " + profile.Path + " → " + relative);
                profilePlugins.Add(relative);
            }
        }
        return new(packageHash, generation, files, profilePlugins);
    }

    private sealed record PluginGroup(string Path, List<(string Suffix, string Value)> Entries);

    private static byte[] MergeProfile(string localText, string incomingText, string root, HashSet<string> knownPlugins)
    {
        var local = Lines(localText); var incoming = Lines(incomingText);
        static string[] Parts(string line) => line.Split('\t', 3);
        static string Key(string[] p) => p[0] + "\t" + p[1];
        static bool Personal(string[] p) => p[0].Equals("LastSession", StringComparison.OrdinalIgnoreCase) || p[0].Equals("TeamSpeakVccs", StringComparison.OrdinalIgnoreCase) ||
            (p[0].Equals("Settings", StringComparison.OrdinalIgnoreCase) && (p[1].Equals("AselKey", StringComparison.OrdinalIgnoreCase) || p[1].Equals("FreqKey", StringComparison.OrdinalIgnoreCase)));
        var localKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in local)
        {
            var p = Parts(line);
            if (p.Length == 3 && !p[0].Equals("Plugins", StringComparison.OrdinalIgnoreCase) && !localKeys.TryAdd(Key(p), line))
                throw new InvalidOperationException("An existing profile contains duplicate field keys; review it before updating.");
        }
        var incomingKeys = incoming.Select(Parts).Where(p => p.Length == 3).Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var output = new List<string>();
        foreach (var line in incoming)
        {
            var p = Parts(line);
            if (p.Length == 3 && p[0].Equals("Plugins", StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Length == 3 && Personal(p))
            {
                if (!p[0].Equals("LastSession", StringComparison.OrdinalIgnoreCase) && localKeys.TryGetValue(Key(p), out var personal)) output.Add(personal);
                else if (p[0].Equals("Settings", StringComparison.OrdinalIgnoreCase)) output.Add(line);
                continue;
            }
            if (p.Length == 3 && p[0].Equals("Settings", StringComparison.OrdinalIgnoreCase) && p[1].Equals("sector", StringComparison.OrdinalIgnoreCase))
            {
                output.Add(p[0] + "\t" + p[1] + "\t" + PackageRelativeReference(p[2]));
                continue;
            }
            output.Add(line);
        }
        foreach (var line in local)
        {
            var p = Parts(line);
            if (p.Length == 3 && !p[0].Equals("Plugins", StringComparison.OrdinalIgnoreCase) && (p[0].Equals("LastSession", StringComparison.OrdinalIgnoreCase) || !incomingKeys.Contains(Key(p)))) output.Add(line);
        }
        var oldPlugins = ParsePlugins(local);
        var newPlugins = ParsePlugins(incoming).Select(p => p with { Path = NormalizePackagePluginPath(p.Path) }).ToList();
        var used = new HashSet<PluginGroup>();
        var merged = new List<PluginGroup>();
        foreach (var group in newPlugins)
        {
            var identity = PluginIdentity(root, group.Path);
            var matches = IsRdfPlugin(group.Path) ? oldPlugins.Where(p => IsRdfPlugin(p.Path)).ToList() :
                oldPlugins.Where(p => Paths.Equals(PluginIdentity(root, p.Path), identity)).ToList();
            if (matches.Count == 0 && !IsRdfPlugin(group.Path))
                matches.AddRange(oldPlugins.Where(p => IsExternal(root, p.Path) && Paths.Equals(PluginFileName(p.Path), PluginFileName(group.Path))));
            if (matches.Count == 0) { merged.Add(group); continue; }
            foreach (var match in matches) used.Add(match);
            var values = group.Entries.Concat(matches.SelectMany(p => p.Entries)).Where(e => e.Suffix.StartsWith("Display", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Value).Distinct(StringComparer.Ordinal).ToList();
            var combined = group.Entries.Where(e => !e.Suffix.StartsWith("Display", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var item in matches.SelectMany(p => p.Entries).Where(e => !e.Suffix.StartsWith("Display", StringComparison.OrdinalIgnoreCase)))
                if (!combined.Any(e => e.Suffix == item.Suffix)) combined.Add(item);
            for (var i = 0; i < values.Count; i++) combined.Add(("Display" + i, values[i]));
            merged.Add(new(!IsRdfPlugin(group.Path) && IsExternal(root, matches[0].Path) ? matches[0].Path : group.Path, combined));
        }
        var replacesAfv = newPlugins.Any(p => IsRdfPlugin(p.Path));
        foreach (var group in oldPlugins)
            if (!used.Contains(group) && !(replacesAfv && Paths.Equals(PluginFileName(group.Path), "AfvEuroScopeBridge.dll")) &&
                (IsExternal(root, group.Path) || !knownPlugins.Contains(Path.GetRelativePath(root, PluginIdentity(root, group.Path))))) merged.Add(group);
        for (var i = 0; i < merged.Count; i++)
        {
            output.Add($"Plugins\tPlugin{i}\t{merged[i].Path}");
            foreach (var item in merged[i].Entries) output.Add($"Plugins\tPlugin{i}{item.Suffix}\t{item.Value}");
        }
        // Unknown non-slot plugin settings are local preferences, never silently discarded.
        var preferences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in local.Concat(incoming))
        {
            var p = Parts(line);
            if (p.Length == 3 && p[0].Equals("Plugins", StringComparison.OrdinalIgnoreCase) && !PluginKey.IsMatch(p[1])) preferences.TryAdd(p[1], line);
        }
        output.AddRange(preferences.Values);
        return PrfEncoding.GetBytes(string.Join("\r\n", output) + "\r\n");
    }

    private static List<PluginGroup> ParsePlugins(IEnumerable<string> lines)
    {
        var rows = lines.Select(l => l.Split('\t', 3)).Where(p => p.Length == 3 && p[0].Equals("Plugins", StringComparison.OrdinalIgnoreCase))
            .Select(p => (Match: PluginKey.Match(p[1]), Value: p[2])).Where(p => p.Match.Success).ToList();
        var groups = new List<PluginGroup>();
        foreach (var row in rows)
            if (!int.TryParse(row.Match.Groups[1].Value, out var slot) || slot > 50000) throw new InvalidOperationException("A profile contains an invalid plugin slot.");
        foreach (var group in rows.GroupBy(r => int.Parse(r.Match.Groups[1].Value)).OrderBy(g => g.Key))
        {
            var main = group.Where(r => r.Match.Groups[2].Value.Length == 0).ToList();
            if (main.Count != 1) throw new InvalidOperationException("A profile has ambiguous or orphaned plugin entries; review it before updating.");
            groups.Add(new(main[0].Value, group.Where(r => r.Match.Groups[2].Value.Length > 0).Select(r => (r.Match.Groups[2].Value, r.Value)).ToList()));
        }
        return groups;
    }

    private static bool IsRdfPlugin(string path) => Paths.Equals(PluginFileName(path), "RDFPlugin.dll") || Paths.Equals(PluginFileName(path), "RDF.dll");
    private static void ValidateAudioPlugins(IReadOnlyList<PluginGroup> plugins)
    {
        var rdfCount = plugins.Count(p => IsRdfPlugin(p.Path));
        if (rdfCount > 1 || (rdfCount > 0 && plugins.Any(p => Paths.Equals(PluginFileName(p.Path), "AfvEuroScopeBridge.dll"))))
            throw new InvalidOperationException("A packaged profile loads conflicting AFV/RDF audio plugins. Choose a corrected package before installing.");
    }
    private static string NormalizePackagePluginPath(string path)
    {
        path = PackageRelativeReference(path);
        path = Regex.Replace(path, @"^(ESAA[\\/]Plugins[\\/])Updated Plugins?[\\/]", "$1", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        path = Normalize(path);
        if (!path.StartsWith(@"ESAA\Plugins\", StringComparison.OrdinalIgnoreCase) || !Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A packaged profile refers to a plugin outside the supported package plugin folder.");
        return path;
    }
    private static string PackageRelativeReference(string path)
    {
        path = path.Trim().Trim('"').Replace('/', '\\');
        // EuroScope package profiles commonly spell paths with one leading separator.
        if (path.StartsWith('\\') && !path.StartsWith("\\\\", StringComparison.Ordinal)) path = path[1..];
        if (path.StartsWith(@".\", StringComparison.Ordinal)) path = path[2..];
        return Normalize(path);
    }

    private static string PluginIdentity(string root, string value)
    {
        value = value.Trim().Trim('"').Replace('/', '\\');
        if (value.StartsWith('\\') && !value.StartsWith("\\\\", StringComparison.Ordinal)) value = value.TrimStart('\\');
        return Path.GetFullPath(Path.IsPathFullyQualified(value) ? value : Path.Combine(root, value));
    }
    private static bool IsExternal(string root, string path) => !PluginIdentity(root, path).StartsWith(root.TrimEnd('\\') + "\\ESAA\\Plugins\\", StringComparison.OrdinalIgnoreCase);
    private static string PluginFileName(string path) => Path.GetFileName(path.Trim().Trim('"').Replace('/', '\\'));
    private static string[] Lines(string text) => text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
    private static bool IsPersonal(string path) => Path.GetFileName(path).Equals("LoginProfiles.txt", StringComparison.OrdinalIgnoreCase) ||
        (new[] { ".txt", ".ini", ".cfg", ".json", ".xml" }.Contains(Path.GetExtension(path), Paths) &&
         (Path.GetFileName(path).Contains("Local", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Contains("Hoppie", StringComparison.OrdinalIgnoreCase)));
    private static string FormatVersion(string generation) { var m = SectorName.Match(generation); return m.Groups[2].Value[..4] + "/" + m.Groups[2].Value[4..] + " rev." + int.Parse(m.Groups[3].Value); }
    private static void RequireNewer(string old, string incoming)
    {
        var before = SectorName.Match(old); var after = SectorName.Match(incoming);
        if (!before.Success || !after.Success) throw new InvalidOperationException("The installed package generation cannot be verified.");
        if (string.CompareOrdinal(after.Groups[1].Value, before.Groups[1].Value) < 0 && after.Groups[2].Value == before.Groups[2].Value && after.Groups[3].Value == before.Groups[3].Value)
            throw new InvalidOperationException("This ZIP has the same AIRAC/revision but an earlier build timestamp than installed files. Full and Update-Only packages may be built separately; choose a package with the same or a later timestamp, or restore the previous update first.");
        if (string.CompareOrdinal(after.Groups[1].Value, before.Groups[1].Value) < 0 || string.CompareOrdinal(after.Groups[2].Value, before.Groups[2].Value) < 0 ||
            (after.Groups[2].Value == before.Groups[2].Value && int.Parse(after.Groups[3].Value) < int.Parse(before.Groups[3].Value)))
            throw new InvalidOperationException("Choose the current or a newer GNG package. Downgrades are not installed.");
    }

    private static string Fingerprint(GngUpdatePlan plan) => Hash(Encoding.UTF8.GetBytes(plan.PackageHash + "\n" + plan.DataFolder + "\n" + plan.ReferencePackagePath + "\n" + plan.ReferencePackageHash + "\n" + plan.PreserveListLayout + "\n" + string.Join("\n", plan.ProtectedPaths) + "\n" +
        string.Join("\n", plan.Dependencies.Select(d => d.Path + "|" + d.Hash + "|" + d.Length)) + "\n" +
        string.Join("\n", plan.Files.Select(f => f.RelativePath + "|" + f.Action + "|" + f.BeforeHash + "|" + f.BeforeLength + "|" + f.AfterHash + "|" + f.AfterLength))));
    private static string BackupBase(string root, OperationContext context)
    {
        var storage = CanonicalPath(context.StorageRoot);
        var id = Hash(Encoding.UTF8.GetBytes(CanonicalPath(root).ToUpperInvariant()));
        return CanonicalPath(Path.Combine(storage, "Installations", id));
    }
    private static Installation? ReadInstallation(string root, OperationContext context)
    {
        var path = Path.Combine(BackupBase(root, context), CurrentName);
        EnsureNoReparse(path);
        if (!File.Exists(path)) return null;
        return ValidateInstallation(ReadJson<Installation>(path), root, context);
    }
    private static Installation ValidateInstallation(Installation value, string root, OperationContext context)
    {
        if (value is null || string.IsNullOrWhiteSpace(value.BackupFolder) || string.IsNullOrWhiteSpace(value.PackagePath) ||
            !ValidHash(value.PackageHash) || !ValidGeneration(value.Generation)) throw new InvalidOperationException("The saved installation metadata is invalid.");
        var backup = CanonicalPath(value.BackupFolder);
        var package = CanonicalPath(value.PackagePath);
        if (!Paths.Equals(Path.GetDirectoryName(backup), BackupBase(root, context)) || !Paths.Equals(Path.GetDirectoryName(package), Path.Combine(backup, "package")) ||
            !MatchesPackageGeneration(Path.GetFileName(package), value.Generation))
            throw new InvalidOperationException("The saved installation metadata has invalid paths.");
        ValidateFiles(value.Files);
        string? completePath = null;
        if (value.CompletePackagePath is not null || value.CompletePackageHash is not null)
        {
            if (string.IsNullOrWhiteSpace(value.CompletePackagePath) || !ValidHash(value.CompletePackageHash!))
                throw new InvalidOperationException("The saved complete-package reference metadata is invalid.");
            completePath = CanonicalPath(value.CompletePackagePath);
            var match = PackageName.Match(Path.GetFileName(completePath));
            var generation = "ESAA-Sweden_" + match.Groups[2].Value + "-" + match.Groups[3].Value + "-" + match.Groups[4].Value;
            if (!IsCompletePackage(completePath) || !ValidGeneration(generation) || !SamePackageVersion(generation, value.Generation) ||
                !(Paths.Equals(completePath, package) || Paths.Equals(Path.GetDirectoryName(completePath), Path.Combine(backup, "complete-package"))))
                throw new InvalidOperationException("The saved complete-package reference has invalid paths or a different AIRAC/revision.");
            if (Paths.Equals(completePath, package) && value.CompletePackageHash != value.PackageHash)
                throw new InvalidOperationException("The saved primary Full Package has conflicting hashes.");
        }
        return value with { BackupFolder = backup, PackagePath = package, CompletePackagePath = completePath };
    }
    private static Journal ReadJournal(string backup)
    {
        var value = ReadJson<Journal>(Path.Combine(backup, ManifestName));
        if (value.Format != 1 || !Path.IsPathFullyQualified(value.DataFolder) || !ValidHash(value.PackageHash) || !ValidGeneration(value.Generation) ||
            !MatchesPackageGeneration(value.PackageName, value.Generation) || value.Status is not ("Prepared" or "Complete" or "Restored" or "Recovery required"))
            throw new InvalidOperationException("Invalid update recovery manifest.");
        ValidateFiles(value.Files);
        return value with { DataFolder = CanonicalPath(value.DataFolder) };
    }
    private static void ValidateFiles(List<SavedFile>? files)
    {
        if (files is null || files.Count > 50000) throw new InvalidOperationException("Invalid update file inventory.");
        var names = new HashSet<string>(Paths);
        long total = 0;
        foreach (var f in files)
        {
            if (f is null || !names.Add(Normalize(f.RelativePath)) || f.BeforeLength is < 0 or > MaxFile || f.AfterLength is < 0 or > MaxFile ||
                (f.BeforeHash is null && f.BeforeLength != 0) || !ValidHash(f.AfterHash) || (f.BeforeHash is not null && !ValidHash(f.BeforeHash)))
                throw new InvalidOperationException("Invalid file entry in update recovery manifest.");
            total += f.AfterLength;
            if (total > MaxTotal) throw new InvalidOperationException("Update file inventory exceeds supported size limits.");
        }
        foreach (var name in names)
            for (var parent = Path.GetDirectoryName(name); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
                if (names.Contains(parent)) throw new InvalidOperationException("Update inventory contains conflicting file and directory paths.");
    }
    private static bool ValidGeneration(string? value) => value is not null && SectorName.IsMatch(value) && DateTime.TryParseExact(SectorName.Match(value).Groups[1].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    private static bool MatchesPackageGeneration(string? name, string generation)
    {
        if (name is null) return false;
        var match = PackageName.Match(name);
        return match.Success && Paths.Equals("ESAA-Sweden_" + match.Groups[2].Value + "-" + match.Groups[3].Value + "-" + match.Groups[4].Value, generation);
    }
    private static bool ValidHash(string value) => value is not null && Regex.IsMatch(value, "\\A[0-9A-F]{64}\\z");
    private static T ReadJson<T>(string path)
    {
        EnsureNoReparse(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 32 * 1024 * 1024) throw new InvalidOperationException("Update metadata is too large.");
        try { return JsonSerializer.Deserialize<T>(stream) ?? throw new InvalidOperationException("Invalid update metadata."); }
        catch (JsonException ex) { throw new InvalidOperationException("Invalid update metadata. Keep the recovery folder for review.", ex); }
    }
    private static void WriteJson<T>(string path, T value)
    {
        using var guard = new DirectoryGuard(Path.GetDirectoryName(path)!, true);
        EnsureNoReparse(path);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true }); stream.Flush(true); }
        File.Move(temp, path, true);
    }
    private static void DeleteMarker(string backupBase) { var path = Path.Combine(backupBase, PendingName); EnsureNoReparse(path); File.Delete(path); }
    private static string ExistingDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Choose the EuroScope data folder first.");
        path = CanonicalPath(path);
        if (Paths.Equals(path, Path.GetPathRoot(path))) throw new InvalidOperationException("A drive root cannot be the EuroScope data folder.");
        EnsureNoReparse(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("The EuroScope data folder does not exist.");
        return path;
    }
    private static string CanonicalPath(string path)
    {
        path = PreservationPath.Normalize(path);
        EnsureNoReparse(path);
        path = PreservationPath.Resolve(path);
        EnsureNoReparse(path);
        return path;
    }
    private static bool Overlaps(string first, string second) => Paths.Equals(first, second) ||
        first.StartsWith(second.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) || second.StartsWith(first.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    private static IReadOnlyList<string> ValidateRoots(string root, IEnumerable<string>? protectedPaths, OperationContext context)
    {
        var storage = CanonicalPath(context.StorageRoot);
        if (Overlaps(root, storage)) throw new InvalidOperationException("The EuroScope data folder and Launchpad GNG cache/recovery folder must be separate, with neither inside the other.");
        if (!Paths.Equals(Path.GetPathRoot(root), Path.GetPathRoot(storage)))
            throw new InvalidOperationException("The EuroScope data folder and Launchpad AppData backups are on different volumes. This version requires them on the same volume; no installed files were changed.");
        var paths = (protectedPaths ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Select(CanonicalPath).Distinct(Paths).Order(Paths).ToArray();
        if (paths.Any(p => Paths.Equals(root, p) || root.StartsWith(p.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A configured application or protected data folder contains the entire EuroScope data folder. Review those configured paths before updating GNG.");
        return paths;
    }
    private static void RequireUnprotected(string path, IReadOnlyList<string> protections)
    {
        path = CanonicalPath(path);
        if (protections.Any(p => Overlaps(path, CanonicalPath(p))))
            throw new InvalidOperationException("A package file overlaps a configured application or protected data path: " + path + ". Review those paths before changing GNG.");
    }
    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("An archive path is empty.");
        path = path.Replace('/', '\\');
        if (Path.IsPathRooted(path)) throw new InvalidOperationException("Absolute archive paths are not allowed.");
        foreach (var part in path.Split('\\'))
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) throw new InvalidOperationException("The ZIP or recovery manifest contains an unsafe path.");
        return path;
    }
    private static string Contained(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, Normalize(relative)));
        if (!path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("A file path escaped its expected folder.");
        return path;
    }
    private static void EnsureNoReparse(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Updates do not follow symbolic links or junctions."); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
    }
    private static void RequireClosed() => EuroScopeProcessGuard.RequireClosed();
    private static (string Hash, long Length) Snapshot(string path)
    {
        EnsureNoReparse(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return (Hash(stream), stream.Length);
    }
    private static string Hash(Stream stream) => Convert.ToHexString(SHA256.HashData(stream));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static Encoding CreateEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
    }
    private static void CopyBounded(Stream source, Stream destination, long expected)
    {
        var buffer = new byte[65536]; long count = 0; int read;
        while ((read = source.Read(buffer)) > 0) { count += read; if (count > expected) throw new IOException("A file grew while being staged."); destination.Write(buffer, 0, read); }
        if (count != expected) throw new IOException("A staged file is incomplete.");
    }
    private static void CopyVerified(string source, string destination, long length, string hash)
    {
        using var sourceParents = new DirectoryGuard(Path.GetDirectoryName(source)!, false);
        using var destinationParents = new DirectoryGuard(Path.GetDirectoryName(destination)!, true);
        using var handle = CreateFile(ExtendedPath(source), 0x80000000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) throw NativeError("Could not open the file for backup");
        CheckHandle(handle, false);
        using var input = new FileStream(handle, FileAccess.Read);
        if (input.Length != length || Hash(input) != hash) throw new IOException("A file changed after preview or its backup was modified.");
        input.Position = 0;
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        CopyBounded(input, output, length); output.Flush(true);
    }
    private static void MoveVerified(string source, string destination, long length, string hash)
    {
        using var sourceParents = new DirectoryGuard(Path.GetDirectoryName(source)!, false);
        using var destinationParents = new DirectoryGuard(Path.GetDirectoryName(destination)!, true);
        using var handle = CreateFile(ExtendedPath(source), 0x80000000 | 0x00010000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) throw NativeError("Could not open the file for replacement");
        CheckHandle(handle, false);
        using var input = new FileStream(handle, FileAccess.Read);
        if (input.Length != length || Hash(input) != hash) throw new IOException("The file changed after preview; it was retained.");
        // Win32's absolute destination form works across supported Windows builds.
        // Parent handles keep both paths stable; RootDirectory remains NULL.
        var name = Encoding.Unicode.GetBytes(ExtendedPath(destination));
        var rootOffset = IntPtr.Size == 8 ? 8 : 4; var lengthOffset = rootOffset + IntPtr.Size; var nameOffset = lengthOffset + 4;
        var size = nameOffset + name.Length + 2; var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            Marshal.WriteInt32(buffer, lengthOffset, name.Length); Marshal.Copy(name, 0, IntPtr.Add(buffer, nameOffset), name.Length);
            if (!SetFileInformationByHandle(handle, 3, buffer, (uint)size)) throw NativeError("Could not move the verified file; existing destinations are never overwritten");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private sealed class DependencyGuard : IDisposable
    {
        private readonly List<IDisposable> held = [];
        public DependencyGuard(IEnumerable<GngUpdateDependency> dependencies)
        {
            try
            {
                foreach (var dependency in dependencies)
                {
                    held.Add(new DirectoryGuard(Path.GetDirectoryName(dependency.Path)!, false));
                    var handle = CreateFile(ExtendedPath(dependency.Path), 0x80000000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
                    if (handle.IsInvalid) { handle.Dispose(); throw NativeError("Could not hold a local file required by this installation review"); }
                    held.Add(handle); CheckHandle(handle, false);
                    var input = new FileStream(handle, FileAccess.Read); held.Add(input);
                    if (input.Length != dependency.Length || Hash(input) != dependency.Hash) throw new IOException("A local file required by this installation review changed after preview.");
                }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { for (var i = held.Count - 1; i >= 0; i--) held[i].Dispose(); held.Clear(); }
    }
    private sealed class DirectoryGuard : IDisposable
    {
        private readonly List<SafeFileHandle> handles = new();
        public SafeFileHandle Handle => handles[^1];
        public DirectoryGuard(string directory, bool create)
        {
            var chain = new Stack<string>();
            for (var path = Path.GetFullPath(directory); !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path)) chain.Push(path);
            try
            {
                foreach (var path in chain)
                {
                    if (create && !Directory.Exists(path)) Directory.CreateDirectory(path);
                    var handle = CreateFile(ExtendedPath(path), 0x80, 3, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
                    if (handle.IsInvalid) { var error = NativeError("Could not lock an update directory"); handle.Dispose(); throw error; }
                    handles.Add(handle); CheckHandle(handle, true);
                }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { for (var i = handles.Count - 1; i >= 0; i--) handles[i].Dispose(); handles.Clear(); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeInfo { public uint Attributes; public uint Tag; }
    private static void CheckHandle(SafeFileHandle handle, bool directory)
    {
        if (!GetFileInformationByHandleEx(handle, 9, out var info, 8)) throw NativeError("Could not verify the file handle");
        if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 || ((info.Attributes & (uint)FileAttributes.Directory) != 0) != directory)
            throw new IOException("A file or folder became a reparse point or changed its type.");
    }
    private static IOException NativeError(string message) => new(message + ": " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
    private static string ExtendedPath(string path)
    {
        path = Path.GetFullPath(path);
        if (path.StartsWith("\\\\?\\", StringComparison.Ordinal)) return path;
        return path.StartsWith("\\\\", StringComparison.Ordinal) ? "\\\\?\\UNC\\" + path[2..] : "\\\\?\\" + path;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, IntPtr info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out AttributeInfo info, uint size);
}
