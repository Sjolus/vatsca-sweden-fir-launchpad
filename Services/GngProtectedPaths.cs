using System.IO;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

internal static class GngProtectedPaths
{
    // Profiles are intentionally merged by an update. Cleanup separately protects the
    // selected profile because archiving it would remove the user's launch choice.
    internal static IEnumerable<string> ForUpdate(AppSettings settings)
    {
        yield return settings.EuroscopeExePath;
        // VatEFS may share ESAA/Plugins with package DLLs. Protect its configured DLL,
        // not that entire shared directory, when installing official GNG files.
        if (!string.IsNullOrWhiteSpace(settings.VatEfsPath))
            yield return Path.Combine(settings.VatEfsPath, "VatEFS.dll");
        foreach (var executable in new[] { settings.TrackAudioExePath, settings.VacsExePath, settings.VatisExePath })
        {
            if (!string.IsNullOrWhiteSpace(executable))
            {
                var parent = Path.GetDirectoryName(executable);
                yield return string.IsNullOrWhiteSpace(parent) ? executable : parent;
            }
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(local, "VatscaUpdateChecker");
        yield return Path.Combine(roaming, "VatscaUpdateChecker");
        yield return Path.Combine(local, "SwedenFirLaunchpad");
        yield return AppContext.BaseDirectory;
    }
}
