using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

public enum PublicUpdateCheck { Gng, GitHub }

public static class UpdateChecker
{
    public static event Action<PublicUpdateCheck, Exception>? FailureObserved;

    private static void ObserveFailure(PublicUpdateCheck check, Exception exception)
    {
        if (FailureObserved is { } observers)
            foreach (Action<PublicUpdateCheck, Exception> observer in observers.GetInvocationList())
                try { observer(check, exception); } catch { }
    }
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders = { { "User-Agent", "VatscaUpdateChecker/1.0" } }
    };
    private static readonly VendorMetadataReader Metadata = new(Http, validate: ValidVersionPage);

    internal static bool ValidVersionPage(Uri uri, string body)
    {
        if (uri.Host == "files.aero-nav.com")
            return Regex.IsMatch(body, @"ESAA Update_Only</td><td>([^<]+)</td><td>(\d+)</td>");
        try
        {
            using var document = JsonDocument.Parse(body);
            if (uri.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal))
                return IsPublishedTag(document.RootElement);
            return document.RootElement.ValueKind == JsonValueKind.Array &&
                document.RootElement.EnumerateArray().All(release => release.ValueKind == JsonValueKind.Object &&
                    release.TryGetProperty("draft", out var draft) && draft.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                    release.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                    release.TryGetProperty("tag_name", out var tag) && tag.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(tag.GetString()));
        }
        catch (JsonException) { return false; }
    }

    private static bool IsPublishedTag(JsonElement release) => release.ValueKind == JsonValueKind.Object &&
        release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.False &&
        release.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.False &&
        release.TryGetProperty("tag_name", out var tag) && tag.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(tag.GetString());

    // -------------------------------------------------------------------------
    // EuroScope application
    // -------------------------------------------------------------------------
    public static Task CheckEuroscope(CheckResult result, string exePath)
    {
        result.ObservedLocalVersion = null;
        result.InstalledVersion = "—";
        result.LatestVersion = "v" + EuroScopePolicy.SupportedVersion;
        result.StatusMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(exePath))
        {
            result.Status = CheckStatus.NotConfigured;
            return Task.CompletedTask;
        }

        try
        {
            if (!File.Exists(exePath))
            {
                result.InstalledVersion = "Executable not found";
                result.Status = CheckStatus.Error;
                return Task.CompletedTask;
            }

            const string supportedVer = EuroScopePolicy.SupportedVersion;

            var fileInfo = FileVersionInfo.GetVersionInfo(exePath);
            result.ObservedLocalVersion = fileInfo.ProductVersion ?? fileInfo.FileVersion ?? "";
            var localVer = fileInfo.ProductVersion ?? fileInfo.FileVersion ?? "0.0.0";
            result.InstalledVersion = $"v{localVer}";
            result.LatestVersion    = $"v{supportedVer}";
            result.DownloadUrl      = EuroScopePolicy.DownloadUrl;

            if (!Version.TryParse(localVer, out var local) || !Version.TryParse(supportedVer, out var supported))
            {
                result.Status = CheckStatus.Error;
                return Task.CompletedTask;
            }

            result.Status = local < supported ? CheckStatus.UpdateAvailable
                          : local > supported ? CheckStatus.Unsupported
                          :                     CheckStatus.UpToDate;
            result.StatusMessage = local == supported ? "Supported token-authentication build for the Swedish GNG package." :
                "Sweden supports EuroScope " + supportedVer + ". Use Manage to review a switch; newer releases are not automatically recommended.";

            Logger.Log("CHECK", $"EuroScope: installed={result.InstalledVersion} latest={result.LatestVersion} → {result.Status}");
        }
        catch (Exception ex)
        {
            result.StatusMessage = ex.Message;
            result.Status = CheckStatus.Error;
            Logger.Log("CHECK", $"EuroScope: error — {ex.Message}");
        }

        return Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // EuroScope / GNG Pack
    // -------------------------------------------------------------------------
    public static Task CheckGng(CheckResult result, string euroscopeDataPath) =>
        CheckGng(result, euroscopeDataPath,
            () => Metadata.GetAsync(new("https://files.aero-nav.com/ESAA"), VendorService.AeroNav), GngUpdateService.RequireNoPendingUpdate,
            GngUpdateService.IsTestingInstallation);

    internal static async Task CheckGng(CheckResult result, string euroscopeDataPath,
        Func<Task<string>> fetchPackagePage, Action<string> requireNoPendingUpdate, Func<string, bool>? isTestingInstallation = null)
    {
        ResetGngLocal(result);
        result.LatestVersion = "—";
        try
        {
            if (!ReadGngLocal(result, euroscopeDataPath, requireNoPendingUpdate, isTestingInstallation,
                out var localAirac, out var localRev)) return;

            // Scrape aero-nav.com — table is visible without login
            // This result is UI-bound; its property observers must run on the caller's context.
            var html = await fetchPackagePage();

            // Row: <td>ES</td><td>ESAA Update_Only</td><td>2603 / 01</td><td>1</td>
            var rowMatch = Regex.Match(html, @"ESAA Update_Only</td><td>([^<]+)</td><td>(\d+)</td>");
            if (!rowMatch.Success)
            {
                result.LatestVersion = "Parse error";
                result.Status = CheckStatus.Error;
                return;
            }

            // "2603 / 01" → strip spaces/slash → "260301"
            var remoteAirac = rowMatch.Groups[1].Value.Replace(" ", "").Replace("/", "");
            int remoteRev   = int.Parse(rowMatch.Groups[2].Value);

            result.LatestVersion = FormatGngVersion(remoteAirac, remoteRev);
            result.DownloadUrl   = "https://files.aero-nav.com/ESAA";

            result.Status = IsGngUpToDate(localAirac, localRev, remoteAirac, remoteRev)
                ? CheckStatus.UpToDate
                : CheckStatus.UpdateAvailable;

            Logger.Log("CHECK", $"GNG Pack: installed={result.InstalledVersion} latest={result.LatestVersion} → {result.Status}");
        }
        catch (Exception ex) { GngFailure(result, ex); }
    }

    // Installation/restore completion must not wait for an external version check.
    // Keep the last known public version; without one, local presence is not proof of being up to date.
    public static void RefreshGngLocal(CheckResult result, string euroscopeDataPath) =>
        RefreshGngLocal(result, euroscopeDataPath, GngUpdateService.RequireNoPendingUpdate, GngUpdateService.IsTestingInstallation);

    internal static void RefreshGngLocal(CheckResult result, string euroscopeDataPath,
        Action<string> requireNoPendingUpdate, Func<string, bool>? isTestingInstallation = null)
    {
        ResetGngLocal(result);
        try
        {
            if (!ReadGngLocal(result, euroscopeDataPath, requireNoPendingUpdate, isTestingInstallation,
                out var localAirac, out var localRev)) return;
            var latest = Regex.Match(result.LatestVersion, @"^(\d{4})\s*/\s*(\d{2})\s+rev\.\s*(\d{1,4})$");
            if (latest.Success)
                result.Status = IsGngUpToDate(localAirac, localRev, latest.Groups[1].Value + latest.Groups[2].Value,
                    int.Parse(latest.Groups[3].Value)) ? CheckStatus.UpToDate : CheckStatus.UpdateAvailable;
            else
            {
                result.Status = CheckStatus.Installed;
                result.StatusMessage = "Installed; check for updates.";
            }
        }
        catch (Exception ex) { GngFailure(result, ex); }
    }

    private static void ResetGngLocal(CheckResult result)
    {
        result.ObservedLocalVersion = null;
        result.GngIsTesting = false;
        result.InstalledVersion = "—";
        result.StatusMessage = string.Empty;
    }

    private static bool ReadGngLocal(CheckResult result, string dataFolder, Action<string> requireNoPendingUpdate,
        Func<string, bool>? isTestingInstallation, out string airac, out int revision)
    {
        airac = string.Empty;
        revision = 0;
        if (string.IsNullOrWhiteSpace(dataFolder))
        {
            result.Status = CheckStatus.NotConfigured;
            return false;
        }
        if (!Directory.Exists(dataFolder))
        {
            result.InstalledVersion = "Folder not found";
            result.Status = CheckStatus.Error;
            return false;
        }
        // A new sector filename alone cannot establish a completed installation.
        requireNoPendingUpdate(dataFolder);
        result.GngIsTesting = isTestingInstallation?.Invoke(dataFolder) == true;
        var sectors = Directory.GetFiles(dataFolder, "ESAA-Sweden_*.sct");
        if (sectors.Length == 0)
        {
            result.InstalledVersion = "No SCT file found";
            result.Status = CheckStatus.Error;
            return false;
        }
        // Timestamp order identifies the newest sector generation, including retained old files.
        Array.Sort(sectors);
        result.ObservedLocalVersion = "";
        var match = Regex.Match(Path.GetFileNameWithoutExtension(sectors[^1]), @"-(\d{6})-(\d{4})$");
        if (!match.Success)
        {
            result.InstalledVersion = "Cannot parse filename";
            result.Status = CheckStatus.Error;
            return false;
        }
        airac = match.Groups[1].Value;
        revision = int.Parse(match.Groups[2].Value);
        result.InstalledVersion = FormatGngVersion(airac, revision);
        result.ObservedLocalVersion = result.InstalledVersion;
        return true;
    }

    private static void GngFailure(CheckResult result, Exception exception)
    {
        result.StatusMessage = exception.Message;
        result.Status = CheckStatus.Error;
        Logger.Log("CHECK", $"GNG Pack: error — {exception.Message}");
        ObserveFailure(PublicUpdateCheck.Gng, exception);
    }

    private static string FormatGngVersion(string airac, int rev) =>
        $"{airac[..4]}/{airac[4..]}  rev.{rev}";

    private static bool IsGngUpToDate(string localAirac, int localRev, string remoteAirac, int remoteRev)
    {
        if (!int.TryParse(localAirac, out int l) || !int.TryParse(remoteAirac, out int r))
            return localAirac == remoteAirac && localRev == remoteRev;

        return l > r || (l == r && localRev >= remoteRev);
    }

    // -------------------------------------------------------------------------
    // GitHub-hosted applications
    // -------------------------------------------------------------------------
    public static Task CheckGitHub(CheckResult result, string exePath, string githubRepo, bool skipPreRelease = false) =>
        CheckGitHub(result, exePath, githubRepo, () => FetchLatestTag(githubRepo, skipPreRelease));

    internal static async Task CheckGitHub(CheckResult result, string exePath, string githubRepo,
        Func<Task<string?>> fetchLatestTag)
    {
        result.InstalledVersion = "—";
        result.LatestVersion = "—";
        result.StatusMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(exePath))
        {
            result.Status = CheckStatus.NotConfigured;
            return;
        }

        try
        {
            if (!File.Exists(exePath))
            {
                result.InstalledVersion = "Executable not found";
                result.Status = CheckStatus.Error;
                return;
            }

            var fileInfo   = FileVersionInfo.GetVersionInfo(exePath);
            var rawLocal   = fileInfo.ProductVersion ?? fileInfo.FileVersion ?? "0.0.0";
            var localSemVer = ExtractSemVer(rawLocal);
            result.InstalledVersion = localSemVer is not null ? $"v{localSemVer}" : rawLocal;

            // Try /releases/latest first; some repos only have pre-releases so fall back to /releases
            // Keep both success and failure notifications on the caller's UI context.
            var tagName   = await fetchLatestTag();
            var remoteUrl = $"https://github.com/{githubRepo}/releases/latest";

            if (tagName is null)
            {
                result.LatestVersion = "Cannot fetch";
                result.Status = CheckStatus.Error;
                return;
            }

            var remoteSemVer = ExtractSemVer(tagName);
            result.LatestVersion = remoteSemVer is not null ? $"v{remoteSemVer}" : tagName;
            result.DownloadUrl   = remoteUrl;

            if (localSemVer is null || remoteSemVer is null)
            {
                // Fall back to plain string compare if we can't parse either side
                result.Status = string.Equals(rawLocal.TrimStart('v'), tagName.TrimStart('v'),
                    StringComparison.OrdinalIgnoreCase)
                    ? CheckStatus.UpToDate
                    : CheckStatus.UpdateAvailable;
            }
            else
            {
                result.Status = IsVersionUpToDate(localSemVer, remoteSemVer)
                    ? CheckStatus.UpToDate
                    : CheckStatus.UpdateAvailable;
            }

            Logger.Log("CHECK", $"{result.AppName} ({githubRepo}): installed={result.InstalledVersion} latest={result.LatestVersion} → {result.Status}");
        }
        catch (Exception ex)
        {
            result.StatusMessage = ex.Message;
            result.Status = CheckStatus.Error;
            Logger.Log("CHECK", $"{result.AppName} ({githubRepo}): error — {ex.Message}");
            ObserveFailure(PublicUpdateCheck.GitHub, ex);
        }
    }

    private static async Task<string?> FetchLatestTag(string repo, bool skipPreRelease = false)
    {
        // When filtering pre-releases, skip /releases/latest — the repo author may not have set
        // the prerelease flag (e.g. TrackAudio betas), so GitHub's "latest" can point to a beta.
        if (!skipPreRelease)
        {
            try
            {
                var json = await Metadata.GetAsync(new($"https://api.github.com/repos/{repo}/releases/latest"), VendorService.GitHub)
                                      .ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("tag_name").GetString();
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Fall through to listing
            }
        }

        {
            var json = await Metadata.GetAsync(new($"https://api.github.com/repos/{repo}/releases?per_page=20"), VendorService.GitHub)
                                  .ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
                if (release.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()) continue;
                if (skipPreRelease && release.TryGetProperty("tag_name", out var tag))
                {
                    var tagStr = tag.GetString() ?? string.Empty;
                    // Treat any tag with a pre-release suffix (e.g. "1.4.0-beta.4") as a pre-release,
                    // regardless of the GitHub prerelease flag which repo authors often leave unset.
                    if (Regex.IsMatch(tagStr, @"\d+\.\d+\.\d+-.")) continue;
                    return tagStr;
                }
                if (!skipPreRelease && release.TryGetProperty("tag_name", out var t))
                    return t.GetString();
            }
        }
        return null;
    }

    /// <summary>
    /// Extracts the first "major.minor.patch" sequence from any string.
    /// Handles tags like "1.4.0-beta.4", "v2.0.0", "vacs-client-v2.0.0".
    /// Returns null if no semver pattern is found.
    /// </summary>
    private static string? ExtractSemVer(string input)
    {
        var m = Regex.Match(input, @"(\d+\.\d+\.\d+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static bool IsVersionUpToDate(string local, string remote)
    {
        if (Version.TryParse(local, out var l) && Version.TryParse(remote, out var r))
            return l >= r;
        return string.Equals(local, remote, StringComparison.OrdinalIgnoreCase);
    }

}
