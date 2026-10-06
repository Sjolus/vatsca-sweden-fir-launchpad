using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using VatscaUpdateChecker.Services;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

public partial class InstallationDiscoveryWindow : Window
{
    private sealed class Choice(InstallationCandidate candidate) : INotifyPropertyChanged
    {
        public InstallationCandidate Candidate { get; } = candidate;
        public bool Selected { get; set; }
        public bool CanSelect { get; private set; }
        public string VerificationText { get; private set; } = "Checking the program or folder…";
        public Brush VerificationBrush { get; private set; } = Brushes.Gray;
        public string Path => Candidate.Id is AtcRemovalApp.Gng or AtcRemovalApp.VatEfs
            ? Candidate.DataPath ?? "" : Candidate.ExecutablePath;
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
            "Unknown" => "Installation type not confirmed",
            _ => ""
        };
        public string SupportText => Candidate.CanManage
            ? "Launchpad checks update and removal support separately when you use those actions."
            : "This copy may need manual updates or removal. Choosing it here only tells Launchpad where it is.";
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
        public void SetResult(ConfiguredPathValidation result, Brush text, Brush warning)
        {
            CanSelect = result.Status is PathValidationStatus.Verified or PathValidationStatus.Warning;
            if (!CanSelect) Selected = false;
            VerificationText = result.Message;
            VerificationBrush = result.Status is PathValidationStatus.Invalid or PathValidationStatus.Warning ? warning : text;
            foreach (var name in new[] { nameof(CanSelect), nameof(Selected), nameof(VerificationText), nameof(VerificationBrush) })
                PropertyChanged?.Invoke(this, new(name));
        }
    }

    private readonly Choice[] _choices;
    private readonly ConfiguredPathValidationService _validator = new();
    private bool _checking, _closed;
    public IReadOnlyList<InstallationCandidate> SelectedCandidates { get; private set; } = Array.Empty<InstallationCandidate>();
    public bool ReplaceExisting => ReplaceConfigured.IsChecked == true;

    public InstallationDiscoveryWindow(IReadOnlyList<InstallationCandidate> candidates)
    {
        InitializeComponent();
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        _choices = candidates.Select(c => new Choice(c)).ToArray();
        CandidatesList.ItemsSource = _choices;
        UseButton.IsEnabled = false;
        Closed += (_, _) => _closed = true;
        if (_choices.Length == 0)
            ResultText.Text = "Nothing was found in the usual locations. If you already installed an application, choose its program file in Settings. Otherwise, return to setup to install it.";
        else Loaded += async (_, _) =>
        {
            if (await VerifyAsync(_choices))
                ResultText.Text = "Choose the programs and folders you use. Unrecognised results need your confirmation in Settings; wrong or missing locations cannot be selected.";
        };
    }

    private async Task<bool> VerifyAsync(Choice[] choices)
    {
        _checking = true;
        UseButton.IsEnabled = CandidatesList.IsEnabled = ReplaceConfigured.IsEnabled = false;
        ResultText.Text = "Checking the selected files and folders without starting any applications…";
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
            if (!_closed) ResultText.Text = "The locations could not be checked. Cancel and search again, or choose files and folders in Settings.";
            return false;
        }
        finally
        {
            _checking = false;
            if (!_closed)
            {
                CandidatesList.IsEnabled = ReplaceConfigured.IsEnabled = true;
                UseButton.IsEnabled = _choices.Any(c => c.CanSelect);
            }
        }
    }

    private async void Use_Click(object sender, RoutedEventArgs e)
    {
        if (_checking) return;
        var selected = _choices.Where(c => c.Selected && c.CanSelect).ToArray();
        if (selected.Length == 0) { ResultText.Text = "Tick a program or folder you want to use, or choose Cancel."; return; }
        if (selected.GroupBy(c => c.Candidate.Id).Any(g => g.Count() > 1))
        { ResultText.Text = "Choose only one copy of each program or data folder."; return; }
        if (!await VerifyAsync(selected) || _closed) return;
        if (selected.Any(c => !c.CanSelect))
        {
            ResultText.Text = "A location changed or could not be recognised as a usable choice. Check the explanation beside it before continuing.";
            return;
        }
        SelectedCandidates = selected.Select(c => c.Candidate).ToArray();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}