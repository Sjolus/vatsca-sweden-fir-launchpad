using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Velopack;
using Velopack.Exceptions;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

/// <summary>Checks and stages updates. Only RestartToApply may start the updater or exit Launchpad.</summary>
public sealed class LaunchpadUpdateService
{
    public const string RepositoryUrl = "https://github.com/Sjolus/vatsca-sweden-fir-launchpad";
    public const string PackageId = "SwedenFirLaunchpad";
    public const string Channel = "win-x64";
    private const string ReceiptName = "launchpad-ready.json";
    private readonly IVelopackLocator _locator;
    private readonly CheckedSource _source;
    private readonly UpdateManager _manager;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateInfo? _available;
    private VelopackAsset? _ready;

    public LaunchpadUpdateService()
        : this(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false), VelopackLocator.Current) { }

    // An injected source/locator keeps regression checks entirely within synthetic installation directories.
    internal LaunchpadUpdateService(IUpdateSource source, IVelopackLocator locator)
    {
        _locator = locator;
        _source = new CheckedSource(source);
        _manager = new UpdateManager(_source, new UpdateOptions
        {
            ExplicitChannel = Channel,
            AllowVersionDowngrade = false,
            // Full packages avoid a helper process during download. Applying remains an explicit action.
            MaximumDeltasBeforeFallback = -1
        }, locator);
        State = IsInstalled
            ? NewState(LaunchpadUpdateStatus.Idle, "Check for Launchpad updates.")
            : NewState(LaunchpadUpdateStatus.Unsupported, "Use the installer to enable automatic updates. Portable copies can be updated from Downloads.");
    }

    public bool IsInstalled => _manager.IsInstalled && !_manager.IsPortable &&
        string.Equals(_manager.AppId, PackageId, StringComparison.Ordinal);

    public LaunchpadUpdateState State { get; private set; }
    public event EventHandler<LaunchpadUpdateState>? StateChanged;

    public async Task<LaunchpadUpdateState> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!IsInstalled || cancellationToken.IsCancellationRequested || !await _gate.WaitAsync(0).ConfigureAwait(false))
            return State;
        var previous = State;
        var previousAvailable = _available;
        try
        {
            Publish(NewState(LaunchpadUpdateStatus.Checking, "Checking for Launchpad updates…"));
            _ready = null;
            _available = null;

            // UpdatePendingRestart infers readiness from filenames/nuspec and has no trusted checksum.
            // A receipt records the feed hash verified when this service staged the download.
            var receipt = ReadReceipt();
            if (receipt != null && await IsVerifiedPackageAsync(receipt, cancellationToken).ConfigureAwait(false))
                return MarkReady(receipt);

            // This SDK's check has no CancellationToken. Keep the gate until it ends, then honor cancellation;
            // abandoning the task early would allow a second operation to race its shared source/manager.
            var update = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_source.HasFullRelease)
                return Publish(NewState(LaunchpadUpdateStatus.NoFeed, "Installer updates have not been published yet. Open Downloads for existing releases."));
            if (update == null)
                return Publish(NewState(LaunchpadUpdateStatus.UpToDate, "No newer Launchpad installer update is available."));

            _available = new UpdateInfo(update.TargetFullRelease, isDowngrade: false);
            if (await IsVerifiedPackageAsync(update.TargetFullRelease, cancellationToken).ConfigureAwait(false))
            {
                WriteReceipt(update.TargetFullRelease);
                return MarkReady(update.TargetFullRelease);
            }
            return Publish(NewState(LaunchpadUpdateStatus.Available, "A Launchpad update is available to download.", update.TargetFullRelease));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _available = previousAvailable;
            // A previous Ready state must not survive cancellation before its package was reverified.
            return Publish(previous.Status == LaunchpadUpdateStatus.Ready
                ? NewState(LaunchpadUpdateStatus.Idle, "Update check cancelled.")
                : previous with { Message = "Update check cancelled." });
        }
        catch (Exception ex)
        {
            return Publish(NewState(LaunchpadUpdateStatus.Error, ErrorMessage(ex, "check for updates")));
        }
        finally { _gate.Release(); }
    }

    public async Task<LaunchpadUpdateState> DownloadAsync(CancellationToken cancellationToken = default)
    {
        if (!IsInstalled || cancellationToken.IsCancellationRequested || !await _gate.WaitAsync(0).ConfigureAwait(false))
            return State;
        try
        {
            if (_available == null)
                return State;
            var asset = _available.TargetFullRelease;
            _ready = null;
            Publish(NewState(LaunchpadUpdateStatus.Downloading, "Downloading Launchpad…", asset, 0));
            var path = PackagePath(asset);
            // Velopack skips checking existing complete packages. Remove only an invalid target package so
            // a cancelled/corrupt previous download can be repaired by an explicit Download action.
            if (File.Exists(path) && !await IsVerifiedPackageAsync(asset, cancellationToken).ConfigureAwait(false))
            {
                RejectReparse(path);
                File.Delete(path);
            }
            cancellationToken.ThrowIfCancellationRequested();
            await _manager.DownloadUpdatesAsync(_available,
                percent => Publish(NewState(LaunchpadUpdateStatus.Downloading, "Downloading Launchpad…", asset, Math.Clamp(percent, 0, 100))),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!await IsVerifiedPackageAsync(asset, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("Downloaded package did not match its release metadata.");
            WriteReceipt(asset);
            return MarkReady(asset);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Publish(NewState(LaunchpadUpdateStatus.Available, "Download cancelled. You can retry when ready.", _available?.TargetFullRelease));
        }
        catch (Exception ex)
        {
            return Publish(NewState(LaunchpadUpdateStatus.Error, ErrorMessage(ex, "download the update"), _available?.TargetFullRelease));
        }
        finally { _gate.Release(); }
    }

    /// <summary>Call only from the user's explicit Restart and update action, after checking other app work is idle.</summary>
    public void RestartToApply()
    {
        if (!_gate.Wait(0))
            throw new InvalidOperationException("Wait for the current update operation to finish.");
        try
        {
            if (!IsInstalled || State.Status != LaunchpadUpdateStatus.Ready || _ready == null)
                throw new InvalidOperationException("Download and verify a Launchpad update before restarting.");
            if (!IsVerifiedPackageAsync(_ready, CancellationToken.None).GetAwaiter().GetResult())
                throw new InvalidDataException("The downloaded update changed. Check for updates and download it again.");
            _manager.ApplyUpdatesAndRestart(_ready);
        }
        catch (Exception ex)
        {
            _ready = null;
            Publish(NewState(LaunchpadUpdateStatus.Error, ErrorMessage(ex, "restart for the update")));
            throw new InvalidOperationException(State.Message, ex);
        }
        finally { _gate.Release(); }
    }

    private LaunchpadUpdateState MarkReady(VelopackAsset asset)
    {
        _ready = asset;
        return Publish(NewState(LaunchpadUpdateStatus.Ready, "Update downloaded. Restart Launchpad when you are ready to install it.", asset, 100));
    }

    private LaunchpadUpdateState NewState(LaunchpadUpdateStatus status, string message, VelopackAsset? asset = null, int? percent = null) =>
        new(status, message, _manager.CurrentVersion?.ToString(), asset?.Version.ToString(), percent);

    private LaunchpadUpdateState Publish(LaunchpadUpdateState state)
    {
        State = state;
        StateChanged?.Invoke(this, state);
        return state;
    }

    private string PackagePath(VelopackAsset asset)
    {
        ValidateAsset(asset);
        var directory = _locator.PackagesDir ?? throw new InvalidDataException("The installer package directory is unavailable.");
        RejectReparse(directory);
        return Path.Combine(directory, asset.FileName);
    }

    private async Task<bool> IsVerifiedPackageAsync(VelopackAsset asset, CancellationToken cancellationToken)
    {
        var path = PackagePath(asset);
        if (!File.Exists(path)) return false;
        RejectReparse(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        if (stream.Length != asset.Size) return false;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!string.Equals(hash, asset.SHA256, StringComparison.OrdinalIgnoreCase)) return false;
        var package = VelopackAsset.FromNupkgNoChecksum(path);
        return package.PackageId == PackageId && package.Version == asset.Version && package.Type == VelopackAssetType.Full;
    }

    private VelopackAsset? ReadReceipt()
    {
        var directory = _locator.PackagesDir;
        if (directory == null) return null;
        RejectReparse(directory);
        var path = Path.Combine(directory, ReceiptName);
        if (!File.Exists(path)) return null;
        RejectReparse(path);
        if (new FileInfo(path).Length > 16_384) return null;
        try
        {
            var receipt = JsonSerializer.Deserialize<ReadyReceipt>(File.ReadAllText(path));
            if (receipt == null || receipt.Channel != Channel || receipt.PackageId != PackageId ||
                !SemanticVersion.TryParse(receipt.Version, out var version) || version <= _manager.CurrentVersion)
                return null;
            var asset = new VelopackAsset
            {
                PackageId = receipt.PackageId, Version = version, Type = VelopackAssetType.Full,
                FileName = receipt.FileName, Size = receipt.Size, SHA256 = receipt.SHA256
            };
            ValidateAsset(asset);
            return asset;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException) { return null; }
    }

    private void WriteReceipt(VelopackAsset asset)
    {
        var directory = Path.GetDirectoryName(PackagePath(asset))!;
        var path = Path.Combine(directory, ReceiptName);
        RejectReparse(path);
        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.ready");
        try
        {
            var receipt = new ReadyReceipt(PackageId, Channel, asset.Version.ToString(), asset.FileName, asset.Size, asset.SHA256);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, receipt);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void RejectReparse(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The update path is a link and cannot be used safely.");
    }

    private static void ValidateAsset(VelopackAsset asset)
    {
        if (asset.PackageId != PackageId || asset.Type != VelopackAssetType.Full || asset.Version == null || asset.Version.IsPrerelease ||
            string.IsNullOrWhiteSpace(asset.FileName) || asset.FileName != Path.GetFileName(asset.FileName) ||
            asset.FileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !asset.FileName.EndsWith("-full.nupkg", StringComparison.OrdinalIgnoreCase) ||
            asset.Size <= 0 || asset.Size > 2L * 1024 * 1024 * 1024 ||
            asset.SHA256 == null || !Regex.IsMatch(asset.SHA256, "\\A[0-9a-fA-F]{64}\\z"))
            throw new InvalidDataException("The installer release metadata is incomplete or invalid.");
    }

    private static string ErrorMessage(Exception ex, string action) => ex switch
    {
        ChecksumFailedException or InvalidDataException => "The update could not be verified. Check for updates and download it again.",
        HttpRequestException or TaskCanceledException => $"Could not {action}. Check your connection and try again.",
        UnauthorizedAccessException => "The update folder is not writable. Check its permissions and try again.",
        _ => $"Could not {action}. Try again or open Downloads."
    };

    private sealed record ReadyReceipt(string PackageId, string Channel, string Version, string FileName, long Size, string SHA256);

    private sealed class CheckedSource(IUpdateSource source) : IUpdateSource
    {
        public bool HasFullRelease { get; private set; }

        public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
            Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
        {
            HasFullRelease = false;
            var feed = await source.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease).ConfigureAwait(false);
            var assets = feed.Assets.Where(asset => asset.PackageId == PackageId && asset.Type == VelopackAssetType.Full &&
                asset.Version != null && !asset.Version.IsPrerelease).ToArray();
            foreach (var asset in assets) ValidateAsset(asset);
            HasFullRelease = assets.Length > 0;
            return new VelopackAssetFeed { Assets = assets };
        }

        public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile,
            Action<int> progress, CancellationToken cancelToken = default) =>
            source.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken);
    }
}
