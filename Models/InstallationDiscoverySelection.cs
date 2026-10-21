namespace VatscaUpdateChecker.Models;

public sealed record InstallationDiscoveryOption(InstallationCandidate Candidate, bool IsCurrent)
{
    public string Path => InstallationDiscoverySelection.CandidatePath(Candidate);
}

/// <summary>Discovery choices are changes to a settings draft, not saved application-use preferences.</summary>
public sealed class InstallationDiscoverySelection
{
    private readonly Dictionary<AtcRemovalApp, InstallationDiscoveryOption> _selected = [];
    public IReadOnlyList<InstallationDiscoveryOption> Options { get; }
    public IReadOnlyList<InstallationCandidate> Changes => _selected.Values.Where(o => !o.IsCurrent)
        .Select(o => o.Candidate).OrderBy(c => c.Id).ToArray();

    public InstallationDiscoverySelection(AppSettings current, IReadOnlyList<InstallationCandidate> discovered)
    {
        var options = new List<InstallationDiscoveryOption>();
        foreach (var (id, name) in Applications)
        {
            var configured = ConfiguredPath(current, id).Trim();
            var copies = discovered.Where(c => c.Id == id && !string.IsNullOrWhiteSpace(CandidatePath(c)))
                .DistinctBy(c => Normalize(CandidatePath(c)), StringComparer.OrdinalIgnoreCase).ToArray();
            if (configured.Length > 0)
            {
                var candidate = copies.FirstOrDefault(c => SamePath(CandidatePath(c), configured)) ??
                    new(id, name, id is AtcRemovalApp.Gng or AtcRemovalApp.VatEfs ? "" : configured,
                        id is AtcRemovalApp.Gng or AtcRemovalApp.VatEfs ? configured : null,
                        "Configured", "Current location in Launchpad. Update and removal support is checked separately.", false);
                var existing = new InstallationDiscoveryOption(candidate, true);
                options.Add(existing);
                _selected[id] = existing;
            }
            options.AddRange(copies.Where(c => !SamePath(CandidatePath(c), configured))
                .Select(c => new InstallationDiscoveryOption(c, false)));
        }
        Options = options;
    }

    public bool IsSelected(InstallationDiscoveryOption option) =>
        _selected.TryGetValue(option.Candidate.Id, out var selected) && selected == option;

    public bool HasCurrent(AtcRemovalApp id) => Options.Any(o => o.Candidate.Id == id && o.IsCurrent);

    public void Choose(InstallationDiscoveryOption option)
    {
        if (!Options.Contains(option)) throw new ArgumentException("This location was not part of the search.", nameof(option));
        _selected[option.Candidate.Id] = option;
    }

    public void Undo(InstallationDiscoveryOption option)
    {
        if (!Options.Contains(option)) throw new ArgumentException("This location was not part of the search.", nameof(option));
        if (!IsSelected(option) || option.IsCurrent) return;
        var current = Options.FirstOrDefault(o => o.Candidate.Id == option.Candidate.Id && o.IsCurrent);
        if (current != null) _selected[option.Candidate.Id] = current;
        else _selected.Remove(option.Candidate.Id);
    }

    public AppSettings ApplyTo(AppSettings draft)
    {
        var result = draft.Copy();
        foreach (var candidate in Changes)
        {
            var path = CandidatePath(candidate);
            switch (candidate.Id)
            {
                case AtcRemovalApp.EuroScope: result.EuroscopeExePath = path; break;
                case AtcRemovalApp.Gng: result.EuroscopeDataPath = path; break;
                case AtcRemovalApp.TrackAudio: result.TrackAudioExePath = path; break;
                case AtcRemovalApp.Vacs: result.VacsExePath = path; break;
                case AtcRemovalApp.Vatis: result.VatisExePath = path; break;
                case AtcRemovalApp.VatEfs: result.VatEfsPath = path; break;
            }
        }
        return result;
    }

    public static string CandidatePath(InstallationCandidate candidate) =>
        candidate.Id is AtcRemovalApp.Gng or AtcRemovalApp.VatEfs ? candidate.DataPath ?? "" : candidate.ExecutablePath;

    public static bool RequiresVatEfsProfileRelocation(AppSettings current, AppSettings selected) =>
        current.PatchVatEfs && selected.PatchVatEfs && !SamePath(current.VatEfsPath ?? "", selected.VatEfsPath ?? "");

    public static string ConfiguredPath(AppSettings settings, AtcRemovalApp id) => id switch
    {
        AtcRemovalApp.EuroScope => settings.EuroscopeExePath ?? "",
        AtcRemovalApp.Gng => settings.EuroscopeDataPath ?? "",
        AtcRemovalApp.TrackAudio => settings.TrackAudioExePath ?? "",
        AtcRemovalApp.Vacs => settings.VacsExePath ?? "",
        AtcRemovalApp.Vatis => settings.VatisExePath ?? "",
        AtcRemovalApp.VatEfs => settings.VatEfsPath ?? "",
        _ => ""
    };

    private static bool SamePath(string first, string second) =>
        Normalize(first).Equals(Normalize(second), StringComparison.OrdinalIgnoreCase);
    private static string Normalize(string value) => value.Trim().Replace('/', '\\').TrimEnd('\\');
    private static readonly (AtcRemovalApp Id, string Name)[] Applications =
    [
        (AtcRemovalApp.EuroScope, "EuroScope"), (AtcRemovalApp.Gng, "Swedish GNG data"),
        (AtcRemovalApp.TrackAudio, "TrackAudio"), (AtcRemovalApp.Vacs, "VACS"),
        (AtcRemovalApp.Vatis, "vATIS"), (AtcRemovalApp.VatEfs, "VatEFS")
    ];
}
