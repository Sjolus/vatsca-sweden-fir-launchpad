using System.IO;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

/// <summary>A EuroScope process Launchpad started, bound to the launch-set entry and profile it used.</summary>
public sealed record EuroScopeInstanceRecord(Guid EntryId, string? ProfilePath, string ProfileName, int Order,
    ApplicationProcessIdentity Process);

public enum EuroScopeLaunchStepKind { Start, AlreadyRunning, Skip }

public sealed record EuroScopeLaunchStep(EuroScopeLaunchEntry Entry, string ProfileName, int Order,
    EuroScopeLaunchStepKind Kind, int DelaySeconds, string? Message);

/// <summary>Pure planning for the ordered launch set. It never starts or inspects processes itself.</summary>
public static class EuroScopeLaunchPlanner
{
    public static string ProfileName(string? profilePath) =>
        string.IsNullOrWhiteSpace(profilePath) ? "No profile" : Path.GetFileNameWithoutExtension(profilePath);

    public static IReadOnlyList<EuroScopeLaunchEntry> Normalize(IEnumerable<EuroScopeLaunchEntry>? entries) =>
        (entries ?? []).Where(entry => entry is not null).Take(EuroScopeLaunchEntry.MaxEntries)
            .Select(entry => entry with
            {
                Id = entry.Id == Guid.Empty ? Guid.NewGuid() : entry.Id,
                ProfilePath = entry.ProfilePath ?? string.Empty,
                DelaySeconds = Math.Clamp(entry.DelaySeconds, 0, EuroScopeLaunchEntry.MaxDelaySeconds)
            }).ToArray();

    /// <param name="validate">Returns a skip reason for an entry, or null when it may start.</param>
    public static IReadOnlyList<EuroScopeLaunchStep> Build(IEnumerable<EuroScopeLaunchEntry> entries,
        IReadOnlyCollection<EuroScopeInstanceRecord> running, Func<EuroScopeLaunchEntry, string?> validate)
    {
        var steps = new List<EuroScopeLaunchStep>();
        var runningIds = running.Select(record => record.EntryId).ToHashSet();
        var seenProfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int order = 0;
        bool firstStart = true;

        foreach (var entry in Normalize(entries).Where(entry => entry.Enabled))
        {
            order++;
            var name = ProfileName(entry.ProfilePath);
            bool duplicate = !string.IsNullOrWhiteSpace(entry.ProfilePath) && !seenProfiles.Add(Path.GetFullPath(entry.ProfilePath));

            if (runningIds.Contains(entry.Id))
            {
                steps.Add(new(entry, name, order, EuroScopeLaunchStepKind.AlreadyRunning, 0, "Already running · left open."));
                continue;
            }

            string? blocked;
            try { blocked = validate(entry); }
            catch (Exception error) { blocked = error.Message; }
            if (blocked is not null)
            {
                steps.Add(new(entry, name, order, EuroScopeLaunchStepKind.Skip, 0, "Skipped · " + blocked));
                continue;
            }

            steps.Add(new(entry, name, order, EuroScopeLaunchStepKind.Start, firstStart ? 0 : entry.DelaySeconds,
                duplicate ? "This profile is also used by an earlier instance; both write the same session files." : null));
            firstStart = false;
        }

        return steps;
    }
}
