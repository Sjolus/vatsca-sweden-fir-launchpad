using System.IO;
using System.Text;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

public sealed record AtcRemovalSelection(AtcRemovalTarget Target, bool RemoveApplication, bool RemoveData);
public sealed record LaunchpadDataSelection(bool Settings, bool BrowserSessions, bool Credentials, bool Downloads, bool Backups);
public sealed record AtcMaintenancePlan(
    IReadOnlyList<AtcRemovalSelection> Selections,
    RemovalFilePlan BackupFiles,
    RemovalFilePlan DirectRemovalFiles,
    IReadOnlyDictionary<AtcRemovalApp, RemovalFilePlan> VendorSnapshots,
    LaunchpadDataSelection LaunchpadData,
    string? BackupDestination,
    string Review);

/// <summary>Reviews destructive actions and completes any recovery export selected or required by the plan before applying them. Discovery never starts a process.</summary>
public sealed class AtcMaintenanceService
{
    private readonly AtcRemovalCatalog _catalog;
    private readonly AtcRemovalVendor _vendors;
    private readonly AtcRemovalEnvironment _environment;
    private readonly Action<string> _deleteCredential;
    private readonly AppSettings _settings;
    public string? LastBackupFolder { get; private set; }
    public bool RestartRequired { get; private set; }

    public AtcMaintenanceService(AppSettings settings) : this(settings, new(), CredentialManagerService.Delete) { }
    internal AtcMaintenanceService(AppSettings settings, AtcRemovalEnvironment environment, Action<string> deleteCredential)
    {
        _settings = settings;
        _environment = environment;
        _catalog = new(environment);
        _vendors = new(environment);
        _deleteCredential = deleteCredential;
    }
    public IReadOnlyList<AtcRemovalTarget> Discover() => _catalog.Discover(_settings);

