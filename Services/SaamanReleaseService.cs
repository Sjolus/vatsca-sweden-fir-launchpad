using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

/// <summary>Published stable SAAMAN releases and bounded, checksum-verified downloads. Never loads a DLL.</summary>
public sealed class SaamanReleaseService
{
    public const string ReleasesUrl = "https://github.com/Kaskobiii/SAAMAN/releases";
    public const long MaximumDllSize = 64L * 1024 * 1024;
    private static readonly string[] RequiredAssets = ["SAAMAN.dll", "LICENSE.txt", "THIRD-PARTY-NOTICES.txt", "SHA256SUMS.txt"];
    private static readonly HttpClient SharedClient = new(new HttpClientHandler { AllowAutoRedirect = false });
    private static readonly ConditionalWeakTable<HttpClient, CatalogCache> Caches = new();
    private static readonly TimeSpan CatalogLifetime = TimeSpan.FromMinutes(10);
    private readonly HttpClient _client;
    private readonly CatalogCache _cache;
    private readonly Func<DateTimeOffset> _utcNow;
    public SaamanReleaseService() : this(SharedClient) { }
    public SaamanReleaseService(HttpClient client) : this(client, () => DateTimeOffset.UtcNow) { }
    internal SaamanReleaseService(HttpClient client, Func<DateTimeOffset> utcNow)
    {
        _client = client;
        _cache = Caches.GetValue(client, _ => new CatalogCache());
        _utcNow = utcNow;
    }

