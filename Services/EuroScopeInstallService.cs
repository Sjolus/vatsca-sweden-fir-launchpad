using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

internal sealed record EuroScopePackage(Uri Url, string FileName, long Size, string Sha256)
{
    internal static EuroScopePackage Supported { get; } = new(new(EuroScopePolicy.DownloadUrl),
        EuroScopePolicy.PackageFileName, EuroScopePolicy.PackageSize, EuroScopePolicy.PackageSha256);
}

/// <summary>All Windows probes and process execution are replaced by synthetic delegates in tests.</summary>
internal sealed class EuroScopeInstallEnvironment
{
    public AtcRemovalEnvironment Removal { get; init; } = new();
    public Func<string?> PrerequisiteProblem { get; init; } = EuroScopePrerequisiteService.GetProblem;
}

/// <summary>Explicit pinned MSI installation. Preview, download and backup never run EuroScope.</summary>
public sealed class EuroScopeInstallService
{
    private readonly HttpClient _http;
    private readonly EuroScopeInstallEnvironment _environment;
    private readonly EuroScopePackage _package;
    private readonly string _cacheRoot;
    private readonly TimeSpan _downloadTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _canCancel;

    /// <param name="http">Caller-owned HTTP client whose handler must disable automatic redirects.</param>
    public EuroScopeInstallService(HttpClient http, string? cacheRoot = null)
        : this(http, new(), EuroScopePackage.Supported, cacheRoot, TimeSpan.FromMinutes(20)) { }

    internal EuroScopeInstallService(HttpClient http, EuroScopeInstallEnvironment environment,
        EuroScopePackage package, string? cacheRoot = null, TimeSpan? downloadTimeout = null)
    {
        _http = http; _environment = environment; _package = package;
        _cacheRoot = SoftwareInstaller.FullPath(cacheRoot ?? Path.Combine(environment.Removal.LocalAppData,
            "VatscaUpdateChecker", "EuroScopeInstall"));
        _downloadTimeout = downloadTimeout ?? TimeSpan.FromMinutes(20);
        if (_downloadTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(downloadTimeout));
    }
    public string? LastBackupFolder { get; private set; }
    public bool RestartRequired { get; private set; }
    public bool IsBusy => _gate.CurrentCount == 0;
    public bool CanCancel => IsBusy && _canCancel;

