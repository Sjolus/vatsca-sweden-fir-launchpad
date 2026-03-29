using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

public static class UpdateChecker
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders = { { "User-Agent", "VatscaUpdateChecker/1.0" } }
    };

    // -------------------------------------------------------------------------
    // EuroScope application
    // -------------------------------------------------------------------------
    public static Task CheckEuroscope(CheckResult result, string exePath)
    {
        result.InstalledVersion = "—";
        result.LatestVersion = "—";
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

            const string supportedVer = "3.2.3.2";

            var fileInfo = FileVersionInfo.GetVersionInfo(exePath);
            var localVer = fileInfo.ProductVersion ?? fileInfo.FileVersion ?? "0.0.0";
            result.InstalledVersion = $"v{localVer}";
            result.LatestVersion    = $"v{supportedVer}";
            result.DownloadUrl      = "https://www.euroscope.hu/wp/category/public-release/";

            if (!Version.TryParse(localVer, out var local) || !Version.TryParse(supportedVer, out var supported))
            {
                result.Status = CheckStatus.Error;
                return Task.CompletedTask;
            }

            result.Status = local < supported ? CheckStatus.UpdateAvailable
                          : local > supported ? CheckStatus.Unsupported
                          :                     CheckStatus.UpToDate;

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
    public static async Task CheckGng(CheckResult result, string euroscopeDataPath)
    {
        result.InstalledVersion = "—";
        result.LatestVersion = "—";
        result.StatusMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(euroscopeDataPath))
        {
            result.Status = CheckStatus.NotConfigured;
            return;
        }

        try
        {
            if (!Directory.Exists(euroscopeDataPath))
            {
                result.InstalledVersion = "Folder not found";
                result.Status = CheckStatus.Error;
                return;
            }

            // Find the most recent ESAA-Sweden_*.sct file (filename contains timestamp so alpha sort = time sort)
            var sctFiles = Directory.GetFiles(euroscopeDataPath, "ESAA-Sweden_*.sct");
            if (sctFiles.Length == 0)
            {
                result.InstalledVersion = "No SCT file found";
                result.Status = CheckStatus.Error;
                return;
            }

            Array.Sort(sctFiles);
            var sctName = Path.GetFileNameWithoutExtension(sctFiles[^1]);

            // Filename: ESAA-Sweden_20260320164747-260301-0001.sct
            //   Group 1 = "260301"  (AIRAC cycle + package, e.g. 2603/01)
            //   Group 2 = "0001"    (revision within package)
            var match = Regex.Match(sctName, @"-(\d{6})-(\d{4})$");
            if (!match.Success)
            {
                result.InstalledVersion = "Cannot parse filename";
                result.Status = CheckStatus.Error;
                return;
            }

            var localAirac = match.Groups[1].Value;          // "260301"
            int localRev   = int.Parse(match.Groups[2].Value); // 1

            result.InstalledVersion = FormatGngVersion(localAirac, localRev);

            // Scrape aero-nav.com — table is visible without login
            var html = await Http.GetStringAsync("https://files.aero-nav.com/ESAA").ConfigureAwait(false);

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
        catch (Exception ex)
        {
            result.StatusMessage = ex.Message;
            result.Status = CheckStatus.Error;
            Logger.Log("CHECK", $"GNG Pack: error — {ex.Message}");
        }
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
    public static async Task CheckGitHub(CheckResult result, string exePath, string githubRepo, bool skipPreRelease = false)
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
            var tagName   = await FetchLatestTag(githubRepo, skipPreRelease).ConfigureAwait(false);
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
                var json = await Http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest")
                                      .ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("tag_name").GetString();
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Fall through to listing
            }
        }

        try
        {
            var json = await Http.GetStringAsync($"https://api.github.com/repos/{repo}/releases?per_page=20")
                                  .ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
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
        catch (Exception ex) { Logger.Log("CHECK", $"FetchLatestTag ({repo}): error — {ex.Message}"); }

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