    public AtcMaintenancePlan Preview(IEnumerable<AtcRemovalSelection> selections, LaunchpadDataSelection local,
        string? backupDestination)
    {
        RequireNoRestart();
        var selected = selections.Where(s => s.RemoveApplication || s.RemoveData).ToArray();
        if (selected.Select(s => s.Target.Id).Distinct().Count() != selected.Length)
            throw new InvalidDataException("An application can only be selected once.");
        var backupRoots = new List<string>();
        var deleteRoots = new List<string>();
        var review = new StringBuilder("Review the exact removal plan\n\n");
        RequireBrowsersClosed(local);
        foreach (var selection in selected)
        {
            var target = _catalog.Revalidate(selection.Target, _settings);
            if (selection.RemoveApplication && !target.CanRemoveApplication || selection.RemoveData && !target.CanRemoveData)
                throw new InvalidOperationException(target.Name + ": this action is not supported for the detected copy.");
            if (target.Id == AtcRemovalApp.Gng && selection.RemoveData && !selection.RemoveApplication)
                throw new InvalidOperationException("GNG settings are part of the package. Select Remove application / package as well for a complete package reset.");
            if (_catalog.IsRunning(target)) throw new InvalidOperationException("Close " + target.Name + " before reviewing removal.");
            review.AppendLine(target.Name + ": " + (selection.RemoveApplication ? "remove application" : "keep application") +
                (selection.RemoveData ? "; delete settings/data" : "; keep settings/data"));
            if (selection.RemoveApplication)
            {
                backupRoots.AddRange(target.ProgramRoots);
                backupRoots.AddRange(target.DataRoots);
                if (target.VendorSpec == null) deleteRoots.AddRange(target.ProgramRoots);
                if (target.VendorSpec?.RemovesData == true && !selection.RemoveData)
                {
                    if (string.IsNullOrWhiteSpace(backupDestination))
                        throw new InvalidOperationException(target.Name + " removes data with the application. Choose a recovery export folder to keep a copy of your settings.");
                    review.AppendLine("  The vendor can remove settings files. The recovery export keeps the reviewed originals for manual restoration.");
                }
                // GNG is one package, with personal settings inside it. Export is the preservation route.
                if (target.Id == AtcRemovalApp.Gng)
                {
                    review.AppendLine("  GNG removal includes personal files inside the reviewed package. A recovery export is the way to keep a copy.");
                    if (!selection.RemoveData && string.IsNullOrWhiteSpace(backupDestination))
                        throw new InvalidOperationException("GNG files and personal settings share folders. Choose a recovery export folder, or explicitly select deleting package settings too.");
                }
            }
            if (selection.RemoveData)
            {
                backupRoots.AddRange(target.DataRoots);
                deleteRoots.AddRange(target.DataRoots);
            }
            foreach (var warning in target.Warnings) review.AppendLine("  " + warning);
        }
        var localRoots = GetLaunchpadDataRoots(local);
        backupRoots.AddRange(localRoots);
        deleteRoots.AddRange(localRoots);
        ValidateTargetOverlap(selected, localRoots);
        if (local.Settings) review.AppendLine("Launchpad: delete preferences, identity fields, log and process-tracking files.");
        if (local.BrowserSessions) review.AppendLine("Launchpad: delete its dedicated VATIRIS/VatEFS and AeroNav sign-in browser sessions.");
        if (local.Downloads) review.AppendLine("Launchpad: delete temporary client/GNG downloads and verification staging. Managed GNG package copies stay with their recovery backups.");
        if (local.Backups) review.AppendLine("Launchpad: delete existing software-update, GNG installation and cleanup recovery backups, including saved GNG package history. Review every listed path below.");
        if (local.Credentials) review.AppendLine("Credential Manager: delete VatscaLaunchpad/VATSIM and VatscaLaunchpad/Hoppie. These secrets are not exported; copies in external profiles remain unless their files are selected.");

        var backup = RemovalFileService.Preview(MinimizeRoots(backupRoots));
        var direct = RemovalFileService.Preview(MinimizeRoots(deleteRoots));
        var vendorSnapshots = selected.Where(s => s.RemoveApplication && s.Target.VendorSpec != null)
            .ToDictionary(s => s.Target.Id, s => RemovalFileService.Preview(MinimizeRoots(s.Target.ProgramRoots.Concat(s.Target.DataRoots))));
        var destination = string.IsNullOrWhiteSpace(backupDestination) ? null : Path.GetFullPath(backupDestination);
        if (destination != null && backup.Roots.Any(root => IsWithin(root, destination) || IsWithin(destination, root)))
            throw new InvalidOperationException("Choose a recovery folder outside every selected application/data folder.");
        review.AppendLine();
        review.AppendLine(destination == null ? "NO RECOVERY EXPORT. Selected data will be permanently removed." :
            "Recovery export: " + destination + " (kept private; may contain passwords and profiles).");
        review.AppendLine($"Snapshot: {backup.Files.Count:N0} files, {backup.TotalBytes / 1048576d:N1} MB.");
        review.AppendLine("Launchpad does not separately remove Edge/WebView2, .NET/VC++ runtimes, system fonts or online accounts. Vendor uninstallers may remove their own registered components, including EuroScope's font.");
        review.AppendLine("Unknown/portable installations need manual removal. Reinstalling clients is a separate operation.");
        review.AppendLine("\nAffected folders/files:");
        foreach (var root in backup.Roots) review.AppendLine(root);
        review.AppendLine("\nComplete file inventory:");
        foreach (var file in backup.Files) review.AppendLine($"{file.FullPath} ({file.Length:N0} bytes)");
        return new(selected, backup, direct, vendorSnapshots, local, destination, review.ToString());
    }

