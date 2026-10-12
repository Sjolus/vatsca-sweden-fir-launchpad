using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

public sealed record ActivityHistorySnapshot(IReadOnlyList<ActivityEntry> Entries, bool Unavailable = false);

public static class ActivityHistoryService
{
    private static readonly ActivityHistoryStore Store = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VatscaUpdateChecker", "Activity", "history.json"));
    public static event Action? Changed;
    public static bool LastWriteFailed { get; private set; }
    public static ActivityHistorySnapshot Read() => Store.Read();
    public static void ResetCompleted()
    {
        LastWriteFailed = false;
        NotifyChanged();
    }

    // Call only after an explicitly requested operation. Checks and previews are deliberately absent.
    public static void Record(ActivityApplication app, ActivityOperation operation, ActivityOutcome outcome,
        string? version = null, string? recoveryFolder = null, string? dataFolder = null, bool restartRequired = false, string? previousVersion = null)
    {
        LastWriteFailed = !Store.Append(new(Guid.NewGuid(), DateTimeOffset.UtcNow, app, operation, outcome,
            ActivityHistoryStore.ValidVersion(version) ? version : null,
            ActivityHistoryStore.IsLocalPath(recoveryFolder) ? recoveryFolder : null,
            ActivityHistoryStore.IsLocalPath(dataFolder) ? dataFolder : null, restartRequired,
            ActivityHistoryStore.ValidVersion(previousVersion) ? previousVersion : null));
        NotifyChanged();
    }

    private static void NotifyChanged()
    {
        if (Changed is { } observers)
            foreach (Action observer in observers.GetInvocationList())
                try { observer(); } catch { /* Recording must never change an installation's result. */ }
    }

    public static ActivityApplication ForSoftware(SoftwareApp app) => app switch
    {
        SoftwareApp.Vacs => ActivityApplication.Vacs, SoftwareApp.Vatis => ActivityApplication.Vatis,
        SoftwareApp.TrackAudio => ActivityApplication.TrackAudio, _ => ActivityApplication.VatEfs
    };

    public static bool CanOpenRecoveryFolder(string? path)
    {
        if (!ActivityHistoryStore.IsLocalPath(path)) return false;
        try
        {
            for (var folder = new DirectoryInfo(path!); folder is not null; folder = folder.Parent)
                if (!folder.Exists || (folder.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        { return false; }
    }

    public static bool SameDataFolder(string? stored, string? configured) =>
        ActivityHistoryStore.IsLocalPath(stored) && ActivityHistoryStore.IsLocalPath(configured) &&
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(stored!)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured!)), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Bounded, atomic local storage; injectable path keeps tests away from real AppData.</summary>
public sealed class ActivityHistoryStore(string path)
{
    public const int MaximumEntries = 100;
    private const int MaximumBytes = 512 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, MaxDepth = 8 };
    private readonly object _gate = new();
    internal string? LastFailure { get; private set; }
    private sealed record Document(int Schema, List<ActivityEntry> Entries);

    public ActivityHistorySnapshot Read()
    {
        lock (_gate)
        {
            try { return ReadCore(); }
            catch (Exception ex) when (IsStorageError(ex)) { return new([], true); }
        }
    }

    public bool Append(ActivityEntry entry)
    {
        lock (_gate)
        {
            string? temporary = null;
            LastFailure = null;
            string stage = "validation";
            try
            {
                if (!Valid(entry)) return false;
                var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
                stage = "directory";
                Directory.CreateDirectory(directory);
                // A second Launchpad cannot overwrite a concurrently appended outcome.
                stage = "lease";
                using var lease = new FileStream(Path.Combine(directory, "history.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                stage = "read";
                var existing = ReadCore();
                if (existing.Unavailable) return false; // Keep unreadable history intact for recovery.
                var entries = existing.Entries.Where(e => e.Id != entry.Id).Append(entry)
                    .OrderByDescending(e => e.Time).Take(MaximumEntries).ToList();
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, entries), JsonOptions);
                if (bytes.Length > MaximumBytes) return false;
                temporary = Path.Combine(directory, "history-" + Guid.NewGuid().ToString("N") + ".tmp");
                stage = "temporary";
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes); stream.Flush(flushToDisk: true); }
                // Antivirus/indexing can briefly retain the replaced file on Windows.
                // Retry this atomic rename only; never replay a client operation.
                stage = "replace";
                for (int attempt = 0; ; attempt++)
                {
                    try { File.Move(temporary, path, overwrite: true); break; }
                    catch (Exception ex) when (attempt < 3 && ex is IOException or UnauthorizedAccessException) { Thread.Sleep(20); }
                }
                temporary = null;
                return true;
            }
            catch (Exception ex) when (IsStorageError(ex)) { LastFailure = stage + ":" + ex.GetType().Name + ":" + (ex.HResult & 0xFFFF); return false; }
            finally
            {
                if (temporary is not null)
                    try { File.Delete(temporary); } catch (Exception ex) when (IsStorageError(ex)) { }
            }
        }
    }

    private ActivityHistorySnapshot ReadCore()
    {
        FileStream input;
        try { input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return new([]); }
        using var stream = input;
        if (stream.Length > MaximumBytes) return new([], true);
        var document = JsonSerializer.Deserialize<Document>(stream, JsonOptions);
        if (document is not { Schema: 1, Entries: not null } || document.Entries.Count > MaximumEntries ||
            document.Entries.Any(e => e is null || !Valid(e)) || document.Entries.Select(e => e.Id).Distinct().Count() != document.Entries.Count)
            return new([], true);
        return new(document.Entries.OrderByDescending(e => e.Time).ToArray());
    }

    private static bool Valid(ActivityEntry entry) => entry.Id != Guid.Empty &&
        entry.Time >= DateTimeOffset.UnixEpoch && Enum.IsDefined(entry.Application) && Enum.IsDefined(entry.Operation) && Enum.IsDefined(entry.Outcome) &&
        ValidVersion(entry.Version) && ValidVersion(entry.PreviousVersion) &&
        (entry.RecoveryFolder is null || IsLocalPath(entry.RecoveryFolder)) &&
        (entry.DataFolder is null || IsLocalPath(entry.DataFolder));

    internal static bool ValidVersion(string? version) => version is null ||
        version.Length <= 64 && Regex.IsMatch(version, @"\A[vV]?\d[0-9A-Za-z./ -]*\z");

    internal static bool IsLocalPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl)) return false;
        try
        {
            return Path.IsPathFullyQualified(value) && !value.StartsWith(@"\\", StringComparison.Ordinal) &&
                value.IndexOf(':', 2) < 0 && value.IndexOfAny(['"', '<', '>', '|', '*', '?']) < 0 &&
                !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)),
                    Path.TrimEndingDirectorySeparator(Path.GetPathRoot(value)!), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return false; }
    }

    private static bool IsStorageError(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or
        ArgumentException or NotSupportedException or System.Security.SecurityException;
}
