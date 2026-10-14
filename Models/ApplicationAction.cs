using System.ComponentModel;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker.Models;

public enum ApplicationBatchAction { Start, Close }

public sealed record ApplicationActionTarget(string Id, string Name, string LaunchPath,
    bool IsBrowser = false, bool IsLocalBrowser = false, bool IsEuroScope = false,
    bool IsNotUsed = false, bool IsFolder = false, bool IsLaunchpad = false,
    string? DataFolder = null, string? ProfilePath = null, string? ProfileName = null);

public sealed record ApplicationActionObservation(IReadOnlyList<ApplicationProcessIdentity> Running,
    IReadOnlyList<ApplicationProcessIdentity> Windows, string? StartBlockReason = null);

public sealed class ApplicationActionChoice : INotifyPropertyChanged
{
    public ApplicationActionTarget Target { get; }
    public string ApplicationName => Target.Name;
    public string Detail { get; }
    public bool CanSelect { get; }
    internal IReadOnlyList<ApplicationProcessIdentity> Processes { get; }
    private bool _selected;
    public bool IsSelected
    {
        get => _selected;
        set
        {
            bool selected = CanSelect && value;
            if (_selected == selected) return;
            _selected = selected;
            PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
        }
    }

    public ApplicationActionChoice(ApplicationActionTarget target, string detail, bool canSelect,
        IReadOnlyList<ApplicationProcessIdentity>? processes = null)
    {
        Target = target; Detail = detail; CanSelect = canSelect; _selected = canSelect;
        Processes = processes?.ToArray() ?? [];
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record ApplicationActionResult(string Id, string Name, bool Requested, string Message);