    public async Task<string?> ApplyAsync(AtcMaintenancePlan plan, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        RequireNoRestart();
        LastBackupFolder = null;
        ValidateConfiguredPaths(plan.Selections, plan.DirectRemovalFiles.Roots);
        // Callers hold MaintenanceLock throughout review/apply. Identity and bytes are checked again here.
        foreach (var selection in plan.Selections)
        {
            _catalog.Revalidate(selection.Target, _settings);
            if (_catalog.IsRunning(selection.Target)) throw new InvalidOperationException("Close " + selection.Target.Name + " before removal.");
        }
        RequireBrowsersClosed(plan.LaunchpadData);
        RemovalFileService.Verify(plan.BackupFiles);
        RemovalFileService.Verify(plan.DirectRemovalFiles);
        string? backup = null;
        if (plan.BackupDestination != null)
            backup = await RemovalFileService.BackupAsync(plan.BackupFiles, plan.BackupDestination, progress, cancellationToken).ConfigureAwait(false);
        LastBackupFolder = backup;
        cancellationToken.ThrowIfCancellationRequested();
        RemovalFileService.Verify(plan.BackupFiles);
        if (backup != null) RemovalFileService.VerifyBackup(plan.BackupFiles, backup);
        RequireBrowsersClosed(plan.LaunchpadData);
        foreach (var selection in plan.Selections)
        {
            _catalog.Revalidate(selection.Target, _settings);
            if (_catalog.IsRunning(selection.Target)) throw new InvalidOperationException("Close " + selection.Target.Name + " before removal.");
        }
        // No cancellation after this point: a vendor uninstaller must be allowed to complete.
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report("Removing selected applications. Wait for completion; do not close Launchpad.");
        foreach (var selection in plan.Selections.Where(s => s.RemoveApplication && s.Target.VendorSpec != null))
        {
            ValidateConfiguredPaths(plan.Selections, plan.DirectRemovalFiles.Roots);
            // Verify this vendor's original snapshot, not roots already changed by a completed
            // uninstaller (.dead markers/logs are legitimate vendor output, not direct deletion targets).
            RemovalFileService.Verify(plan.VendorSnapshots[selection.Target.Id]);
            if (backup != null) RemovalFileService.VerifyBackup(plan.BackupFiles, backup);
            _catalog.Revalidate(selection.Target, _settings);
            if (_catalog.IsRunning(selection.Target)) throw new InvalidOperationException("Close " + selection.Target.Name + " before removal.");
            try { await _vendors.UninstallAsync(selection.Target, progress).ConfigureAwait(false); }
            finally { RestartRequired |= _vendors.RestartRequired; }
            // Do not run another vendor, delete data/credentials, or hand off Launchpad removal
            // while Windows still has pending changes from the completed native operation.
            RequireNoRestart();
        }
        RequireBrowsersClosed(plan.LaunchpadData);
        ValidateConfiguredPaths(plan.Selections, plan.DirectRemovalFiles.Roots);
        if (backup != null) RemovalFileService.VerifyBackup(plan.BackupFiles, backup);
        foreach (var selection in plan.Selections.Where(s => s.Target.VendorSpec == null || !s.RemoveApplication))
            if (_catalog.IsRunning(selection.Target)) throw new InvalidOperationException("Close " + selection.Target.Name + " before removing its data.");
        var deleted = await RemovalFileService.DeleteRemainingAsync(plan.DirectRemovalFiles, progress).ConfigureAwait(false);
        if (!deleted.Succeeded)
            throw new IOException("Removal stopped. " + string.Join("; ", deleted.Errors.Select(e => e.Path + ": " + e.Message)));
        if (plan.LaunchpadData.Credentials)
        {
            _deleteCredential(CredentialManagerService.TargetVatsim);
            _deleteCredential(CredentialManagerService.TargetHoppie);
        }
        progress?.Report("Selected removal completed. " + (backup == null ? "No recovery export was requested." : "Recovery export: " + backup));
        return backup;
    }

    private void RequireNoRestart()
    {
        if (RestartRequired)
            throw new InvalidOperationException("Windows requires a restart. Remaining removal and data cleanup were not performed. Restart Windows before reviewing further changes; keep the recovery export.");
    }

    private void RequireBrowsersClosed(LaunchpadDataSelection selected)
    {
        if (selected.BrowserSessions && _environment.IsProcessRunning("msedge"))
            throw new InvalidOperationException("Close Microsoft Edge, including background processes, before deleting browser sessions. Launchpad will not close it for you.");
        if (selected.BrowserSessions && Directory.Exists(Path.Combine(_environment.LocalAppData, "VatscaUpdateChecker", "Gng", "Browser")) &&
            _environment.IsProcessRunning("msedgewebview2"))
            throw new InvalidOperationException("Close GNG sign-in windows and applications using WebView2 before deleting browser sessions. Launchpad will not close them for you.");
    }

