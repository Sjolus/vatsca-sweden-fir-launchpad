using System.IO;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

/// <summary>Reads the vendors' official catalogs; never downloads or runs an installer.</summary>
public sealed class SoftwareReleaseSource : ISoftwareReleaseSource
{
    public const string VatisFeedUrl = "https://vatis.app/updates/windows/releases.win.json";
    public const long MaximumPackageSize = 1024L * 1024 * 1024;
    private const int MaximumMetadataSize = 8 * 1024 * 1024;
    private const string VatisPackageId = "org.vatsim.vatis";
    private const string VatisDownloadBase = "https://vatis.app/updates/windows/";
    private readonly HttpClient _httpClient;
    private readonly TimeSpan _metadataTimeout;

    public SoftwareReleaseSource(HttpClient httpClient) : this(httpClient, TimeSpan.FromSeconds(45)) { }

    internal SoftwareReleaseSource(HttpClient httpClient, TimeSpan metadataTimeout)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        if (metadataTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(metadataTimeout));
        _httpClient = httpClient;
        _metadataTimeout = metadataTimeout;
    }

    public Task<SoftwareRelease?> GetLatestAsync(SoftwareApp app, string installedVersion,
        CancellationToken cancellationToken) => app switch
        {
            SoftwareApp.Vacs or SoftwareApp.TrackAudio or SoftwareApp.VatEfs => ReadGitHubAsync(app, cancellationToken),
            SoftwareApp.Vatis => ReadVatisAsync(installedVersion, cancellationToken),
            _ => throw new NotSupportedException("This application does not support managed updates.")
        };

    /// <summary>vATIS beta needs an explicit opt-in; VatEFS includes published prereleases.</summary>
    public Task<SoftwareRelease?> GetLatestFreshAsync(SoftwareApp app, bool allowVatisBeta,
        CancellationToken cancellationToken = default) => app switch
        {
            SoftwareApp.Vacs or SoftwareApp.TrackAudio or SoftwareApp.VatEfs => ReadGitHubAsync(app, cancellationToken),
            SoftwareApp.Vatis => ReadVatisCatalogAsync(allowVatisBeta, cancellationToken),
            _ => throw new NotSupportedException("This application does not support managed installation.")
        };

    private async Task<SoftwareRelease?> ReadGitHubAsync(SoftwareApp app, CancellationToken cancellationToken)
    {
        string repository = Repository(app);
        JsonElement? latest = null;
        SoftwareVersion? latestVersion = null;
        // GitHub mixes VACS client/server releases and can mark TrackAudio betas as stable.
        // Inspect tags ourselves; bound pagination rather than reporting a partial catalog as current.
        for (int page = 1; page <= 5; page++)
        {
            using var document = await ReadJsonAsync(new Uri(
                $"https://api.github.com/repos/{repository}/releases?per_page=100&page={page}"), cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw InvalidCatalog("GitHub returned an invalid release catalog.");
            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (IsBoolean(release, "draft", true) || app != SoftwareApp.VatEfs && IsBoolean(release, "prerelease", true)) continue;
                string tag = Text(release, "tag_name");
                string version = app == SoftwareApp.Vacs
                    ? tag.StartsWith("vacs-client-v", StringComparison.Ordinal) ? tag[13..] : ""
                    : app == SoftwareApp.VatEfs && tag.StartsWith('v') ? tag[1..] : tag;
                if (!SoftwareVersion.TryParse(version, out var parsed) || app != SoftwareApp.VatEfs && parsed.IsPrerelease) continue;
                if (!IsBoolean(release, "draft", false) ||
                    !(IsBoolean(release, "prerelease", false) || app == SoftwareApp.VatEfs && IsBoolean(release, "prerelease", true)))
                    throw InvalidCatalog("GitHub did not provide a complete release publication status.");
                if (app == SoftwareApp.VatEfs)
                {
                    string published = Text(release, "published_at");
                    if (string.IsNullOrEmpty(published)) continue;
                    if (!DateTimeOffset.TryParse(published, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                        throw InvalidCatalog("VatEFS published an invalid release date.");
                }
                int order = latestVersion is null ? 1 : parsed.CompareTo(latestVersion);
                if (order == 0) throw InvalidCatalog("The vendor published ambiguous releases for the same version.");
                if (order < 0) continue;
                latest = release.Clone();
                latestVersion = parsed;
            }
            if (document.RootElement.GetArrayLength() < 100) break;
            if (page == 5) throw InvalidCatalog("The release catalog is too large to check safely. Open the vendor downloads page.");
        }
        if (latest is null || latestVersion is null) return null;

        if (app == SoftwareApp.VatEfs &&
            (!System.Version.TryParse(latestVersion.Value, out var msiVersion) || msiVersion.Revision != -1 ||
                msiVersion.Major > 255 || msiVersion.Minor > 255 || msiVersion.Build < 0 || msiVersion.Build > 65535))
            throw InvalidCatalog("The newest VatEFS release does not use a supported numeric MSI version. Open releases to review it manually.");

        var selected = latest.Value;
        string fileName = InstallerFileName(app, latestVersion.Value);
        if (!selected.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw InvalidCatalog("The latest release does not contain a verified Windows installer.");
        var matches = assets.EnumerateArray().Where(asset => Text(asset, "name") == fileName).ToArray();
        if (matches.Length != 1)
            throw InvalidCatalog("The latest release does not contain exactly one supported Windows installer.");
        var asset = matches[0];
        if (Text(asset, "state") != "uploaded")
            throw InvalidCatalog("The vendor has not finished publishing the Windows installer.");
        string digest = Text(asset, "digest");
        if (!digest.StartsWith("sha256:", StringComparison.Ordinal))
            throw InvalidCatalog("The vendor has not supplied a SHA-256 checksum for this installer.");
        return ValidatedRelease(app, latestVersion.Value, Text(asset, "browser_download_url"),
            fileName, digest[7..], Size(asset, "size")) with { IsPrerelease = IsBoolean(selected, "prerelease", true) };
    }

    private async Task<SoftwareRelease?> ReadVatisAsync(string installedVersion, CancellationToken cancellationToken)
    {
        if (!SoftwareVersion.TryParse(installedVersion, out var installed))
            throw InvalidCatalog("The installed vATIS version cannot be compared safely.");
        return await ReadVatisCatalogAsync(installed.PrereleaseLabel == "beta", cancellationToken).ConfigureAwait(false);
    }

    private async Task<SoftwareRelease?> ReadVatisCatalogAsync(bool allowBeta, CancellationToken cancellationToken)
    {
        using var document = await ReadJsonAsync(new Uri(VatisFeedUrl), cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("Assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw InvalidCatalog("vATIS returned an invalid Windows update catalog.");
        JsonElement? latest = null;
        SoftwareVersion? latestVersion = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (Text(asset, "PackageId") != VatisPackageId || Text(asset, "Type") != "Full") continue;
            if (!SoftwareVersion.TryParse(Text(asset, "Version"), out var parsed))
                throw InvalidCatalog("vATIS published an invalid package version.");
            // Receiving a beta in the vendor feed does not opt a stable installation into beta software.
            if (parsed.IsPrerelease && (!allowBeta || parsed.PrereleaseLabel != "beta")) continue;
            int order = latestVersion is null ? 1 : parsed.CompareTo(latestVersion);
            if (order == 0) throw InvalidCatalog("vATIS published ambiguous full packages for the same version.");
            if (order < 0) continue;
            latest = asset;
            latestVersion = parsed;
        }
        if (latest is null || latestVersion is null) return null;
        var selected = latest.Value;
        string fileName = Text(selected, "FileName");
        if (fileName != InstallerFileName(SoftwareApp.Vatis, latestVersion.Value))
            throw InvalidCatalog("vATIS published an unsupported full-package filename.");
        return ValidatedRelease(SoftwareApp.Vatis, latestVersion.Value,
            VatisDownloadBase + fileName, fileName, Text(selected, "SHA256"), Size(selected, "Size")) with { IsPrerelease = latestVersion.IsPrerelease };
    }

    private static SoftwareRelease ValidatedRelease(SoftwareApp app, string version,
        string downloadUrl, string fileName, string sha256, long size)
    {
        if (size <= 0 || size > MaximumPackageSize || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw InvalidCatalog("The vendor's download size or SHA-256 checksum is missing or invalid.");
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri))
            throw InvalidCatalog("The vendor's download address is invalid.");
        var release = new SoftwareRelease(app, version, uri, fileName, sha256.ToUpperInvariant(), size);
        if (!IsAllowedDownloadUri(release, uri))
            throw InvalidCatalog("The download address does not belong to the expected official release.");
        return release;
    }

    /// <summary>Only exact official release paths can initiate a download; GitHub's CDN may serve a redirect.</summary>
    public static bool IsAllowedDownloadUri(SoftwareRelease release, Uri uri, bool allowRedirect = false)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            !SoftwareVersion.TryParse(release.Version, out var version)) return false;
        if (!Enum.IsDefined(release.App) ||
            (release.App != SoftwareApp.Vatis && version.IsPrerelease) ||
            release.FileName != InstallerFileName(release.App, release.Version)) return false;
        if (release.App != SoftwareApp.Vatis && allowRedirect &&
            uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "github-releases.githubusercontent.com")
            return true;
        string expected = release.App == SoftwareApp.Vatis
            ? VatisDownloadBase + release.FileName
            : $"https://github.com/{Repository(release.App)}/releases/download/" +
                (release.App == SoftwareApp.Vacs ? "vacs-client-v" : release.App == SoftwareApp.VatEfs ? "v" : "") + release.Version + "/" + release.FileName;
        return uri.Query.Length == 0 && string.Equals(uri.AbsoluteUri, new Uri(expected).AbsoluteUri, StringComparison.Ordinal);
    }

    private async Task<JsonDocument> ReadJsonAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_metadataTimeout);
        try { return await ReadJsonCoreAsync(uri, timeout.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The vendor's update catalog did not respond in time. Try checking again.");
        }
    }

    private async Task<JsonDocument> ReadJsonCoreAsync(Uri uri, CancellationToken requestToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("SwedenFirLaunchpad/2.0");
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken);
        response.EnsureSuccessStatusCode();
        // Metadata redirects must stay at the exact endpoint. Package redirects are checked separately.
        if (response.RequestMessage?.RequestUri is { } finalUri && finalUri != uri)
            throw InvalidCatalog("The update catalog redirected to an unexpected address.");
        if (response.Content.Headers.ContentLength > MaximumMetadataSize)
            throw InvalidCatalog("The update catalog is too large.");
        using var buffer = new MemoryStream();
        await using var input = await response.Content.ReadAsStreamAsync(requestToken);
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await input.ReadAsync(chunk, requestToken)) != 0)
        {
            if (buffer.Length + read > MaximumMetadataSize) throw InvalidCatalog("The update catalog is too large.");
            buffer.Write(chunk, 0, read);
        }
        buffer.Position = 0;
        try { return await JsonDocument.ParseAsync(buffer, cancellationToken: requestToken); }
        catch (JsonException) { throw InvalidCatalog("The vendor's update catalog could not be read."); }
    }

    private static string Repository(SoftwareApp app) => app switch
    {
        SoftwareApp.Vacs => "vacs-project/vacs",
        SoftwareApp.TrackAudio => "pierr3/TrackAudio",
        SoftwareApp.VatEfs => "minsulander/vatefs",
        _ => throw new NotSupportedException("This application does not use GitHub installer releases.")
    };

    private static string InstallerFileName(SoftwareApp app, string version) => app switch
    {
        SoftwareApp.Vacs => $"vacs_{version}_x64-setup.exe",
        SoftwareApp.TrackAudio => $"trackaudio-{version}-x64-setup.exe",
        SoftwareApp.Vatis => $"{VatisPackageId}-{version}-full.nupkg",
        SoftwareApp.VatEfs => $"vatefs-{version}.msi",
        _ => ""
    };
    private static string Text(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString() ?? "" : "";
    private static bool IsBoolean(JsonElement value, string property, bool expected) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var field) &&
        field.ValueKind == (expected ? JsonValueKind.True : JsonValueKind.False);
    private static long Size(JsonElement value, string property) =>
        value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out long size) ? size : 0;
    private static InvalidDataException InvalidCatalog(string message) => new(message);
}
