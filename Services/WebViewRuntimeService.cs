using Microsoft.Web.WebView2.Core;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

/// <summary>Runtime availability and explicit setup using the shared verified Microsoft prerequisite route.</summary>
public static class WebViewRuntimeService
{
    public static string? GetInstalledVersion() => ReadAvailableVersion(() => CoreWebView2Environment.GetAvailableBrowserVersionString());

    internal static string? ReadAvailableVersion(Func<string> readVersion)
    {
        try
        {
            var version = readVersion();
            return string.IsNullOrWhiteSpace(version) ? null : version;
        }
        catch (WebView2RuntimeNotFoundException) { return null; }
    }

    // VACS and the embedded GNG browser use the same WebView2 prerequisite, not a VACS installer.
    // The caller holds MaintenanceLock and forwards restart requests even after later verification failures.
    public static Task<SoftwarePrerequisiteResult> InstallAsync(IProgress<string>? progress = null,
        CancellationToken cancellationToken = default, Action? onRestartRequired = null) =>
        SoftwarePrerequisiteService.InstallAsync(SoftwareApp.Vacs, progress, cancellationToken, onRestartRequired);
}