    private void ValidateTargetOverlap(IReadOnlyList<AtcRemovalSelection> selected, IReadOnlyList<string> localRoots)
    {
        ValidateConfiguredPaths(selected, localRoots);
        var all = _catalog.Discover(_settings);
        foreach (var target in all)
        {
            var removesProgram = selected.Any(s => s.Target.Id == target.Id && s.RemoveApplication);
            // Launchpad caches/browser folders are not vendor installation roots. Never let
            // a local-data reset bypass a vendor uninstaller or an unselected application.
            if ((!removesProgram || target.VendorSpec != null) &&
                localRoots.Any(root => target.ProgramRoots.Any(path => IsWithin(root, path) || IsWithin(path, root))))
                throw new InvalidOperationException("Launchpad data shares a folder with " + target.Name +
                    ". Resolve this shared installation layout before resetting that data.");
        }
        foreach (var selection in selected)
        {
            var removes = (selection.RemoveApplication ? selection.Target.ProgramRoots : Array.Empty<string>())
                .Concat(selection.RemoveData ? selection.Target.DataRoots : Array.Empty<string>()).ToArray();
            foreach (var other in all.Where(t => t.Id != selection.Target.Id))
            {
                var otherSelection = selected.SingleOrDefault(s => s.Target.Id == other.Id);
                var protectedPrograms = otherSelection?.RemoveApplication == true ? Array.Empty<string>() : other.ProgramRoots;
                // Whole GNG removal keeps settings only in the reviewed export, not at their old paths.
                var removesOtherData = otherSelection?.RemoveData == true || other.Id == AtcRemovalApp.Gng && otherSelection?.RemoveApplication == true;
                var protectedData = removesOtherData ? Array.Empty<string>() : other.DataRoots.Where(path => !localRoots.Any(root => IsWithin(root, path)));
                if (removes.Any(root => protectedPrograms.Concat(protectedData).Any(path => IsWithin(root, path))))
                    throw new InvalidOperationException(selection.Target.Name + " shares a removal folder with unselected " + other.Name + " files. Select the affected item explicitly, or resolve the shared layout manually.");
                if (selection.RemoveApplication && selection.Target.VendorSpec != null && otherSelection?.RemoveApplication == true && other.VendorSpec != null &&
                    selection.Target.ProgramRoots.Any(root => other.ProgramRoots.Any(path => IsWithin(root, path) || IsWithin(path, root))))
                    throw new InvalidOperationException("Nested vendor installations cannot be removed together safely. Remove the inner application first, then review a new plan.");
            }
        }
    }

    private void ValidateConfiguredPaths(IReadOnlyList<AtcRemovalSelection> selected, IReadOnlyList<string> directRoots)
    {
        // Recognition grants removal permission, not preservation. A portable, incomplete or
        // currently missing configured copy must not disappear from the overlap safety boundary.
        var removes = directRoots.Concat(selected.SelectMany(selection =>
            (selection.RemoveApplication ? selection.Target.ProgramRoots : Array.Empty<string>())
            .Concat(selection.RemoveData || selection.RemoveApplication && selection.Target.VendorSpec?.RemovesData == true
                ? selection.Target.DataRoots : Array.Empty<string>()))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (removes.Length == 0) return;
        var resolvedRoots = removes.Select(PreservationPath.Resolve).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var configured = new (AtcRemovalApp App, string Name, string Path)[]
        {
            (AtcRemovalApp.EuroScope, "EuroScope executable", _settings.EuroscopeExePath),
            (AtcRemovalApp.Gng, "EuroScope/GNG data folder", _settings.EuroscopeDataPath),
            (AtcRemovalApp.Vacs, "VACS executable", _settings.VacsExePath),
            (AtcRemovalApp.Vatis, "vATIS executable", _settings.VatisExePath),
            (AtcRemovalApp.TrackAudio, "TrackAudio executable", _settings.TrackAudioExePath),
            (AtcRemovalApp.VatEfs, "VatEFS plugin folder", _settings.VatEfsPath)
        };
        foreach (var item in configured)
        {
            if (string.IsNullOrWhiteSpace(item.Path) || selected.Any(selection => selection.Target.Id == item.App && selection.RemoveApplication)) continue;
            string path, resolvedPath;
            try { path = PreservationPath.Normalize(item.Path); resolvedPath = PreservationPath.Resolve(path); }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException)
            { throw new InvalidOperationException("The configured " + item.Name + " path is invalid. Correct or clear it before removing application/data folders.", ex); }
            if (removes.Any(root => IsWithin(root, path)) || resolvedRoots.Any(root => IsWithin(root, resolvedPath)))
                throw new InvalidOperationException("A removal folder contains the unselected configured " + item.Name +
                    ". This path is protected even when the installation is missing, portable or unsupported. Relocate it or resolve the configuration before removal.");
        }
    }