    public async Task<SaamanRelease?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<SaamanRelease?> pending;
        TaskCompletionSource<SaamanRelease?>? start = null;
        lock (_cache)
        {
            DateTimeOffset now = _utcNow();
            if (_cache.HasValue && now < _cache.ExpiresAt) return _cache.Release;
            if (_cache.RetryAt is { } retryAt && now < retryAt)
                throw RateLimited(retryAt);
            if (_cache.InFlight is null)
            {
                start = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _cache.InFlight = start.Task;
            }
            pending = _cache.InFlight;
        }
        if (start is not null) _ = PopulateCacheAsync(start);
        // Cancelling one dialog must not cancel a check shared with another caller.
        return await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PopulateCacheAsync(TaskCompletionSource<SaamanRelease?> completion)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var release = await FetchLatestAsync(timeout.Token).ConfigureAwait(false);
            lock (_cache)
            {
                _cache.Release = release;
                _cache.HasValue = true;
                _cache.ExpiresAt = _utcNow() + CatalogLifetime;
                _cache.RetryAt = null;
                _cache.InFlight = null;
                completion.TrySetResult(release);
            }
        }
        catch (Exception ex)
        {
            var failure = ex is OperationCanceledException
                ? new SaamanReleaseUnavailableException("The SAAMAN release check timed out. Your selected DLL and profile settings are unchanged.", failureKind: VendorFailureKind.Timeout)
                : ex;
            lock (_cache)
            {
                if (ex is SaamanReleaseUnavailableException { RetryAtUtc: { } retryAt }) _cache.RetryAt = retryAt;
                _cache.InFlight = null;
                completion.TrySetException(failure);
            }
            // A caller may already have cancelled its wait; observe the shared failure as well.
            _ = completion.Task.Exception;
        }
    }

    private async Task<SaamanRelease?> FetchLatestAsync(CancellationToken cancellationToken)
    {
        JsonElement? selected = null;
        SoftwareVersion? highest = null;
        for (int page = 1; page <= 5; page++)
        {
            using var response = await SendAsync(new Uri($"https://api.github.com/repos/Kaskobiii/SAAMAN/releases?per_page=100&page={page}"), false, cancellationToken).ConfigureAwait(false);
            using var body = await ReadBoundedAsync(response, 8 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw Invalid("Invalid release catalog.");
            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (Boolean(release, "draft") || Boolean(release, "prerelease")) continue;
                string tag = Text(release, "tag_name");
                if (!SoftwareVersion.TryParse(tag.StartsWith('v') ? tag[1..] : tag, out var version) || version.IsPrerelease) continue;
                if (!DateTimeOffset.TryParse(Text(release, "published_at"), CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    throw Invalid("A stable release has no valid publication date.");
                int order = highest is null ? 1 : version.CompareTo(highest);
                if (order == 0) throw Invalid("The release catalog contains duplicate stable versions.");
                if (order > 0) { selected = release.Clone(); highest = version; }
            }
            if (document.RootElement.GetArrayLength() < 100) break;
            if (page == 5) throw Invalid("The release catalog exceeds the safe inspection limit.");
        }
        if (selected is null || highest is null) return null;
        return ParseRelease(selected.Value, highest.Value);
    }

    private static SaamanRelease ParseRelease(JsonElement selected, string version)
    {
        string tag = Text(selected, "tag_name");
        if (!selected.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw Invalid("The latest stable release has no assets.");
        var verified = new List<SaamanAsset>();
        foreach (string name in RequiredAssets)
        {
            var matches = assets.EnumerateArray().Where(asset => Text(asset, "name") == name).ToArray();
            if (matches.Length != 1) throw Invalid($"The latest stable release must contain exactly one {name}.");
            var asset = matches[0];
            string digest = Text(asset, "digest");
            if (Text(asset, "state") != "uploaded" || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
                !IsHash(digest[7..]) || !asset.TryGetProperty("size", out var size) || !size.TryGetInt64(out long length) ||
                length <= 0 || length > (name == "SAAMAN.dll" ? MaximumDllSize : 1024 * 1024))
                throw Invalid($"The latest stable release has no complete size and SHA-256 metadata for {name}.");
            if (!Uri.TryCreate(Text(asset, "browser_download_url"), UriKind.Absolute, out var uri))
                throw Invalid("Invalid release asset URL.");
            string expected = $"/Kaskobiii/SAAMAN/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}";
            if (!IsHttps(uri) || uri.Host != "github.com" || uri.AbsolutePath != expected || uri.Query.Length != 0)
                throw Invalid("A release asset is outside the official SAAMAN repository.");
            verified.Add(new(name, uri, length, digest[7..].ToLowerInvariant()));
        }
        return new(version, tag, verified.AsReadOnly());
    }

    internal async Task DownloadAsync(SaamanAsset asset, string destination, IProgress<SaamanProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Validate again at the network boundary; a caller cannot turn this into an arbitrary URL download.
        if (!RequiredAssets.Contains(asset.Name) || !IsHash(asset.Sha256) || asset.Size <= 0 ||
            asset.Size > (asset.Name == "SAAMAN.dll" ? MaximumDllSize : 1024 * 1024) ||
            !IsHttps(asset.DownloadUri) || asset.DownloadUri.Host != "github.com" ||
            !asset.DownloadUri.AbsolutePath.StartsWith("/Kaskobiii/SAAMAN/releases/download/", StringComparison.Ordinal) ||
            !asset.DownloadUri.AbsolutePath.EndsWith("/" + asset.Name, StringComparison.Ordinal) || asset.DownloadUri.Query.Length != 0)
            throw Invalid("Invalid SAAMAN download metadata.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var response = await SendAsync(asset.DownloadUri, true, timeout.Token).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is { } advertised && advertised != asset.Size)
            throw Invalid("The download length differs from the reviewed release.");
        using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
        {
            total += read;
            if (total > asset.Size) throw Invalid("The download exceeds its reviewed length.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
            progress?.Report(new($"Downloading {asset.Name} ({total:N0} / {asset.Size:N0} bytes)", total * 100d / asset.Size));
        }
        if (total != asset.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw Invalid("The download does not match its reviewed SHA-256 and size.");
        await output.FlushAsync(timeout.Token).ConfigureAwait(false);
        output.Flush(true);
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, bool assets, CancellationToken cancellationToken)
    {
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("VATSCA-Launchpad/2.5");
            request.Headers.Accept.ParseAdd(assets ? "application/octet-stream" : "application/vnd.github+json");
            HttpResponseMessage response;
            try { response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
            catch (HttpRequestException)
            { throw new SaamanReleaseUnavailableException("GitHub could not be reached. Try again later. Your selected DLL and profile settings are unchanged."); }
            // Injected clients must also disable automatic redirects.
            if (response.RequestMessage?.RequestUri is { } actual && actual != uri)
            { response.Dispose(); throw Invalid("Automatic HTTP redirects are not allowed."); }
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                Uri? location = response.Headers.Location;
                response.Dispose();
                if (!assets || redirects == 5 || location is null) throw Invalid("Unexpected release redirect.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (!IsHttps(uri) || uri.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                    throw Invalid("The release redirected outside GitHub's asset hosts.");
                continue;
            }
            if (response.IsSuccessStatusCode) return response;
            var failure = ResponseFailure(response);
            response.Dispose();
            throw failure;
        }
        throw Invalid("Too many release redirects.");
    }

    private SaamanReleaseUnavailableException ResponseFailure(HttpResponseMessage response)
    {
        var diagnostic = VendorRequestException.FromResponse(response, VendorService.GitHub, _utcNow());
        if (diagnostic.FailureKind != VendorFailureKind.RateLimited)
            return new(diagnostic.FailureKind == VendorFailureKind.Forbidden
                ? "GitHub refused the SAAMAN release request. Try again later or open the release page. Your selected DLL and profile settings are unchanged."
                : "The SAAMAN release service is unavailable. Try again later. Your selected DLL and profile settings are unchanged.",
                failureKind: diagnostic.FailureKind, statusCode: diagnostic.StatusCode);
        return RateLimited(diagnostic.RetryAtUtc!.Value, diagnostic.StatusCode);
    }

    private static SaamanReleaseUnavailableException RateLimited(DateTimeOffset retryAt, HttpStatusCode? statusCode = null) =>
        new($"GitHub is temporarily limiting release checks. Try again after {retryAt.ToLocalTime():HH:mm}. Your selected DLL and profile settings are unchanged.",
            retryAt, VendorFailureKind.RateLimited, statusCode);

    private sealed class CatalogCache
    {
        internal SaamanRelease? Release;
        internal bool HasValue;
        internal DateTimeOffset ExpiresAt;
        internal DateTimeOffset? RetryAt;
        internal Task<SaamanRelease?>? InFlight;
    }

    private static async Task<MemoryStream> ReadBoundedAsync(HttpResponseMessage response, int maximum, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > maximum) throw Invalid("The release catalog is too large.");
        using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var output = new MemoryStream();
        try
        {
            byte[] buffer = new byte[65536]; int read;
            while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            { if (output.Length + read > maximum) throw Invalid("The release catalog is too large."); output.Write(buffer, 0, read); }
            output.Position = 0; return output;
        }
        catch { output.Dispose(); throw; }
    }
    internal static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool IsHttps(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == "https" && uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool Boolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : throw Invalid("Incomplete release publication status.");
    private static InvalidDataException Invalid(string message) => new(message);
}

public sealed class SaamanReleaseUnavailableException(string message, DateTimeOffset? retryAtUtc = null,
    VendorFailureKind failureKind = VendorFailureKind.Unavailable, HttpStatusCode? statusCode = null)
    : VendorRequestException(VendorService.GitHub, retryAtUtc.HasValue ? VendorFailureKind.RateLimited : failureKind,
        statusCode, retryAtUtc, message);