    public EuroScopeInstallPlan Preview(AppSettings settings, string? backupDestination)
    {
        RequireNoRestart();
        if (IsBusy) throw new InvalidOperationException("A EuroScope installation is already in progress.");
        RequireReady();
        var environment = _environment.Removal;
        var registrations = environment.ReadMsiRegistrations();
        if (registrations.Count > 1)
            throw new InvalidOperationException("Multiple EuroScope MSI registrations were found. Resolve them manually before installing the supported build.");
        var copiedSettings = new AppSettings { EuroscopeExePath = settings.EuroscopeExePath, EuroscopeDataPath = settings.EuroscopeDataPath };
        AtcRemovalTarget? target = null;
        AtcMsiRegistration? registration = null;
        string root, exe, scope;
        string? version = null;
        var action = EuroScopeInstallAction.Install;
        if (string.IsNullOrWhiteSpace(settings.EuroscopeExePath))
        {
            if (registrations.Count != 0)
                throw new InvalidOperationException("A registered EuroScope copy exists. Find and adopt its executable in Settings before reviewing repair or replacement.");
            root = Path.Combine(environment.ProgramFilesX86Directory, "EuroScope");
            exe = Path.Combine(root, "EuroScope.exe"); scope = "AllUsers";
            foreach (var folder in new[] { environment.ProgramFilesX86Directory, environment.ProgramFilesDirectory })
            {
                var candidate = Path.Combine(folder, "EuroScope", "EuroScope.exe");
                SoftwareInstaller.RejectReparse(candidate);
                if (File.Exists(candidate)) throw new InvalidOperationException("An unregistered EuroScope copy exists. Adopt or remove it manually before a fresh installation.");
            }
            SoftwareInstaller.RejectReparse(root);
            if (File.Exists(root) || Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
                throw new InvalidOperationException("The default EuroScope installation folder is not empty. Review that existing copy manually.");
        }
        else
        {
            exe = SoftwareInstaller.FullPath(settings.EuroscopeExePath);
            if (registrations.Count != 1 || !File.Exists(exe))
                throw new InvalidOperationException("Repair/replacement requires one recognized installed MSI and its configured executable. Portable, missing or unknown copies require manual handling.");
            target = new AtcRemovalCatalog(environment).Discover(copiedSettings).Single(t => t.Id == AtcRemovalApp.EuroScope);
            if (!target.CanRemoveApplication || target.VendorSpec?.Kind != AtcRemovalVendorKind.Msi)
                throw new InvalidOperationException(target.Reason ?? "The EuroScope installation cannot be identified safely.");
            registration = registrations[0];
            if (!registration.ProductCode.Equals(target.VendorSpec.MsiProductCode, StringComparison.OrdinalIgnoreCase) || registration.Scope != target.VendorSpec.MsiScope)
                throw new InvalidOperationException("The configured EuroScope copy does not match the single registered installation.");
            root = Path.GetDirectoryName(exe)!; scope = registration.Scope;
            version = environment.Software.ReadBinary(exe).ProductVersion;
            if (!Version.TryParse(version, out _)) throw new InvalidDataException("The installed EuroScope version is not recognized.");
            action = version == EuroScopePolicy.SupportedVersion && registration.ProductCode.Equals(EuroScopePolicy.ProductCode, StringComparison.OrdinalIgnoreCase)
                ? EuroScopeInstallAction.Repair : EuroScopeInstallAction.Replace;
            // A second recognizable copy at a normal install location must not be silently ignored.
            foreach (var folder in new[] { environment.ProgramFilesX86Directory, environment.ProgramFilesDirectory })
            {
                var candidate = Path.Combine(folder, "EuroScope", "EuroScope.exe");
                SoftwareInstaller.RejectReparse(candidate);
                if (File.Exists(candidate) && !Same(candidate, exe))
                    throw new InvalidOperationException("Another EuroScope executable exists in a standard installation folder. Resolve the multiple copies manually.");
            }
        }
        root = SoftwareInstaller.FullPath(root); exe = SoftwareInstaller.FullPath(exe);
        var msiexec = Path.Combine(environment.WindowsDirectory, "System32", "msiexec.exe");
        AtcRemovalCatalog.RequireFile(msiexec);
        var dataCandidates = new List<string>
        {
            Path.Combine(environment.RoamingAppData, "EuroScope"),
            Path.Combine(environment.DocumentsDirectory, "EuroScope")
        };
        if (!string.IsNullOrWhiteSpace(settings.EuroscopeDataPath)) dataCandidates.Add(SoftwareInstaller.FullPath(settings.EuroscopeDataPath));
        foreach (var path in dataCandidates) SoftwareInstaller.RejectReparse(path);
        var candidates = dataCandidates.Append(root).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var existing = candidates.Where(Exists).ToArray();
        var backup = RemovalFileService.Preview(Minimize(existing));
        var installFiles = RemovalFileService.Preview(Exists(root) ? new[] { root } : []);
        var externalData = RemovalFileService.Preview(Minimize(dataCandidates.Where(Exists).Where(path => !Within(root, path))));
        var destination = string.IsNullOrWhiteSpace(backupDestination) ? null : SoftwareInstaller.FullPath(backupDestination);
        if (destination == null && (action != EuroScopeInstallAction.Install || backup.Files.Count != 0 || backup.Directories.Count != 0))
            throw new InvalidOperationException("Choose an external recovery export folder before changing an existing EuroScope installation or data folder.");
        if (destination != null)
        {
            SoftwareInstaller.RejectReparse(destination);
            if (!Directory.Exists(destination)) throw new DirectoryNotFoundException("Choose an existing private recovery folder.");
            if (candidates.Any(path => Overlaps(path, destination)))
                throw new InvalidOperationException("The recovery folder must be outside the installation and every reviewed data folder.");
        }
        if (candidates.Any(path => Overlaps(path, _cacheRoot)) || destination != null && Overlaps(destination, _cacheRoot))
            throw new InvalidOperationException("The MSI download cache overlaps the installation, data or recovery folder. Resolve this layout manually.");
        var downgrade = version != null && Version.Parse(version) > Version.Parse(EuroScopePolicy.SupportedVersion);
        var review = new StringBuilder();
        review.AppendLine(action == EuroScopeInstallAction.Install ? "Install the supported EuroScope build." :
            action == EuroScopeInstallAction.Repair ? "Repair the supported EuroScope build; package-owned files may be replaced." :
            downgrade ? "DOWNGRADE: uninstall the newer EuroScope build and replace it with the supported build." :
            "Replace the registered EuroScope build with the supported build.");
        review.AppendLine($"Version: {version ?? "not installed"} → {EuroScopePolicy.SupportedVersion}");
        review.AppendLine("Installation folder: " + root);
        review.AppendLine("Scope: " + scope + (scope == "AllUsers" ? " (Windows elevation required)" : ""));
        review.AppendLine("Pinned official MSI: " + EuroScopePolicy.DownloadUrl);
        review.AppendLine("The MSI is unsigned. Its exact size and SHA-256 are pinned to the reviewed official package; no latest-version lookup is performed.");
        review.AppendLine(destination == null ? "No existing files need a recovery export." : "Private recovery export: " + destination);
        review.AppendLine($"Recovery snapshot: {backup.Files.Count:N0} files, {backup.TotalBytes / 1048576d:N1} MB.");
        review.AppendLine("The MSI also manages its standard EuroScope AppData files, shortcuts and font. Personal changes to package-owned files may need manual restoration from the export. External profile references are not discovered or changed by Launchpad.");
        review.AppendLine("Download and recovery preparation can be cancelled. Once installation/removal begins, wait for completion. EuroScope is never launched and Windows is never restarted by Launchpad.");
        review.AppendLine("\nComplete recovery file inventory:");
        foreach (var file in backup.Files) review.AppendLine(file.FullPath + $" ({file.Length:N0} bytes)");
        foreach (var directory in backup.Directories) review.AppendLine(directory.FullPath + " [folder]");
        return new(action, version, downgrade, exe, root, scope, backup, destination, review.ToString(), copiedSettings,
            target, registration, msiexec, AtcRemovalCatalog.Hash(msiexec), installFiles, externalData, candidates.Where(path => !Exists(path)),
            Path.Combine(environment.RoamingAppData, "EuroScope"));
    }

    public async Task<EuroScopeInstallResult> ApplyAsync(EuroScopeInstallPlan plan,
        IProgress<EuroScopeInstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        RequireNoRestart();
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("A EuroScope installation is already in progress.");
        LastBackupFolder = null; _canCancel = true;
        try { return await Task.Run(() => ApplyCoreAsync(plan, progress, cancellationToken), cancellationToken).ConfigureAwait(false); }
        finally { _canCancel = false; _gate.Release(); }
    }

    private async Task<EuroScopeInstallResult> ApplyCoreAsync(EuroScopeInstallPlan plan, IProgress<EuroScopeInstallProgress>? progress, CancellationToken token)
    {
        string? work = null;
        string? packagePath = null;
        try
        {
            Report(EuroScopeInstallPhase.Checking, "Rechecking the reviewed EuroScope installation…");
            ValidateOriginal(plan); token.ThrowIfCancellationRequested();
            SoftwareInstaller.RejectReparse(_cacheRoot); Directory.CreateDirectory(_cacheRoot);
            work = Path.Combine(_cacheRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
            SoftwareInstaller.RejectReparse(work);
            packagePath = Path.Combine(work, _package.FileName);
            await DownloadAsync(packagePath, progress, token).ConfigureAwait(false);
            SoftwareInstaller.RejectReparse(packagePath);
            using var packageLock = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            Report(EuroScopeInstallPhase.Verifying, "Verifying the exact pinned EuroScope MSI…");
            await VerifyPackageAsync(packageLock, token).ConfigureAwait(false);
            ValidateOriginal(plan); token.ThrowIfCancellationRequested();
            if (plan.BackupDestination != null)
            {
                Report(EuroScopeInstallPhase.BackingUp, "Exporting the reviewed installation and EuroScope data…");
                LastBackupFolder = await RemovalFileService.BackupAsync(plan.BackupFiles, plan.BackupDestination,
                    new InlineProgress(message => Report(EuroScopeInstallPhase.BackingUp, message)), token).ConfigureAwait(false);
            }
            ValidateOriginal(plan); VerifyRecovery(plan);
            await VerifyPackageAsync(packageLock, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _canCancel = false;
            Report(EuroScopeInstallPhase.Installing, "Applying the reviewed EuroScope change. Keep Launchpad open until completion.");
            // A progress callback is not trusted to preserve files; recheck immediately before the first process.
            ValidateOriginal(plan); VerifyRecovery(plan); VerifyPackagePath(packagePath);
            if (plan.Action == EuroScopeInstallAction.Replace)
            {
                var removal = _environment.Removal;
                var monitored = new AtcRemovalEnvironment
                {
                    WindowsDirectory = removal.WindowsDirectory, Software = removal.Software,
                    RoamingAppData = removal.RoamingAppData, LocalAppData = removal.LocalAppData,
                    DocumentsDirectory = removal.DocumentsDirectory, ProgramFilesDirectory = removal.ProgramFilesDirectory,
                    ProgramFilesX86Directory = removal.ProgramFilesX86Directory, ReadMsiFootprint = removal.ReadMsiFootprint,
                    ReadMsiRegistrations = removal.ReadMsiRegistrations, GetMsiComponentPath = removal.GetMsiComponentPath,
                    IsProcessRunning = removal.IsProcessRunning,
                    RunAsync = async command => { var code = await removal.RunAsync(command).ConfigureAwait(false); RestartRequired |= code == 3010; return code; }
                };
                await new AtcRemovalVendor(monitored).UninstallAsync(plan.Target!,
                    new InlineProgress(message => Report(EuroScopeInstallPhase.Installing, message))).ConfigureAwait(false);
                if (RestartRequired)
                {
                    Report(EuroScopeInstallPhase.Completed, "The previous EuroScope build was removed, but Windows requires a restart before installation can continue. The supported build has NOT been installed. Keep the recovery export; restart manually and review the remaining installation before continuing.");
                    return new(plan.ExecutablePath, LastBackupFolder, true, plan.Action, false);
                }
                if (_environment.Removal.ReadMsiRegistrations().Count != 0)
                    throw new IOException("A EuroScope MSI registration remains after removal. Installation stopped; keep the recovery export.");
                RequireReady(); VerifyRecovery(plan);
                ValidateOtherCopies(plan.ExecutablePath);
                foreach (var path in plan.MissingPaths.Where(path => !Within(plan.InstallRoot, path)))
                {
                    SoftwareInstaller.RejectReparse(path);
                    if (Exists(path)) throw new IOException("A previously absent data folder appeared during removal. Keep the recovery export and review the new data before continuing.");
                }
                RemovalFileService.VerifyRemaining(plan.InstallFiles);
                RemovalFileService.VerifyRemaining(plan.ExternalDataFiles);
                VerifyMsiexec(plan); VerifyPackagePath(packagePath);
                await VerifyPackageAsync(packageLock, CancellationToken.None).ConfigureAwait(false);
            }
            var exitCode = await _environment.Removal.RunAsync(BuildInstallCommand(plan, packagePath)).ConfigureAwait(false);
            RestartRequired |= exitCode == 3010;
            if (exitCode == 1223) throw new IOException("Windows elevation was cancelled. EuroScope installation did not complete; retain the recovery export if removal had started.");
            if (exitCode is not (0 or 3010)) throw new IOException("EuroScope Windows Installer returned exit code " + exitCode + ". Keep the recovery export and review the installation manually.");
            ValidateInstalled(plan);
            Report(EuroScopeInstallPhase.Completed, RestartRequired ?
                "EuroScope 3.2.3.2 was installed and remains closed. Windows reports that a restart is required; restart manually when ready." :
                "EuroScope 3.2.3.2 was installed and remains closed.");
            return new(plan.ExecutablePath, LastBackupFolder, RestartRequired, plan.Action);
        }
        finally
        {
            // Only our exact unique package and empty staging directory are cleaned up.
            if (packagePath != null) { try { SoftwareInstaller.RejectReparse(packagePath); if (File.Exists(packagePath)) File.Delete(packagePath); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            if (work != null) { try { SoftwareInstaller.RejectReparse(work); if (Directory.Exists(work)) Directory.Delete(work, false); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        void Report(EuroScopeInstallPhase phase, string message) => progress?.Report(new(phase, message, null, _canCancel));
    }

    private void RequireNoRestart()
    {
        if (RestartRequired) throw new InvalidOperationException("Restart Windows before further EuroScope installation or repair.");
    }

    internal static AtcRemovalCommand BuildInstallCommand(EuroScopeInstallPlan plan, string packagePath)
    {
        var args = new List<string> { "/i", packagePath, "/qn", "/norestart", "TARGETDIR=" + plan.InstallRoot,
            plan.Scope == "AllUsers" ? "ALLUSERS=1" : "ALLUSERS=",
            EuroScopeMsiFootprint.DataDirectoryProperty + "=" + plan.DataRoot };
        if (plan.Action == EuroScopeInstallAction.Repair) args.AddRange(["REINSTALL=ALL", "REINSTALLMODE=vamus"]);
        return new(plan.MsiexecPath, args, plan.Scope == "AllUsers");
    }

    private void ValidateOriginal(EuroScopeInstallPlan plan)
    {
        RequireReady(); VerifyMsiexec(plan);
        ValidateOtherCopies(plan.ExecutablePath);
        var registrations = _environment.Removal.ReadMsiRegistrations();
        if (plan.Registration == null)
        {
            if (registrations.Count != 0 || File.Exists(plan.ExecutablePath)) throw new IOException("A EuroScope installation appeared after review. Review a new plan.");
        }
        else
        {
            if (registrations.Count != 1 || registrations[0] != plan.Registration) throw new IOException("The EuroScope MSI registration changed after review.");
            new AtcRemovalCatalog(_environment.Removal).Revalidate(plan.Target!, plan.Settings);
        }
        foreach (var path in plan.MissingPaths)
        {
            SoftwareInstaller.RejectReparse(path);
            if (Exists(path)) throw new IOException("An installation or data folder appeared after review: " + path);
        }
        RemovalFileService.Verify(plan.BackupFiles);
    }
    private void ValidateOtherCopies(string reviewedExe)
    {
        foreach (var folder in new[] { _environment.Removal.ProgramFilesX86Directory, _environment.Removal.ProgramFilesDirectory })
        {
            var candidate = Path.Combine(folder, "EuroScope", "EuroScope.exe");
            SoftwareInstaller.RejectReparse(candidate);
            if (File.Exists(candidate) && !Same(candidate, reviewedExe))
                throw new IOException("Another EuroScope copy appeared in a standard installation folder. Review the multiple copies manually.");
        }
    }
    private void ValidateInstalled(EuroScopeInstallPlan plan)
    {
        var registrations = _environment.Removal.ReadMsiRegistrations();
        if (registrations.Count != 1 || !registrations[0].ProductCode.Equals(EuroScopePolicy.ProductCode, StringComparison.OrdinalIgnoreCase) ||
            registrations[0].Scope != plan.Scope)
            throw new IOException("The supported EuroScope registration and installation scope could not be confirmed. Keep the recovery export.");
        AtcRemovalCatalog.RequireFile(plan.ExecutablePath);
        var binary = _environment.Removal.Software.ReadBinary(plan.ExecutablePath);
        if (binary.ProductVersion != EuroScopePolicy.SupportedVersion || !AtcRemovalCatalog.IsEuroScopeProduct(binary.ProductName) ||
            !AtcRemovalCatalog.MsiMatches(registrations[0], plan.ExecutablePath, binary, AtcRemovalCatalog.ComponentPath(registrations[0], _environment.Removal)))
            throw new IOException("The exact supported EuroScope executable could not be confirmed at the reviewed location. Keep the recovery export.");
        var footprint = _environment.Removal.ReadMsiFootprint(registrations[0], plan.InstallRoot,
            _environment.Removal.RoamingAppData, _environment.Removal.WindowsDirectory);
        if (!Same(footprint.DataRoot, plan.DataRoot))
            throw new IOException("EuroScope's installed data location differs from the reviewed location. Keep the recovery export.");
        if (_environment.Removal.IsProcessRunning("EuroScope")) throw new IOException("EuroScope is running unexpectedly. It has been left untouched; no further action was taken.");
    }
    private void RequireReady()
    {
        if (_environment.Removal.IsProcessRunning("EuroScope")) throw new InvalidOperationException("Close EuroScope in every Windows session before installation or repair.");
        if (_environment.PrerequisiteProblem() is { } problem) throw new InvalidOperationException(problem);
    }
    private static void VerifyMsiexec(EuroScopeInstallPlan plan)
    {
        AtcRemovalCatalog.RequireFile(plan.MsiexecPath);
        if (!AtcRemovalCatalog.Hash(plan.MsiexecPath).Equals(plan.MsiexecHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Windows Installer changed since preview. Review a fresh plan.");
    }
    private void VerifyRecovery(EuroScopeInstallPlan plan)
    {
        if (plan.BackupDestination != null)
        {
            if (LastBackupFolder == null) throw new IOException("The required recovery export did not complete.");
            RemovalFileService.VerifyBackup(plan.BackupFiles, LastBackupFolder);
        }
    }
    private void VerifyPackagePath(string path)
    {
        SoftwareInstaller.RejectReparse(path);
        if (!File.Exists(path) || new FileInfo(path).Length != _package.Size || !AtcRemovalCatalog.Hash(path).Equals(_package.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The pinned MSI changed during preparation.");
    }
    private async Task VerifyPackageAsync(FileStream package, CancellationToken token)
    {
        package.Position = 0;
        if (package.Length != _package.Size || !Convert.ToHexString(await SHA256.HashDataAsync(package, token).ConfigureAwait(false)).Equals(_package.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded EuroScope MSI does not match its pinned size and SHA-256. Nothing will be installed.");
    }

    private async Task DownloadAsync(string destination, IProgress<EuroScopeInstallProgress>? progress, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_downloadTimeout);
        try
        {
            var current = _package.Url;
            for (int redirects = 0; ; redirects++)
            {
                RequireDownloadUri(current);
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.UserAgent.ParseAdd("VatscaUpdateChecker/2.0");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if (response.RequestMessage?.RequestUri is { } actual && actual != current)
                    throw new InvalidDataException("Automatic download redirects are not supported.");
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (redirects >= 3 || response.Headers.Location == null) throw new InvalidDataException("The official MSI download redirected unexpectedly.");
                    current = new Uri(current, response.Headers.Location); continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is { } length && length != _package.Size)
                    throw new InvalidDataException("The official MSI download size changed. Review the supported package policy before continuing.");
                await using var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920]; long count = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    count += read;
                    if (count > _package.Size) throw new InvalidDataException("The EuroScope download exceeds its pinned size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
                    progress?.Report(new(EuroScopeInstallPhase.Downloading, "Downloading the pinned EuroScope 3.2.3.2 MSI…", count * 100d / _package.Size, true));
                }
                if (count != _package.Size) throw new InvalidDataException("The EuroScope download is incomplete.");
                return;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        { throw new TimeoutException("The EuroScope MSI download timed out. No installation was started."); }
    }
    private static void RequireDownloadUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.Host is not ("euroscope.hu" or "www.euroscope.hu") || uri.AbsolutePath != "/install/EuroScopeSetup.3.2.3.2.msi")
            throw new InvalidDataException("The MSI download left the pinned official EuroScope location.");
    }
    private static IReadOnlyList<string> Minimize(IEnumerable<string> roots)
    {
        var result = new List<string>();
        foreach (var path in roots.Select(SoftwareInstaller.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p.Length))
            if (!result.Any(root => Within(root, path))) result.Add(path);
        return result;
    }
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    private static bool Same(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);
    private static bool Within(string root, string path) => Same(root, path) || path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool Overlaps(string left, string right) => Within(left, right) || Within(right, left);
    private sealed class InlineProgress(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
}