    private List<string> GetLaunchpadDataRoots(LaunchpadDataSelection selected)
    {
        var roaming = Path.Combine(_environment.RoamingAppData, "VatscaUpdateChecker");
        var software = Path.Combine(_environment.LocalAppData, "VatscaUpdateChecker", "SoftwareUpdates");
        var gng = Path.Combine(_environment.LocalAppData, "VatscaUpdateChecker", "Gng");
        var roots = new List<string>();
        if (selected.Settings)
        {
            roots.AddRange(new[] { "settings.json", "launchpad.log", "VATIRIS.pid", "VatEFS.pid" }.Select(name => Path.Combine(roaming, name)));
            roots.Add(Path.Combine(software, "VatEfsLogs"));
        }
        if (selected.BrowserSessions)
        {
            roots.AddRange(new[] { "VATIRISProfile", "VatEFSProfile" }.Select(name => Path.Combine(roaming, name)));
            roots.Add(Path.Combine(gng, "Browser"));
        }
        if (selected.Downloads && Directory.Exists(software))
        {
            SoftwareInstaller.RejectReparse(software);
            roots.Add(Path.Combine(software, "Verification"));
            roots.AddRange(Directory.EnumerateDirectories(software).Where(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _)));
        }
        if (selected.Downloads)
        {
            roots.Add(Path.Combine(gng, "Downloads"));
            foreach (var cache in new[]
            {
                Path.Combine(_environment.LocalAppData, "VatscaUpdateChecker", "EuroScopeInstall"),
                Path.Combine(_environment.LocalAppData, "VatscaUpdateChecker", "EuroScope", "Prerequisites"),
                Path.Combine(_environment.LocalAppData, "VatscaUpdateChecker", "FreshInstalls"),
                Path.Combine(software, "Prerequisites"),
                Path.Combine(software, "FreshInstall", "vATIS")
            })
            {
                SoftwareInstaller.RejectReparse(cache);
                if (Directory.Exists(cache))
                    roots.AddRange(Directory.EnumerateDirectories(cache).Where(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _)));
            }
        }
        if (selected.Backups)
        {
            roots.Add(Path.Combine(software, "Backups"));
            roots.Add(Path.Combine(software, "VatEfsRecovery"));
            roots.Add(Path.Combine(gng, "Installations"));
            roots.Add(Path.Combine(_environment.LocalAppData, "VatscaUpdateChecker", "CleanupBackups"));
        }
        return roots.Where(path => File.Exists(path) || Directory.Exists(path)).ToList();
    }

    internal static IReadOnlyList<string> MinimizeRoots(IEnumerable<string> paths)
    {
        var distinct = paths.Where(path => File.Exists(path) || Directory.Exists(path)).Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path.Length).ToArray();
        var result = new List<string>();
        foreach (var path in distinct) if (!result.Any(parent => IsWithin(parent, path))) result.Add(path);
        return result;
    }

    private static bool IsWithin(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
