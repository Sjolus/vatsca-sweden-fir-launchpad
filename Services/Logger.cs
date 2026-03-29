using System.IO;

namespace VatscaUpdateChecker.Services;

/// <summary>
/// Appends timestamped entries to %APPDATA%\VatscaUpdateChecker\launchpad.log.
/// Trims the file to the most recent ~500 lines whenever it exceeds 200 KB.
/// Never throws — a logging failure must never crash the application.
/// </summary>
public static class Logger
{
    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VatscaUpdateChecker",
        "launchpad.log");

    private static readonly object _lock = new();

    public static void Log(string category, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [{category}] {message}";
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, line + Environment.NewLine);
                TrimIfNeeded();
            }
            catch { /* never let logging break the app */ }
        }
    }

    private static void TrimIfNeeded()
    {
        var info = new FileInfo(LogPath);
        if (!info.Exists || info.Length < 200 * 1024) return;

        var lines = File.ReadAllLines(LogPath);
        File.WriteAllLines(LogPath, lines.Skip(lines.Length / 2));
    }
}
