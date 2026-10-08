using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

internal sealed record FreshSoftwareCommand(string Executable, IReadOnlyList<string> Arguments,
    bool Elevate, string InstallDirectory);

internal sealed class FreshSoftwareInstallEnvironment
{
    public string LocalAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public string RoamingAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public string ProgramFiles { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    public string ProgramFilesX86 { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    public SoftwareInstallerEnvironment Software { get; init; } = new();
    public Func<SoftwareApp, string?> PrerequisiteProblem { get; init; } = SoftwarePrerequisiteService.GetProblem;
    public Func<SoftwareApp, bool> HasInstallerResidue { get; init; } = FreshSoftwareInstallNative.HasInstallerResidue;
    public Func<FreshSoftwareCommand, Task<int>> RunAsync { get; init; } = FreshSoftwareInstallNative.RunAsync;
    public Action<string>? ValidateVatisDestination { get; init; }
    public Action<SoftwareRelease>? ValidateVatisRelease { get; init; }
    public Func<SoftwareRelease, string, string, IProgress<string>?, CancellationToken, Func<Task>, Task<SoftwareInstallResult>>? InstallVatisAsync { get; init; }
    public Action<string>? ValidateVatEfsDestination { get; init; }
    public Func<SoftwareRelease, string, string, IProgress<string>?, CancellationToken, Func<Task>, Task<SoftwareInstallResult>>? InstallVatEfsAsync { get; init; }
}

/// <summary>Explicit reviewed fresh installs. Existing applications are adopted or updated separately.</summary>
public sealed class FreshSoftwareInstallService
{
    private readonly HttpClient _http;
    private readonly FreshSoftwareInstallEnvironment _environment;
    private readonly ISoftwareInstaller _installer;
    private readonly Func<SoftwareApp, bool, CancellationToken, Task<SoftwareRelease?>> _releaseSource;
    private readonly Action<string> _validateVatis;
    private readonly Action<SoftwareRelease> _validateVatisRelease;
    private readonly Func<SoftwareRelease, string, string, IProgress<string>?, CancellationToken, Func<Task>, Task<SoftwareInstallResult>> _installVatis;
    private readonly Action<string> _validateVatEfs;
    private readonly Func<SoftwareRelease, string, string, IProgress<string>?, CancellationToken, Func<Task>, Task<SoftwareInstallResult>> _installVatEfs;
    private readonly string _cacheRoot;
    private readonly TimeSpan _downloadTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _canCancel;

    /// <param name="http">Caller-owned HTTP client; automatic redirects must be disabled.</param>
    public FreshSoftwareInstallService(HttpClient http, string? cacheRoot = null)
        : this(http, new(), new SoftwareReleaseSource(http).GetLatestFreshAsync, null, cacheRoot) { }

    internal FreshSoftwareInstallService(HttpClient http, FreshSoftwareInstallEnvironment environment,
        Func<SoftwareApp, bool, CancellationToken, Task<SoftwareRelease?>> source, ISoftwareInstaller? installer = null,
        string? cacheRoot = null, TimeSpan? downloadTimeout = null)
    {
        _http = http; _environment = environment; _releaseSource = source;
        _installer = installer ?? new SoftwareInstaller(environment.Software);
        _cacheRoot = SoftwareInstaller.FullPath(cacheRoot ?? Path.Combine(environment.LocalAppData, "VatscaUpdateChecker", "FreshInstalls"));
        _downloadTimeout = downloadTimeout ?? TimeSpan.FromMinutes(20);
        if (_downloadTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(downloadTimeout));
        var vatis = new VatisFreshInstaller(http);
        _validateVatis = environment.ValidateVatisDestination ?? vatis.ValidateDestination;
        _validateVatisRelease = environment.ValidateVatisRelease ?? vatis.ValidateRelease;
        _installVatis = environment.InstallVatisAsync ?? (async (release, package, root, progress, token, beforeInstall) =>
        {
            try { return await vatis.InstallAsync(release, package, root, progress, token, beforeInstall).ConfigureAwait(false); }
            finally { RestartRequired |= vatis.RestartRequired; }
        });
        var vatEfs = new VatEfsInstaller(environment.Software.VatEfs ?? new());
        _validateVatEfs = environment.ValidateVatEfsDestination ?? vatEfs.ValidateFreshDestination;
        _installVatEfs = environment.InstallVatEfsAsync ?? (async (release, package, root, progress, token, beforeInstall) =>
        {
            try { return await vatEfs.InstallFreshAsync(release, package, root, progress, token, beforeInstall).ConfigureAwait(false); }
            finally { RestartRequired |= vatEfs.RestartRequired; }
        });
    }

    public bool IsBusy => _gate.CurrentCount == 0;
    public bool CanCancel => IsBusy && _canCancel;
    public string? LastBackupFolder { get; private set; }
    public bool RestartRequired { get; private set; }

    public async Task<FreshSoftwareInstallPlan> PreviewAsync(SoftwareApp app, AppSettings settings,
        string? backupDestination, bool allowVatisBeta = false, CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Copy only the relevant configured path; no live settings reference or credentials in the plan.
            var configured = app switch { SoftwareApp.Vacs => settings.VacsExePath, SoftwareApp.Vatis => settings.VatisExePath, SoftwareApp.TrackAudio => settings.TrackAudioExePath,
                SoftwareApp.VatEfs => string.IsNullOrWhiteSpace(settings.VatEfsPath) ? string.Empty : Path.Combine(settings.VatEfsPath, "efs.exe"),
                _ => throw new ArgumentOutOfRangeException(nameof(app)) };
            allowVatisBeta &= app == SoftwareApp.Vatis;
            var layout = Layout(app);
            await Task.Run(() => ValidateAbsent(app, configured, layout.Candidates, layout.Root), cancellationToken).ConfigureAwait(false);
            var release = await _releaseSource(app, allowVatisBeta, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(app == SoftwareApp.Vatis && !allowVatisBeta
                    ? "No compatible stable vATIS package is currently available. You can explicitly select the official beta channel or use the vendor's installer manually."
                    : "The vendor has no compatible verified Windows package for a fresh installation.");
            ValidateRelease(app, release, allowVatisBeta);
            if (app == SoftwareApp.Vatis) _validateVatisRelease(release);
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateAbsent(app, configured, layout.Candidates, layout.Root);
                var data = DataRoots(app);
                foreach (var path in data) SoftwareInstaller.RejectReparse(path);
                var backup = RemovalFileService.Preview(data.Where(Exists));
                var destination = string.IsNullOrWhiteSpace(backupDestination) ? null : SoftwareInstaller.FullPath(backupDestination);
                if (destination == null && backup.Roots.Count != 0)
                    throw new InvalidOperationException("Existing settings were found. Choose a private external recovery folder before installing; the settings will be kept in place.");
                if (destination != null)
                {
                    SoftwareInstaller.RejectReparse(destination);
                    if (!Directory.Exists(destination)) throw new DirectoryNotFoundException("Choose an existing private recovery folder.");
                    if (data.Append(layout.Root).Any(path => Overlaps(path, destination)))
                        throw new InvalidOperationException("The recovery folder must be outside the installation and settings folders.");
                }
                if (data.Append(layout.Root).Any(path => Overlaps(path, _cacheRoot)) || destination != null && Overlaps(destination, _cacheRoot))
                    throw new InvalidOperationException("The download cache overlaps the reviewed installation, settings or recovery location.");
                var text = new StringBuilder();
                text.AppendLine($"Install {Name(app)} {release.Version}" + (release.IsPrerelease || release.Version.Contains('-')
                    ? app == SoftwareApp.VatEfs ? " — PRERELEASE (review before installing)." : " — BETA RELEASE (explicitly selected)." : "."));
                text.AppendLine("Installation folder: " + layout.Root);
                text.AppendLine("Scope: " + layout.Scope + (layout.Scope == "AllUsers" ? " (Windows elevation required)" : ""));
                text.AppendLine("Official package: " + release.DownloadUri);
                text.AppendLine("Package size and SHA-256 must match the vendor's release metadata. " + (app == SoftwareApp.Vatis
                    ? "The signed vATIS package and separately verified Setup must agree exactly. Setup will not run over any existing vATIS folder."
                    : app == SoftwareApp.VatEfs ? "The MSI product, version, upgrade identity and installation layout are checked. The package is not represented as having a trusted publisher signature."
                    : "The installer product/version is checked. VACS/TrackAudio packages are not represented as having a trusted publisher signature."));
                if (app == SoftwareApp.VatEfs)
                    text.AppendLine("Close every EuroScope instance and VatEFS backend before installing. The MSI installs the plugin and flight-strip application together. Your EuroScope profiles and external settings are not changed. After installation, use Controller profile to review enabling the plugin.");
                text.AppendLine(destination == null ? "No existing known settings need an export." : "Private settings recovery export: " + destination);
                text.AppendLine("Existing known settings remain in place. External/custom settings are not discovered. Launchpad does not delete a previous installation or start the client as part of installation.");
                text.AppendLine("Required shared runtimes must be installed through their separate explicit action. Preparation can be cancelled; once the vendor installer starts, wait for completion. No Windows restart is requested.");
                text.AppendLine($"\nSettings snapshot: {backup.Files.Count:N0} files, {backup.TotalBytes / 1048576d:N1} MB.");
                foreach (var file in backup.Files) text.AppendLine(file.FullPath + $" ({file.Length:N0} bytes)");
                foreach (var directory in backup.Directories) text.AppendLine(directory.FullPath + " [folder]");
                return new FreshSoftwareInstallPlan(release, layout.Exe, layout.Root, layout.Scope, backup, destination,
                    text.ToString(), configured, layout.Candidates, data.Where(path => !Exists(path)), allowVatisBeta);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _canCancel = false; _gate.Release(); }
    }

    public async Task<FreshSoftwareInstallResult> ApplyAsync(FreshSoftwareInstallPlan plan,
        IProgress<FreshSoftwareInstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false); LastBackupFolder = null;
        try { return await Task.Run(() => ApplyCoreAsync(plan, progress, cancellationToken), cancellationToken).ConfigureAwait(false); }
        finally { _canCancel = false; _gate.Release(); }
    }

    private async Task<FreshSoftwareInstallResult> ApplyCoreAsync(FreshSoftwareInstallPlan plan,
        IProgress<FreshSoftwareInstallProgress>? progress, CancellationToken token)
    {
        string? work = null, packagePath = null;
        try
        {
            Report(FreshSoftwareInstallPhase.Checking, "Rechecking the reviewed installation and official release…");
            ValidateOriginal(plan); await ValidateReleaseUnchangedAsync(plan, token).ConfigureAwait(false);
            SoftwareInstaller.RejectReparse(_cacheRoot); Directory.CreateDirectory(_cacheRoot);
            work = Path.Combine(_cacheRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
            SoftwareInstaller.RejectReparse(work); packagePath = Path.Combine(work, plan.Release.FileName);
            await DownloadAsync(plan.Release, packagePath, progress, token).ConfigureAwait(false);
            SoftwareInstaller.RejectReparse(packagePath);
            using var packageLock = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Report(FreshSoftwareInstallPhase.Verifying, "Verifying official package bytes and application identity…");
            await VerifyBytesAsync(plan.Release, packageLock, token).ConfigureAwait(false);
            await _installer.VerifyPackageAsync(plan.Release, packagePath, token).ConfigureAwait(false);
            ValidateOriginal(plan); token.ThrowIfCancellationRequested();
            if (plan.BackupDestination != null)
            {
                Report(FreshSoftwareInstallPhase.BackingUp, "Exporting existing settings before installation…");
                LastBackupFolder = await RemovalFileService.BackupAsync(plan.BackupFiles, plan.BackupDestination,
                    new InlineProgress(message => Report(FreshSoftwareInstallPhase.BackingUp, message)), token).ConfigureAwait(false);
            }
            if (plan.App is SoftwareApp.Vatis or SoftwareApp.VatEfs)
            {
                var installAdapter = plan.App == SoftwareApp.Vatis ? _installVatis : _installVatEfs;
                await installAdapter(plan.Release, packagePath, plan.InstallRoot,
                    new InlineProgress(message => Report(_canCancel ? FreshSoftwareInstallPhase.Verifying : FreshSoftwareInstallPhase.Installing, message)),
                    token, BeforeInstall).ConfigureAwait(false);
                if (_canCancel) throw new InvalidOperationException("The installer adapter did not enter its reviewed installation boundary.");
            }
            else
            {
                await BeforeInstall().ConfigureAwait(false);
                var code = await _environment.RunAsync(BuildCommand(plan, packagePath)).ConfigureAwait(false);
                RestartRequired |= code == 3010;
                if (code == 1223) throw new IOException("Windows elevation was cancelled. Installation did not complete; retained settings and their recovery export remain available.");
                if (code is not (0 or 3010)) throw new IOException("The vendor installer returned exit code " + code + ". Keep the recovery export and review the incomplete installation manually.");
            }
            var installed = _installer.Inspect(plan.App, plan.ExecutablePath);
            if (!installed.CanUpdate || installed.Version != plan.Release.Version || installed.Scope != plan.Scope ||
                !Same(installed.RootPath, plan.InstallRoot) || !Same(installed.ExePath, plan.ExecutablePath))
                throw new IOException("The installed version, scope or destination could not be confirmed. Keep the recovery export and review the application manually.");
            if (_environment.Software.IsRunning(plan.App) || _installer.IsRunning(installed))
                throw new IOException("The client is running unexpectedly. Launchpad has left it untouched; review it manually.");
            Report(FreshSoftwareInstallPhase.Completed, Name(plan.App) + " installed and remains closed." + (RestartRequired ? " Windows reports that a restart is needed; restart manually when ready." : ""));
            return new(plan.ExecutablePath, LastBackupFolder, RestartRequired);

            async Task BeforeInstall()
            {
                ValidateOriginal(plan); VerifyRecovery(plan);
                await ValidateReleaseUnchangedAsync(plan, token).ConfigureAwait(false);
                await VerifyBytesAsync(plan.Release, packageLock, token).ConfigureAwait(false);
                SoftwareInstaller.RejectReparse(packagePath);
                ValidateOriginal(plan); VerifyRecovery(plan); token.ThrowIfCancellationRequested();
                _canCancel = false;
                Report(FreshSoftwareInstallPhase.Installing, "Installing " + Name(plan.App) + ". Keep Launchpad open until completion.");
                // A synchronous progress consumer cannot bypass the last file/process checks.
                ValidateOriginal(plan); VerifyRecovery(plan);
            }
        }
        finally
        {
            if (packagePath != null) { try { SoftwareInstaller.RejectReparse(packagePath); if (File.Exists(packagePath)) File.Delete(packagePath); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            if (work != null) { try { SoftwareInstaller.RejectReparse(work); if (Directory.Exists(work)) Directory.Delete(work, false); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        void Report(FreshSoftwareInstallPhase phase, string message) => progress?.Report(new(phase, message, null, _canCancel));
    }

    internal static FreshSoftwareCommand BuildCommand(FreshSoftwareInstallPlan plan, string packagePath) => plan.App switch
    {
        SoftwareApp.Vacs => new(packagePath, ["/S", "/AllUsers"], true, plan.InstallRoot),
        SoftwareApp.TrackAudio => new(packagePath, ["/S", "/currentuser"], false, plan.InstallRoot),
        _ => throw new InvalidOperationException("vATIS requires its verified fresh-Setup adapter.")
    };

    private async Task EnterAsync(CancellationToken token)
    {
        if (RestartRequired) throw new InvalidOperationException("Restart Windows before further application installation.");
        if (!await _gate.WaitAsync(0, token).ConfigureAwait(false)) throw new InvalidOperationException("Another fresh installation operation is still in progress.");
        _canCancel = true;
    }
    private void ValidateOriginal(FreshSoftwareInstallPlan plan)
    {
        ValidateRelease(plan.App, plan.Release, plan.AllowVatisBeta);
        if (plan.App == SoftwareApp.Vatis) _validateVatisRelease(plan.Release);
        ValidateAbsent(plan.App, plan.ConfiguredExePath, plan.InstallCandidates, plan.InstallRoot);
        RemovalFileService.Verify(plan.BackupFiles);
        foreach (var path in plan.MissingDataRoots)
        {
            SoftwareInstaller.RejectReparse(path);
            if (Exists(path)) throw new IOException("Settings appeared after review. Review the installation again and export those settings first.");
        }
    }
    private void ValidateAbsent(SoftwareApp app, string configured, IReadOnlyList<string> candidates, string root)
    {
        if (_environment.Software.IsRunning(app)) throw new InvalidOperationException("Close " + Name(app) + " in every Windows session before reviewing installation.");
        if (_environment.PrerequisiteProblem(app) is { } problem) throw new InvalidOperationException(problem);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = SoftwareInstaller.FullPath(configured); SoftwareInstaller.RejectReparse(path);
            if (Exists(path)) throw new InvalidOperationException("A configured copy already exists. Use Settings to adopt it and the update action for an installed application.");
            if (Directory.Exists(Path.GetDirectoryName(path)))
                throw new InvalidOperationException("The configured executable is missing but its previous folder remains. Review that folder or clear the stale path in Settings before a fresh installation.");
        }
        if (app == SoftwareApp.Vatis) _validateVatis(root);
        else if (app == SoftwareApp.VatEfs) _validateVatEfs(root);
        else if (_environment.HasInstallerResidue(app) || _environment.Software.ReadRegistrations(app).Count != 0)
            throw new InvalidOperationException("An existing registration or installer destination was found. Adopt the installation in Settings, or remove the old copy explicitly before fresh installation.");
        foreach (var path in candidates)
        {
            SoftwareInstaller.RejectReparse(path);
            if (Exists(path)) throw new InvalidOperationException("An installation folder or leftover files already exist: " + path + ". Review/remove them explicitly; fresh installation will not overwrite them.");
        }
    }
    private void VerifyRecovery(FreshSoftwareInstallPlan plan)
    {
        if (plan.BackupDestination == null) return;
        if (LastBackupFolder == null) throw new IOException("The required recovery export did not complete.");
        RemovalFileService.VerifyBackup(plan.BackupFiles, LastBackupFolder);
    }
    private async Task ValidateReleaseUnchangedAsync(FreshSoftwareInstallPlan plan, CancellationToken token)
    {
        var current = await _releaseSource(plan.App, plan.AllowVatisBeta, token).ConfigureAwait(false);
        if (current != plan.Release) throw new IOException("The official release changed after review. Check and review the new package before installing.");
    }
    private static void ValidateRelease(SoftwareApp app, SoftwareRelease release, bool allowBeta)
    {
        if (release.App != app || !SoftwareVersion.TryParse(release.Version, out var version) ||
            version.IsPrerelease && (app != SoftwareApp.Vatis || !allowBeta || version.PrereleaseLabel != "beta") ||
            !SoftwareReleaseSource.IsAllowedDownloadUri(release, release.DownloadUri) || release.Size <= 0 || release.Size > SoftwareReleaseSource.MaximumPackageSize ||
            release.Sha256.Length != 64 || !release.Sha256.All(char.IsAsciiHexDigit) || release.FileName != Path.GetFileName(release.FileName) ||
            release.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || release.FileName is "." or ".." || release.FileName.EndsWith('.') || release.FileName.EndsWith(' '))
            throw new InvalidDataException("The fresh-install release metadata is unsupported or incomplete.");
    }
    private (string Root, string Exe, string Scope, IReadOnlyList<string> Candidates) Layout(SoftwareApp app)
    {
        var local = _environment.LocalAppData;
        string root, name, scope; string[] candidates;
        switch (app)
        {
            case SoftwareApp.Vacs:
                root = Path.Combine(_environment.ProgramFiles, "vacs"); name = "vacs-client.exe"; scope = "AllUsers";
                candidates = [root, Path.Combine(_environment.ProgramFilesX86, "vacs"), Path.Combine(local, "vacs")]; break;
            case SoftwareApp.TrackAudio:
                root = Path.Combine(local, "Programs", "trackaudio"); name = "trackaudio.exe"; scope = "CurrentUser";
                candidates = [root, Path.Combine(_environment.ProgramFiles, "TrackAudio"), Path.Combine(_environment.ProgramFilesX86, "TrackAudio")]; break;
            case SoftwareApp.Vatis:
                root = Path.Combine(local, "org.vatsim.vatis"); name = Path.Combine("current", "vATIS.exe"); scope = "CurrentUser";
                candidates = [root, Path.Combine(local, "vATIS"), Path.Combine(_environment.ProgramFiles, "vATIS"), Path.Combine(_environment.ProgramFilesX86, "vATIS")]; break;
            case SoftwareApp.VatEfs:
                root = Path.Combine(_environment.ProgramFiles, "VatEFS"); name = "efs.exe"; scope = "AllUsers";
                candidates = [root, Path.Combine(_environment.ProgramFilesX86, "VatEFS")]; break;
            default: throw new ArgumentOutOfRangeException(nameof(app));
        }
        return (SoftwareInstaller.FullPath(root), Path.Combine(root, name), scope,
            candidates.Select(SoftwareInstaller.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }
    private IReadOnlyList<string> DataRoots(SoftwareApp app) => app switch
    {
        SoftwareApp.Vacs => [Path.Combine(_environment.RoamingAppData, "app.vacs.vacs-client"), Path.Combine(_environment.LocalAppData, "app.vacs.vacs-client")],
        SoftwareApp.TrackAudio => [Path.Combine(_environment.RoamingAppData, "trackaudio")],
        SoftwareApp.Vatis => [], // Any surviving vATIS root is refused by the Setup adapter, including data-only roots.
        SoftwareApp.VatEfs => [], // External EuroScope/browser settings are not installer-managed; the installation root must be absent.
        _ => throw new ArgumentOutOfRangeException(nameof(app))
    };
    private async Task DownloadAsync(SoftwareRelease release, string path, IProgress<FreshSoftwareInstallProgress>? progress, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(_downloadTimeout);
        try
        {
            var uri = release.DownloadUri;
            for (int redirects = 0; ; redirects++)
            {
                if (!SoftwareReleaseSource.IsAllowedDownloadUri(release, uri, redirects > 0)) throw new InvalidDataException("The installer download left its official hosts.");
                using var request = new HttpRequestMessage(HttpMethod.Get, uri); request.Headers.UserAgent.ParseAdd("SwedenFirLaunchpad/2.0");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if (response.RequestMessage?.RequestUri is { } actual && actual != uri) throw new InvalidDataException("Automatic download redirects must be disabled.");
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    if (redirects >= 5 || response.Headers.Location == null) throw new InvalidDataException("The installer download redirected unexpectedly.");
                    uri = new Uri(uri, response.Headers.Location); continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is { } size && size != release.Size) throw new InvalidDataException("The installer size differs from the reviewed official release.");
                await using var source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920]; long total = 0; int lastPercent = -1;
                while (true)
                {
                    int count = await source.ReadAsync(buffer, deadline.Token).ConfigureAwait(false); if (count == 0) break;
                    total += count; if (total > release.Size) throw new InvalidDataException("The installer exceeds its reviewed size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
                    int percent = (int)(total * 100 / release.Size);
                    if (percent != lastPercent) { lastPercent = percent; progress?.Report(new(FreshSoftwareInstallPhase.Downloading, "Downloading " + Name(release.App) + "…", percent, true)); }
                }
                if (total != release.Size) throw new InvalidDataException("The installer download is incomplete.");
                return;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("The installer download timed out. No installer was started."); }
    }
    private static async Task VerifyBytesAsync(SoftwareRelease release, FileStream file, CancellationToken token)
    {
        file.Position = 0;
        if (file.Length != release.Size || !Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The installer does not match the official release SHA-256 and size.");
    }
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
    private static bool Within(string root, string path) => Same(root, path) || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool Overlaps(string a, string b) => Within(a, b) || Within(b, a);
    private static string Name(SoftwareApp app) => app switch
    { SoftwareApp.Vacs => "VACS", SoftwareApp.Vatis => "vATIS", SoftwareApp.VatEfs => "VatEFS", _ => "TrackAudio" };
    private sealed class InlineProgress(Action<string> action) : IProgress<string> { public void Report(string value) => action(value); }
}

internal static class FreshSoftwareInstallNative
{
    internal static bool HasInstallerResidue(SoftwareApp app)
    {
        if (app == SoftwareApp.Vatis) throw new InvalidOperationException("vATIS registration checks belong to its Setup adapter.");
        var name = app == SoftwareApp.Vacs ? "vacs" : SoftwareInstaller.TrackAudioGuid;
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var registration = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + name);
            using var destination = root.OpenSubKey(app == SoftwareApp.Vacs ? @"Software\vacs\vacs" : @"Software\" + SoftwareInstaller.TrackAudioGuid);
            if (registration != null || destination != null) return true;
        }
        return false;
    }
    internal static string BuildArguments(FreshSoftwareCommand command)
    {
        var root = SoftwareInstaller.FullPath(command.InstallDirectory);
        if (root.Any(c => c is '"' or '\r' or '\n')) throw new InvalidDataException("The installer destination contains unsupported characters.");
        // NSIS consumes the remainder of the command line after /D=. It must be last and unquoted.
        return string.Join(" ", command.Arguments.Select(SoftwareInstallerNative.QuoteArgument)) + " /D=" + root;
    }
    internal static async Task<int> RunAsync(FreshSoftwareCommand command)
    {
        var info = new ProcessStartInfo
        {
            FileName = command.Executable, Arguments = BuildArguments(command), WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = command.Elevate, Verb = command.Elevate ? "runas" : "", CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        Process? process;
        try { process = Process.Start(info); }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223) { return 1223; }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 740 && !command.Elevate)
        { throw new IOException("This per-user installer unexpectedly requires elevation. Use the vendor installer manually to keep the intended Windows account and destination.", exception); }
        if (process == null) throw new IOException("Windows did not provide an installer process to monitor.");
        using (process) { await process.WaitForExitAsync().ConfigureAwait(false); return process.ExitCode; }
    }
}
