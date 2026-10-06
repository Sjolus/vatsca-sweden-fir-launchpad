using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

public interface ISoftwareReleaseSource
{
    Task<SoftwareRelease?> GetLatestAsync(SoftwareApp app, string installedVersion, CancellationToken cancellationToken);
}

public interface ISoftwareInstaller
{
    bool RestartRequired => false;
    SoftwareInstallation Inspect(SoftwareApp app, string exePath);
    bool IsRunning(SoftwareInstallation installation);
    Task VerifyPackageAsync(SoftwareRelease release, string packagePath, CancellationToken cancellationToken);
    Task<string?> BackupAsync(SoftwareInstallation installation, IProgress<string>? progress, CancellationToken cancellationToken);
    Task<SoftwareInstallResult> InstallAsync(SoftwareInstallation installation, SoftwareRelease release,
        string packagePath, IProgress<string>? progress);
}

/// <summary>Coordinates explicit updates. Constructing or checking never installs or starts a tool.</summary>
public sealed class SoftwareUpdateService
{
    public const long MaximumPackageBytes = 1024L * 1024 * 1024;
    private readonly ISoftwareReleaseSource _source;
    private readonly ISoftwareInstaller _installer;
    private readonly HttpClient _http;
    private readonly string _cacheRoot;
    private readonly TimeSpan _downloadTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<SoftwareApp, SoftwareUpdateState> _states = new();
    private CancellationTokenSource? _activeCancellation;
    private SoftwareApp? _activeApp;
    private bool _installing;

    /// <param name="http">Caller-owned client; its handler must set AllowAutoRedirect=false.</param>
    public SoftwareUpdateService(ISoftwareReleaseSource source, ISoftwareInstaller installer,
        HttpClient http, string? cacheRoot = null)
        : this(source, installer, http, cacheRoot, TimeSpan.FromMinutes(20)) { }

