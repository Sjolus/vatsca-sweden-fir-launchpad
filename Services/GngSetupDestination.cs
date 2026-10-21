using System.IO;

namespace VatscaUpdateChecker.Services;

/// <summary>Prepares only the standard destination after the user requests GNG setup.</summary>
internal static class GngSetupDestination
{
    internal static string PrepareDefault() => PrepareDefault(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    // Tests supply a synthetic existing roaming directory, never the current user's AppData.
    internal static string PrepareDefault(string roamingAppData)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(roamingAppData) || roamingAppData.Length > 2048 || roamingAppData.Any(char.IsControl) ||
                roamingAppData.Replace('/', '\\').Split('\\').Any(part => part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')))
                throw new InvalidDataException("The roaming data folder is not a supported local path.");
            string roaming = SaamanFileSafety.Canonical(roamingAppData);
            if (roaming.Equals(Path.GetPathRoot(roaming), StringComparison.OrdinalIgnoreCase) || !Directory.Exists(roaming))
                throw new InvalidDataException("The roaming data folder must be an existing directory.");

            // Hold the existing ancestry while creating and checking the single child.
            // This refuses links and prevents its parents being replaced during creation.
            using var parents = new SaamanFileSafety.DirectoryGuard(roaming, create: false);
            string destination = Path.Combine(roaming, "EuroScope");
            SaamanFileSafety.NoLinks(destination);
            using var folder = new SaamanFileSafety.DirectoryGuard(destination, create: true);
            return destination;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            throw new InvalidOperationException("The standard EuroScope data folder could not be prepared safely. Choose an existing folder under Setup → Swedish GNG.", ex);
        }
    }
}
