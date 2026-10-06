using System.IO;
using System.Text.Json;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

public static class SettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VatscaUpdateChecker",
        "settings.json");

    public static bool HasSavedSettings => File.Exists(SettingsPath);

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { /* Return defaults on any error */ }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }

    public static bool TrySave(AppSettings settings) => TrySave(settings, Save);

    // A failed write must not replace the caller's current settings with older disk values.
    // Inject the writer for isolated failure/retry tests without touching real settings.
    internal static bool TrySave(AppSettings settings, Action<AppSettings> save)
    {
        try { save(settings); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return false; }
    }
}
