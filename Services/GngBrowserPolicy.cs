using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace VatscaUpdateChecker.Services;

/// <summary>Website navigation and package-download checks, without reading browser credentials.</summary>
public static class GngBrowserPolicy
{
    public const string AeroNavHome = "https://files.aero-nav.com/ESAA";
    public const long MaximumDownloadBytes = 2L * 1024 * 1024 * 1024;
    private static readonly Regex PackageName = new(
        @"^ESAA-(Full-Package|Update-Only)_([0-9]{14})-[0-9]{6}-[0-9]{4}(?: \([0-9]+\))?\.zip\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Login providers may use different HTTPS hosts; show the actual origin in every window.
    public static bool IsAllowedNavigation(string value) =>
        value.Equals("about:blank", StringComparison.OrdinalIgnoreCase) ||
        (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0);

    // Turnstile uses inline document frames. They inherit the containing page's
    // browser security context and must not be treated as external navigation.
    public static bool IsAllowedFrameNavigation(string value) =>
        value.Equals("about:srcdoc", StringComparison.OrdinalIgnoreCase) || IsAllowedNavigation(value);

    public static string VisibleOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return "Sign-in window";
        return "https://" + uri.IdnHost + (uri.IsDefaultPort ? "" : ":" + uri.Port);
    }

    public static bool TryGetDownloadFilename(string address, string suggestedPath, out string filename)
    {
        filename = string.Empty;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Port != 443 || uri.UserInfo.Length != 0 ||
            !(uri.IdnHost.Equals("aero-nav.com", StringComparison.OrdinalIgnoreCase) ||
              uri.IdnHost.EndsWith(".aero-nav.com", StringComparison.OrdinalIgnoreCase))) return false;
        try
        {
            var candidate = Path.GetFileName(suggestedPath);
            var match = PackageName.Match(candidate);
            if (!match.Success || !DateTime.TryParseExact(match.Groups[2].Value, "yyyyMMddHHmmss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return false;
            filename = candidate;
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    public static bool IsCompletePackage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var match = PackageName.Match(Path.GetFileName(path));
            return match.Success && match.Groups[1].Value.Equals("Full-Package", StringComparison.OrdinalIgnoreCase) &&
                   DateTime.TryParseExact(match.Groups[2].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        }
        catch (ArgumentException) { return false; }
    }
}
