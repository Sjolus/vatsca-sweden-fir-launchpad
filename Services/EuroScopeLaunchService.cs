using System.Diagnostics;
using System.IO;

namespace VatscaUpdateChecker.Services;

internal static class EuroScopeLaunchService
{
    // Builds launch instructions only. The caller remains responsible for the explicit launch.
    internal static ProcessStartInfo CreateStartInfo(string executablePath, string? dataFolder,
        string? profilePath, string? defaultDataFolder = null)
    {
        if (!Path.IsPathFullyQualified(executablePath))
            throw new IOException("Choose the full EuroScope executable path in Settings before launching.");

        string? profile = null;
        if (!string.IsNullOrWhiteSpace(profilePath))
        {
            if (!Path.IsPathFullyQualified(profilePath))
                throw new IOException("Choose the EuroScope profile again; its path must be absolute.");
            profile = Path.GetFullPath(profilePath);
        }

        // A selected profile is the best fallback for its relative GNG resources. Without one,
        // use EuroScope's ordinary per-user shortcut destination, never Launchpad's directory.
        var folder = !string.IsNullOrWhiteSpace(dataFolder) ? dataFolder
            : profile != null ? Path.GetDirectoryName(profile)!
            : defaultDataFolder ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EuroScope");
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
            throw new DirectoryNotFoundException(
                "The EuroScope data folder is missing or unavailable. Open App settings → Program files and data folders, " +
                "then choose the EuroScope folder for Swedish GNG data before launching.");
        if (profile != null && !File.Exists(profile))
            throw new FileNotFoundException(
                "The selected EuroScope profile is missing. Choose another profile or ‘No profile’ before launching.");

        var start = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(executablePath),
            WorkingDirectory = Path.GetFullPath(folder),
            UseShellExecute = false
        };
        if (profile != null) start.ArgumentList.Add(profile);
        return start;
    }
}
