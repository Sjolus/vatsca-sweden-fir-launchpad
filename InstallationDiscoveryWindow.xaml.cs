using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using VatscaUpdateChecker.Services;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

public partial class InstallationDiscoveryWindow : Window
{
    private sealed class Choice(InstallationDiscoveryOption option, InstallationDiscoverySelection selection) : INotifyPropertyChanged
    {
        public InstallationDiscoveryOption Option { get; } = option;
        public InstallationCandidate Candidate => Option.Candidate;
        public string Path => Option.Path;
        public bool CanSelect { get; private set; }
        public string VerificationText { get; private set; } = "Checking this location…";
        public string DetailsLabel { get; private set; } = "Checking location…";
        public Brush VerificationBrush { get; private set; } = Brushes.Gray;
        public string StateLabel => Option.IsCurrent ? (selection.IsSelected(Option) ? "Current location" : "Will be replaced")
            : selection.IsSelected(Option) ? "Selected change" : selection.HasCurrent(Candidate.Id) ? "Other copy found" : "Found on this PC";
        public string ActionText => Option.IsCurrent ? "Keep current" : selection.IsSelected(Option) ? "Undo change"
            : selection.HasCurrent(Candidate.Id) ? "Use this copy instead" : "Use this copy";
        public bool CanChoose => Option.IsCurrent ? !selection.IsSelected(Option) : selection.IsSelected(Option) || CanSelect;
        public string LocationKind => Candidate.Id switch
        {
            AtcRemovalApp.Gng => "EuroScope folder containing Swedish GNG data",
            AtcRemovalApp.VatEfs => "Folder containing the VatEFS plugin",
            _ => "Installed program file"
        };
        public string ScopeLabel => Candidate.Scope switch
        {
            "CurrentUser" => "Installed for your Windows account",
            "AllUsers" or "LocalMachine" => "Installed for all Windows accounts",
            "Configured" => "Previously chosen in Launchpad",
            "Unknown" => "Installation type not confirmed",
            _ => ""
        };
        public string SupportText => Candidate.CanManage
            ? "Updates and removal are checked separately when requested."
            : "Choosing a location does not enable managed updates or removal for an unsupported copy.";
        public ConfiguredPathKind Kind => Candidate.Id switch
        {
            AtcRemovalApp.EuroScope => ConfiguredPathKind.EuroScopeExecutable,
            AtcRemovalApp.Gng => ConfiguredPathKind.EuroScopeDataFolder,
            AtcRemovalApp.TrackAudio => ConfiguredPathKind.TrackAudioExecutable,
            AtcRemovalApp.Vacs => ConfiguredPathKind.VacsExecutable,
            AtcRemovalApp.Vatis => ConfiguredPathKind.VatisExecutable,
            AtcRemovalApp.VatEfs => ConfiguredPathKind.VatEfsFolder,
            _ => throw new InvalidOperationException("This application has no selectable location.")
        };
        public event PropertyChangedEventHandler? PropertyChanged;
        public void RefreshSelection()
        {
            foreach (var name in new[] { nameof(StateLabel), nameof(ActionText), nameof(CanChoose) })
                PropertyChanged?.Invoke(this, new(name));
        }
        public void SetResult(ConfiguredPathValidation result, Brush text, Brush warning)
        {
            CanSelect = result.Status is PathValidationStatus.Verified or PathValidationStatus.Warning;
            VerificationText = result.Message;
            DetailsLabel = result.Status switch
            {
                PathValidationStatus.Invalid => "Location unavailable — details",
                PathValidationStatus.Warning => "Check location — details",
                _ => "Details"
            };
            VerificationBrush = result.Status is PathValidationStatus.Invalid or PathValidationStatus.Warning ? warning : text;
            foreach (var name in new[] { nameof(CanSelect), nameof(CanChoose), nameof(VerificationText), nameof(VerificationBrush), nameof(DetailsLabel) })
                PropertyChanged?.Invoke(this, new(name));
        }
    }

    private readonly Choice[] _choices;
    private readonly AppSettings _current;
    private readonly InstallationDiscoverySelection _selection;
    private readonly ConfiguredPathValidationService _validator = new();
    private bool _checking, _closed;
    public IReadOnlyList<InstallationCandidate> SelectedCandidates { get; private set; } = Array.Empty<InstallationCandidate>();

    public InstallationDiscoveryWindow(IReadOnlyList<InstallationCandidate> candidates, AppSettings? current = null)
    {
        InitializeComponent();
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        _current = current?.Copy() ?? new();
        _selection = new(_current, candidates);
        _choices = _selection.Options.Select(o => new Choice(o, _selection)).ToArray();
        CandidatesList.ItemsSource = _choices;
        Closed += (_, _) => _closed = true;
        if (_choices.Length == 0)
            ResultText.Text = "No installed copies found. Choose a location in Settings, or install a missing application in Setup.";
        else Loaded += async (_, _) =>
        {
            if (await VerifyAsync(_choices)) UpdateSummary();
        };
    }

    private async Task<bool> VerifyAsync(Choice[] choices)
    {
        _checking = true;
        UseButton.IsEnabled = CandidatesList.IsEnabled = false;
        ResultText.Text = "Checking the files and folders…";
        try
        {
            var results = await Task.Run(() => choices.Select(c => _validator.Validate(c.Kind, c.Path)).ToArray());
            if (_closed) return false;
            for (int i = 0; i < choices.Length; i++)
                choices[i].SetResult(results[i], (Brush)FindResource("SecondaryText"), (Brush)FindResource("WarningFg"));
            return true;
        }
        catch
        {
            if (!_closed) ResultText.Text = "The locations could not be checked. Your current choices are kept.";
            return false;
        }
        finally
        {
            _checking = false;
            if (!_closed) CandidatesList.IsEnabled = UseButton.IsEnabled = true;
        }
    }

    private void Choose_Click(object sender, RoutedEventArgs e)
    {
        if (_checking || (sender as FrameworkElement)?.DataContext is not Choice choice || !choice.CanChoose) return;
        if (!choice.Option.IsCurrent && _selection.IsSelected(choice.Option)) _selection.Undo(choice.Option);
        else _selection.Choose(choice.Option);
        foreach (var item in _choices) item.RefreshSelection();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int count = _selection.Changes.Count;
        ResultText.Text = count == 0 ? "Your current locations are kept. Choose a different copy only if you want to change one."
            : $"{count} location{(count == 1 ? "" : "s")} selected for change. Other locations stay as they are.";
        if (InstallationDiscoverySelection.RequiresVatEfsProfileRelocation(_current, _selection.ApplyTo(_current)))
            ResultText.Text += "\nVatEFS is enabled. Its EuroScope profiles will use the selected copy; close EuroScope first.";
        UseButton.Content = count == 0 ? "Done" : "Use selected locations";
    }

    private async void Use_Click(object sender, RoutedEventArgs e)
    {
        if (_checking) return;
        var selected = _choices.Where(c => !c.Option.IsCurrent && _selection.IsSelected(c.Option)).ToArray();
        if (selected.Length > 0)
        {
            if (!await VerifyAsync(selected) || _closed) return;
            if (selected.Any(c => !c.CanSelect))
            {
                ResultText.Text = "A selected location is no longer usable. Undo that change or choose another copy.";
                return;
            }
        }
        SelectedCandidates = _selection.Changes;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
