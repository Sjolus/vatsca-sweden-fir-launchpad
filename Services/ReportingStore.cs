using System.IO;
using System.Text.Json;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

internal sealed record ReportingDocument(int SchemaVersion, Guid SupportId, Guid Generation,
    ReportingPreferences Preferences, List<ReportingEvent> Pending,
    DateTime? UsageNotBefore = null, DateTime? ErrorsNotBefore = null, int UsageAttempts = 0, int ErrorAttempts = 0, long Revision = 1,
    DateTime? InventoryNotBefore = null, int InventoryAttempts = 0, List<ReportingDelivery>? LastSent = null);

// One atomically replaced document keeps identity, permissions, queue and receipts consistent.
// A cross-process lease is deliberately non-blocking, including in fatal-error paths.
internal sealed class ReportingStore(string directory)
{
    internal const int MaximumBytes = 1024 * 1024;
    internal string DirectoryPath { get; } = Path.GetFullPath(directory);
    internal string StatePath => Path.Combine(DirectoryPath, "reporting.json");
    private readonly object _gate = new();
    private bool _closed;

    internal bool Initialize(out ReportingDocument? document) => Access(create: true, null, out document);
    internal bool Read(out ReportingDocument? document) => Access(create: false, null, out document);
    internal bool Change(Func<ReportingDocument, ReportingDocument> change, out ReportingDocument? document) => Access(false, change, out document);

    // Shutdown/reset waits only for the current bounded local write, never HTTP.
    // After this barrier a delayed HTTP continuation cannot recreate removed data.
    internal void Close() { lock (_gate) _closed = true; }

    private bool Access(bool create, Func<ReportingDocument, ReportingDocument>? change, out ReportingDocument? document)
    {
        document = null;
        if (!Monitor.TryEnter(_gate)) return false;
        string? temporary = null;
        try
        {
            if (_closed) return false;
            EnsureSafeDirectory();
            if (create) Directory.CreateDirectory(DirectoryPath);
            if (!Directory.Exists(DirectoryPath)) return false;
            using var lease = new FileStream(Path.Combine(DirectoryPath, "reporting.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            ReportingDocument current;
            if (!File.Exists(StatePath))
            {
                if (!create) return false;
                current = new(1, Guid.NewGuid(), Guid.NewGuid(), new(), []);
            }
            else
            {
                if ((File.GetAttributes(StatePath) & FileAttributes.ReparsePoint) != 0) return false;
                using var input = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (input.Length > MaximumBytes) return false;
                using var stored = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 16 });
                current = stored.RootElement.Deserialize<ReportingDocument>(ReportingWire.Json)!;
                if (!Valid(current) || !ReportingHistoryValidation.ValidStoredReceipts(stored.RootElement, current.LastSent)) return false;
                // Older documents did not have a separate inventory choice. Its
                // missing value is false, so legacy version metadata cannot replay.
                // A possibly acknowledged event must never change under its old ID.
                if (!current.Preferences.Inventory)
                    current = current with { Pending = current.Pending.Where(entry => !ReportingWire.HasVersions(entry)).ToList() };
                if (!current.Preferences.DetailedPlugins)
                    current = current with { Pending = current.Pending.Where(entry => entry.DllInventory is null).ToList() };
            }
            var next = change is null ? current : FitQueue(change(current) with { Revision = checked(current.Revision + 1) });
            if (!Valid(next)) return false;
            if (change is not null || !File.Exists(StatePath))
            {
                var bytes = ReportingWire.Bytes(next);
                if (bytes.Length > MaximumBytes) return false;
                // A fixed lease-protected staging name cannot accumulate after repeated crashes.
                string staging = Path.Combine(DirectoryPath, "reporting.pending.tmp");
                if (File.Exists(staging))
                {
                    if ((File.GetAttributes(staging) & FileAttributes.ReparsePoint) != 0) return false;
                    File.Delete(staging);
                }
                temporary = staging;
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { output.Write(bytes); output.Flush(flushToDisk: true); }
                File.Move(temporary, StatePath, overwrite: true);
                temporary = null;
            }
            document = next;
            return true;
        }
        catch (Exception error) when (IsStorageError(error)) { return false; }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (Exception error) when (IsStorageError(error)) { }
            Monitor.Exit(_gate);
        }
    }

    private static bool Valid(ReportingDocument? document) => document is { SchemaVersion: 1, Preferences: not null, Pending: not null } &&
        ReportingWire.IsUuid(document.SupportId) && ReportingWire.IsUuid(document.Generation) &&
        document.Revision is > 0 and < long.MaxValue &&
        (document.Preferences.Confirmed || (!document.Preferences.Usage && !document.Preferences.Errors && !document.Preferences.Inventory && !document.Preferences.DetailedPlugins)) &&
        (!document.Preferences.DetailedPlugins || document.Preferences.Inventory) &&
        document.Pending.Count <= 4096 && document.UsageAttempts is >= 0 and <= 20 && document.ErrorAttempts is >= 0 and <= 20 && document.InventoryAttempts is >= 0 and <= 20 &&
        (document.UsageNotBefore is null || document.UsageNotBefore.Value.Kind == DateTimeKind.Utc) &&
        (document.ErrorsNotBefore is null || document.ErrorsNotBefore.Value.Kind == DateTimeKind.Utc) &&
        (document.InventoryNotBefore is null || document.InventoryNotBefore.Value.Kind == DateTimeKind.Utc) &&
        ReportingHistoryValidation.Valid(document.LastSent, document.SupportId) &&
        document.Pending.All(entry => entry is not null && entry.SupportId == document.SupportId &&
            ReportingWire.Allowed(document.Preferences, entry.EventName) &&
            ReportingWire.ValidEvent(entry, DateTime.UtcNow, checkAge: false)) &&
        document.Pending.Select(entry => entry.EventId).Distinct().Count() == document.Pending.Count;

    // A newly acknowledged payload must fit even when the queue was already full.
    // Keep the latest receipts and remove the oldest queued non-errors first.
    internal static ReportingDocument FitQueue(ReportingDocument document)
    {
        if (document.Pending is null || document.Pending.Any(entry => entry is null) || Fits(document) || document.Pending.Count == 0) return document;
        var pending = document.Pending;
        var order = Enumerable.Range(0, pending.Count).OrderBy(index => ReportingWire.IsError(pending[index].EventName))
            .ThenBy(index => pending[index].OccurredAt).ThenBy(index => index).ToArray();
        ReportingDocument Remove(int count)
        {
            var remove = order.Take(count).ToHashSet();
            return document with { Pending = pending.Where((_, index) => !remove.Contains(index)).ToList() };
        }
        int first = 1, last = pending.Count;
        while (first < last)
        {
            int middle = first + (last - first) / 2;
            if (Fits(Remove(middle))) last = middle; else first = middle + 1;
        }
        return Remove(first);
    }

    private static bool Fits(ReportingDocument document) => document.Pending.Count <= 4096 && ReportingWire.Bytes(document).Length <= MaximumBytes;

    private void EnsureSafeDirectory()
    {
        for (var folder = new DirectoryInfo(DirectoryPath); folder is not null; folder = folder.Parent)
            if (folder.Exists && (folder.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Reporting storage cannot use a linked directory.");
        string leasePath = Path.Combine(DirectoryPath, "reporting.lock");
        if (File.Exists(leasePath) && (File.GetAttributes(leasePath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Reporting storage cannot use a linked lease.");
    }

    internal static bool IsStorageError(Exception error) => error is IOException or UnauthorizedAccessException or
        System.Security.SecurityException or JsonException or ArgumentException or NotSupportedException or InvalidOperationException;
}
