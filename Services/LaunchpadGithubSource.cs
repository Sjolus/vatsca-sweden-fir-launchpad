using System.Net.Http;
using System.IO;
using System.Text.Json;
using Velopack;
using Velopack.Sources;

namespace VatscaUpdateChecker.Services;

/// <summary>Keeps Velopack's package pipeline, with bounded cached public catalog checks.</summary>
internal sealed class LaunchpadGithubSource : GithubSource
{
    private const string CatalogUrl = "https://api.github.com/repos/Sjolus/vatsca-sweden-fir-launchpad/releases?per_page=10&page=1";
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false });
    private static readonly VendorMetadataReader SharedReader = new(Client, validate: ValidCatalog);
    private readonly VendorMetadataReader _reader;

    internal LaunchpadGithubSource() : this(SharedReader) { }
    internal LaunchpadGithubSource(VendorMetadataReader reader, IFileDownloader? downloader = null)
        : base(LaunchpadUpdateService.RepositoryUrl, accessToken: null, prerelease: false, downloader: downloader) => _reader = reader;

    protected override async Task<GithubRelease[]> GetReleases(bool includePrereleases)
    {
        string json = await _reader.GetAsync(new(CatalogUrl), VendorService.GitHub).ConfigureAwait(false);
        if (!ValidCatalog(new(CatalogUrl), json)) throw new InvalidDataException("The Launchpad release catalog could not be read.");
        var releases = JsonSerializer.Deserialize<GithubRelease[]>(json) ?? [];
        using var document = JsonDocument.Parse(json);
        var published = document.RootElement.EnumerateArray().Select(release => !release.GetProperty("draft").GetBoolean()).ToArray();
        // Match the pinned SDK's ordering; its downstream feed validation still requires stable full packages.
        return releases.Where((_, index) => published[index]).OrderByDescending(release => release.PublishedAt)
            .Where(release => !release.Prerelease).ToArray();
    }

    private static bool ValidCatalog(Uri uri, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return false;
            foreach (var release in document.RootElement.EnumerateArray())
                if (release.ValueKind != JsonValueKind.Object ||
                    !release.TryGetProperty("draft", out var draft) || draft.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    !release.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    !release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return false;
            _ = JsonSerializer.Deserialize<GithubRelease[]>(json);
            return true;
        }
        catch (JsonException) { return false; }
    }
}
