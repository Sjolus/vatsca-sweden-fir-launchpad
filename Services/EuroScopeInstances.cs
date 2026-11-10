using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace VatscaUpdateChecker.Services;

/// <summary>
/// Remembers the exact EuroScope processes Launchpad started. Records are verified by PID, start
/// time and executable path; nothing is adopted by name, window title or timing.
/// </summary>
public sealed class EuroScopeInstanceTracker
{
    private const int MaxRecords = 32;
    private const long MaxBytes = 32 * 1024;
    private readonly string _path;
    private readonly Func<ApplicationProcessIdentity, bool> _isAlive;

    public EuroScopeInstanceTracker(string path, Func<ApplicationProcessIdentity, bool>? isAlive = null)
    {
        _path = path;
        _isAlive = isAlive ?? ApplicationProcessControl.IsAlive;
    }

    public string ReceiptPath => _path;

    public IReadOnlyList<EuroScopeInstanceRecord> Live()
    {
        var stored = Read();
        var live = stored.Where(record => _isAlive(record.Process))
            .OrderBy(record => record.Order).ToList();

        if (live.Count != stored.Count) Write(live);
        return live;
    }

    public void Record(EuroScopeInstanceRecord record)
    {
        var records = Live().Where(existing => existing.EntryId != record.EntryId).ToList();
        records.Add(record);
        Write(records.OrderBy(existing => existing.Order).TakeLast(MaxRecords).ToList());
    }

    public bool TryRecord(Guid entryId, string? profilePath, int order, Process process)
    {
        try
        {
            Record(new(entryId, profilePath, EuroScopeLaunchPlanner.ProfileName(profilePath), order,
                ApplicationProcessControl.Identity(process)));
            return true;
        }
        catch { return false; }
    }

    /// <summary>Running copies of the configured executable that no receipt accounts for.</summary>
    public static IReadOnlyList<ApplicationProcessIdentity> Untracked(IEnumerable<ApplicationProcessIdentity> all,
        IEnumerable<EuroScopeInstanceRecord> tracked)
    {
        var known = tracked.Select(record => record.Process).ToArray();
        return all.Where(process => !known.Any(record => ApplicationProcessControl.SameIdentity(record, process))).ToArray();
    }

    private List<EuroScopeInstanceRecord> Read()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > MaxBytes) return [];
            return JsonSerializer.Deserialize<List<EuroScopeInstanceRecord>>(File.ReadAllText(_path))?
                .Where(record => record?.Process is not null).Take(MaxRecords).ToList() ?? [];
        }
        catch { return []; }
    }

    private void Write(List<EuroScopeInstanceRecord> records)
    {
        try
        {
            if (records.Count == 0) { if (File.Exists(_path)) File.Delete(_path); return; }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(records));
            File.Move(temp, _path, overwrite: true);
        }
        catch { /* Tracking is best effort; Find still reports every running copy. */ }
    }
}