    internal SoftwareUpdateService(ISoftwareReleaseSource source, ISoftwareInstaller installer,
        HttpClient http, string? cacheRoot, TimeSpan downloadTimeout)
    {
        if (downloadTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(downloadTimeout));
        _source = source;
        _installer = installer;
        _http = http;
        _downloadTimeout = downloadTimeout;
        _cacheRoot = Path.GetFullPath(cacheRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VatscaUpdateChecker", "SoftwareUpdates"));
    }

    public event Action<SoftwareUpdateState>? StateChanged;
    public bool IsBusy => _gate.CurrentCount == 0;
    public bool RestartRequired => _installer.RestartRequired;
    public bool CanCancel { get { lock (_sync) return _activeCancellation != null && !_installing; } }

    public SoftwareUpdateState GetState(SoftwareApp app)
    {
        lock (_sync)
            return _states.GetValueOrDefault(app) ?? new(app, SoftwareUpdatePhase.Idle, "Not checked.");
    }

    public void Cancel(SoftwareApp app)
    {
        lock (_sync)
        {
            if (_activeApp == app && !_installing)
                _activeCancellation?.Cancel();
        }
    }

    public async Task<SoftwareUpdateState> CheckAsync(SoftwareApp app, string exePath,
        CancellationToken cancellationToken = default)
    {
        using var operation = await EnterAsync(app, cancellationToken).ConfigureAwait(false);
        var token = operation.Token;
        try
        {
            SetState(new(app, SoftwareUpdatePhase.Checking, "Checking installation and release…"));
            token.ThrowIfCancellationRequested();
            var installation = await Task.Run(() => _installer.Inspect(app, exePath), token).ConfigureAwait(false);
            if (!installation.CanUpdate)
                return SetState(new(app, SoftwareUpdatePhase.Unavailable,
                    installation.Reason ?? "This installation cannot be updated in Launchpad.", installation));
            ValidateInstallation(app, installation);
            var release = await _source.GetLatestAsync(app, installation.Version, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (release == null)
                return SetState(new(app, SoftwareUpdatePhase.Unavailable,
                    "No compatible release was found.", installation));
            ValidateRelease(app, release);
            var comparison = Compare(release.Version, installation.Version);
            return SetState(new(app, comparison > 0 ? SoftwareUpdatePhase.Available : SoftwareUpdatePhase.Current,
                comparison > 0 ? "Update available." : "Installed version is current or newer.", installation, release));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return SetState(GetState(app) with { Phase = SoftwareUpdatePhase.Cancelled, Message = "Check cancelled.", Release = null });
        }
        catch (Exception ex)
        {
            return Fail(app, ex);
        }
    }

    public async Task<SoftwareUpdateState> UpdateAsync(SoftwareApp app,
        CancellationToken cancellationToken = default)
    {
        using var operation = await EnterAsync(app, cancellationToken).ConfigureAwait(false);
        var token = operation.Token;
        string? workFolder = null;
        string? packagePath = null;
        try
        {
            if (RestartRequired) throw new InvalidOperationException("Restart Windows before further application updates.");
            var preview = GetState(app);
            if (!preview.CanUpdate || preview.Installation == null || preview.Release == null)
                throw new InvalidOperationException("Check for an available update before installing.");
            var installation = preview.Installation;
            var release = preview.Release;
            ValidateRelease(app, release);
            await Task.Run(() => ValidateUnchangedInstallation(installation), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            SetState(preview with { Phase = SoftwareUpdatePhase.Downloading, Message = "Downloading update…", ProgressPercent = 0 });
            EnsureNoReparsePoints(_cacheRoot);
            Directory.CreateDirectory(_cacheRoot);
            workFolder = Path.Combine(_cacheRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workFolder);
            EnsureNoReparsePoints(workFolder);
            packagePath = Path.Combine(workFolder, release.FileName);
            await DownloadAsync(app, release, packagePath, token).ConfigureAwait(false);

            // Keep the verified bytes read-locked until the installer has exited.
            using (var packageLock = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                SetState(GetState(app) with { Phase = SoftwareUpdatePhase.Verifying, Message = "Verifying downloaded package…", ProgressPercent = null });
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(packageLock, token).ConfigureAwait(false));
                if (packageLock.Length != release.Size || !hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The downloaded package does not match the published size and SHA-256.");
                await _installer.VerifyPackageAsync(release, packagePath, token).ConfigureAwait(false);
                ValidateUnchangedInstallation(installation);
                await ValidateUnchangedReleaseAsync(release, installation.Version, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                SetState(GetState(app) with { Phase = SoftwareUpdatePhase.BackingUp, Message = "Backing up application settings…" });
                var backup = await _installer.BackupAsync(installation,
                    new InlineProgress(message => UpdateMessage(app, message)), token).ConfigureAwait(false);
                SetState(GetState(app) with { BackupFolder = backup });

                // Backup/source work may take time. Recheck both immediately before applying.
                await ValidateUnchangedReleaseAsync(release, installation.Version, token).ConfigureAwait(false);
                ValidateUnchangedInstallation(installation);
                EnsureNoReparsePoints(packagePath);
                lock (_sync)
                {
                    token.ThrowIfCancellationRequested();
                    _installing = true;
                }
                SetState(GetState(app) with { Phase = SoftwareUpdatePhase.Installing,
                    Message = "Installing update. Keep Launchpad open until this finishes.", ProgressPercent = null });
                var result = await _installer.InstallAsync(installation, release, packagePath,
                    new InlineProgress(message => UpdateMessage(app, message))).ConfigureAwait(false);
                var updated = _installer.Inspect(app, result.ExecutablePath);
                ValidateInstallation(app, updated);
                if (!updated.CanUpdate || Compare(updated.Version, release.Version) != 0)
                    throw new InvalidDataException("The installed version could not be confirmed. The settings backup has been retained.");
                if (_installer.IsRunning(updated))
                    throw new InvalidOperationException("The installer unexpectedly started the application. Its process has been left untouched.");
                return SetState(GetState(app) with { Phase = SoftwareUpdatePhase.Completed,
                    Message = "Update installed. The application remains closed." + (RestartRequired ? " Restart Windows before further changes." : ""), Installation = updated, ProgressPercent = null });
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested && !IsInstalling)
        {
            return SetState(GetState(app) with { Phase = SoftwareUpdatePhase.Cancelled,
                Message = "Update cancelled. Check again before retrying.", Release = null, ProgressPercent = null });
        }
        catch (Exception ex)
        {
            return Fail(app, ex);
        }
        finally
        {
            // Delete only our unique download, never application files or settings backups.
            try
            {
                if (workFolder != null)
                {
                    EnsureNoReparsePoints(workFolder);
                    if (packagePath != null && File.Exists(packagePath)) File.Delete(packagePath);
                    if (Directory.Exists(workFolder)) Directory.Delete(workFolder, recursive: false);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private bool IsInstalling { get { lock (_sync) return _installing; } }

    private void ValidateUnchangedInstallation(SoftwareInstallation expected)
    {
        var current = _installer.Inspect(expected.App, expected.ExePath);
        ValidateInstallation(expected.App, current);
        if (!current.CanUpdate || current != expected)
            throw new InvalidOperationException("The installation changed since the check. Check again before updating.");
        if (_installer.IsRunning(current))
            throw new InvalidOperationException("Close the application before updating it.");
    }

    private async Task ValidateUnchangedReleaseAsync(SoftwareRelease expected, string installedVersion, CancellationToken token)
    {
        var current = await _source.GetLatestAsync(expected.App, installedVersion, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (current != expected || Compare(expected.Version, installedVersion) <= 0)
            throw new InvalidOperationException("The release changed since the check. Check again before updating.");
    }

    private static int Compare(string left, string right)
    {
        if (!SoftwareVersion.TryCompare(left, right, out var comparison))
            throw new InvalidDataException("The application version could not be compared safely.");
        return comparison;
    }

    private static void ValidateInstallation(SoftwareApp app, SoftwareInstallation installation)
    {
        if (installation.App != app || string.IsNullOrWhiteSpace(installation.ExePath)
            || !Path.IsPathFullyQualified(installation.ExePath) || string.IsNullOrWhiteSpace(installation.RootPath)
            || !Path.IsPathFullyQualified(installation.RootPath))
            throw new InvalidDataException("The detected installation is not valid.");
        Compare(installation.Version, installation.Version);
    }

    private static void ValidateRelease(SoftwareApp app, SoftwareRelease release)
    {
        if (release.App != app || !release.DownloadUri.IsAbsoluteUri
            || release.DownloadUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(release.DownloadUri.UserInfo)
            || !SoftwareReleaseSource.IsAllowedDownloadUri(release, release.DownloadUri)
            || string.IsNullOrWhiteSpace(release.FileName) || release.FileName.Length > 180
            || release.FileName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_')
            || release.FileName is "." or ".." || release.FileName.EndsWith('.')
            || release.Size <= 0 || release.Size > MaximumPackageBytes
            || release.Sha256.Length != 64 || !release.Sha256.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("The release package metadata is not valid.");
        Compare(release.Version, release.Version);
    }

    private async Task DownloadAsync(SoftwareApp app, SoftwareRelease release, string path, CancellationToken token)
    {
        // ResponseHeadersRead makes HttpClient.Timeout stop at headers; this deadline also bounds the body.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_downloadTimeout);
        try { await DownloadCoreAsync(app, release, path, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
        {
            throw new TimeoutException("The package download timed out. No installer was started.");
        }
    }

    private async Task DownloadCoreAsync(SoftwareApp app, SoftwareRelease release, string path, CancellationToken token)
    {
        using var response = await OpenDownloadAsync(release, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var finalUri = response.RequestMessage?.RequestUri ?? release.DownloadUri;
        if (!SoftwareReleaseSource.IsAllowedDownloadUri(release, finalUri, allowRedirect: true))
            throw new InvalidDataException("The package redirected outside its official download hosts.");
        if (response.Content.Headers.ContentLength is long length && length != release.Size)
            throw new InvalidDataException("The server's package size differs from the checked release.");
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new byte[81920];
        long total = 0;
        int previousPercent = -1;
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            total += read;
            if (total > release.Size || total > MaximumPackageBytes)
                throw new InvalidDataException("The downloaded package exceeds the checked size.");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            var percent = (int)(total * 100 / release.Size);
            if (percent != previousPercent)
            {
                previousPercent = percent;
                SetState(GetState(app) with { ProgressPercent = percent });
            }
        }
        if (total != release.Size) throw new InvalidDataException("The package download was incomplete.");
    }

    private async Task<HttpResponseMessage> OpenDownloadAsync(SoftwareRelease release, CancellationToken token)
    {
        var uri = release.DownloadUri;
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (!SoftwareReleaseSource.IsAllowedDownloadUri(release, uri, allowRedirect: redirects > 0))
                throw new InvalidDataException("The package redirected outside its official download hosts.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("SwedenFirLaunchpad/2.0");
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308))
                return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location == null) throw new InvalidDataException("The package redirect has no destination.");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
        throw new InvalidDataException("The package exceeded the download redirect limit.");
    }

    private static void EnsureNoReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The update cache cannot contain linked folders or files.");
        }
    }

    private void UpdateMessage(SoftwareApp app, string message) => SetState(GetState(app) with { Message = message });

    private SoftwareUpdateState Fail(SoftwareApp app, Exception ex) => SetState(GetState(app) with
    {
        Phase = SoftwareUpdatePhase.Error,
        Message = ex.Message + (RestartRequired ? " Restart Windows before further changes." : " Check again before retrying.")
            + (GetState(app).BackupFolder is { } backup ? " Settings backup: " + backup : ""),
        Release = null,
        ProgressPercent = null
    });

    private SoftwareUpdateState SetState(SoftwareUpdateState state)
    {
        lock (_sync) _states[state.App] = state;
        Notify(state);
        return state;
    }

    private void Notify(SoftwareUpdateState state)
    {
        if (StateChanged == null) return;
        foreach (Action<SoftwareUpdateState> observer in StateChanged.GetInvocationList())
        {
            // A display failure must not interrupt an installer or strand the operation gate.
            try { observer(state); } catch { }
        }
    }

    private async Task<Operation> EnterAsync(SoftwareApp app, CancellationToken token)
    {
        if (!await _gate.WaitAsync(0, token).ConfigureAwait(false))
            throw new InvalidOperationException("Another software update operation is already running.");
        lock (_sync)
        {
            _activeApp = app;
            _activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            _installing = false;
            return new Operation(this, _activeCancellation.Token);
        }
    }

    private sealed class Operation(SoftwareUpdateService owner, CancellationToken token) : IDisposable
    {
        public CancellationToken Token { get; } = token;
        public void Dispose()
        {
            SoftwareApp? app;
            lock (owner._sync)
            {
                app = owner._activeApp;
                owner._activeCancellation?.Dispose();
                owner._activeCancellation = null;
                owner._activeApp = null;
                owner._installing = false;
                owner._gate.Release();
            }
            if (app != null) owner.Notify(owner.GetState(app.Value));
        }
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
