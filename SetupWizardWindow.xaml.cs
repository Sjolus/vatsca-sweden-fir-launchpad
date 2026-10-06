using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class SetupWizardWindow : Window
{
    private readonly SetupWizardState _state;
    private bool _ready, _childOpen;
    private int _page;
    private int _pathCheckGeneration;
    private bool _closed;
    private readonly ConfiguredPathValidationService _pathValidator = new();
    public AppSettings Settings { get; private set; }
    public bool Finished { get; private set; }
    public bool Dismissed { get; private set; }
    public bool HasSavedActions => _state.HasSavedActions;
    public bool RestartRequired => _state.RestartRequired;
    public bool RemovalRequested { get; private set; }

    public SetupWizardWindow(AppSettings current)
    {
        _state = new(current);
        Settings = current.Copy();
        InitializeComponent();
        Closed += (_, _) => { _closed = true; _pathCheckGeneration++; };
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        LoadPreferences();
        _ready = true;
        RenderPage();
    }

    private void LoadPreferences()
    {
        bool wasReady = _ready;
        _ready = false;
        DarkModeChoice.IsChecked = _state.Draft.IsDarkMode;
        CompactChoice.IsChecked = _state.Draft.CompactLayout;
        StartupChoice.IsChecked = _state.Draft.CheckOnStartup;
        _ready = wasReady;
    }

    private void Route_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _state.Route = ManualChoice.IsChecked == true ? SetupWizardRoute.Manual :
            FreshChoice.IsChecked == true ? SetupWizardRoute.Fresh : SetupWizardRoute.Existing;
        UpdateStepHeading();
    }

    private void Preferences_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _state.Draft.IsDarkMode = DarkModeChoice.IsChecked == true;
        _state.Draft.CompactLayout = CompactChoice.IsChecked == true;
        _state.Draft.CheckOnStartup = StartupChoice.IsChecked == true;
        App.SetTheme(_state.Draft.IsDarkMode);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_childOpen) return;
        if (_page == 3)
        {
            Settings = _state.Finish();
            Finished = true;
            DialogResult = true;
            return;
        }
        _page = _page == 0 && _state.Route == SetupWizardRoute.Manual ? 2 : _page + 1;
        RenderPage();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_childOpen || _page == 0) return;
        _page = _page == 2 && _state.Route == SetupWizardRoute.Manual ? 0 : _page - 1;
        RenderPage();
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateStepHeading()
    {
        int steps = _state.Route == SetupWizardRoute.Manual ? 3 : 4;
        int step = _page + 1 - (_state.Route == SetupWizardRoute.Manual && _page >= 2 ? 1 : 0);
        string title = _page switch { 0 => "Welcome", 1 => "Applications", 2 => "Profile and preferences", _ => "Finish" };
        StepText.Text = $"Step {step} of {steps} · {title}";
    }

    private void RenderPage()
    {
        var pages = new[] { WelcomePage, ApplicationsPage, PreferencesPage, SummaryPage };
        for (int i = 0; i < pages.Length; i++) pages[i].Visibility = i == _page ? Visibility.Visible : Visibility.Collapsed;
        UpdateStepHeading();
        BackButton.IsEnabled = _page > 0;
        NextButton.Content = _page == 3 ? "Finish" : "Next";
        ApplicationsHint.Text = _state.Route == SetupWizardRoute.Existing
            ? "First choose the copies you already use. You can also review installing a missing program below."
            : "Choose a program to install below. If it is already on your PC, find or choose that copy instead. Each installation needs your confirmation.";
        RefreshPaths();
        var guides = _state.NextSteps();
        NextStepsPanel.Visibility = guides.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EuroScopeGuidePanel.Visibility = guides.Contains(SetupWizardGuide.EuroScopeGng) ? Visibility.Visible : Visibility.Collapsed;
        TrackAudioGuidePanel.Visibility = guides.Contains(SetupWizardGuide.TrackAudio) ? Visibility.Visible : Visibility.Collapsed;
        VatisGuidePanel.Visibility = guides.Contains(SetupWizardGuide.Vatis) ? Visibility.Visible : Visibility.Collapsed;
        SummaryText.Text = string.Join(Environment.NewLine + Environment.NewLine, _state.Summary());
        ContentScroll.ScrollToTop();
    }

    private void Guide_Click(object sender, RoutedEventArgs e)
    {
        if (_childOpen || sender is not FrameworkElement { Tag: SetupWizardGuide guide }) return;
        try
        {
            Process.Start(new ProcessStartInfo(SetupWizardState.GuideUrl(guide)) { UseShellExecute = true });
            StatusText.Text = "The guide was opened in your browser. Choose Finish to save your Launchpad choices when you are done.";
        }
        catch
        {
            StatusText.Text = "The browser could not open the guide. You can find it on the VATSIM Scandinavia wiki.";
        }
    }

    private async void RefreshPaths()
    {
        int generation = ++_pathCheckGeneration;
        var choices = new[]
        {
            (Kind: ConfiguredPathKind.EuroScopeExecutable, Path: _state.Draft.EuroscopeExePath, Label: EuroScopePath),
            (Kind: ConfiguredPathKind.VacsExecutable, Path: _state.Draft.VacsExePath, Label: VacsPath),
            (Kind: ConfiguredPathKind.TrackAudioExecutable, Path: _state.Draft.TrackAudioExePath, Label: TrackAudioPath),
            (Kind: ConfiguredPathKind.VatisExecutable, Path: _state.Draft.VatisExePath, Label: VatisPath)
        };
        foreach (var choice in choices)
            choice.Label.Text = string.IsNullOrWhiteSpace(choice.Path) ? "No program chosen. You can skip this program." : "Checking program file: " + choice.Path;
        EuroScopeButton.IsEnabled = ProfileButton.IsEnabled = !_state.RestartRequired;
        SetFreshAvailability(VacsButton, _state.Draft.VacsExePath);
        SetFreshAvailability(TrackAudioButton, _state.Draft.TrackAudioExePath);
        SetFreshAvailability(VatisButton, _state.Draft.VatisExePath);
        try
        {
            var results = await Task.Run(() => choices.Select(c => _pathValidator.Validate(c.Kind, c.Path)).ToArray());
            if (_closed || generation != _pathCheckGeneration) return;
            for (int i = 0; i < choices.Length; i++)
            {
                choices[i].Label.Text = results[i].Message + (string.IsNullOrWhiteSpace(choices[i].Path) ? "" : "\n" + choices[i].Path);
                choices[i].Label.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty,
                    results[i].Status is PathValidationStatus.Warning or PathValidationStatus.Invalid ? "WarningFg" : "SecondaryText");
            }
        }
        catch
        {
            if (!_closed && generation == _pathCheckGeneration)
                foreach (var choice in choices) choice.Label.Text = "This program could not be checked. Review its location in Settings.";
        }
    }

    private void SetFreshAvailability(System.Windows.Controls.Button button, string path)
    {
        bool executableExists = File.Exists(path);
        button.IsEnabled = _state.CanReviewFreshSetup(executableExists);
        button.ToolTip = _state.RestartRequired ? "Restart Windows before further setup." :
            executableExists ? "A file exists at the chosen program location. Use Check for Updates in Launchpad, or choose a different file in Settings." :
            string.IsNullOrWhiteSpace(path) ? "Review installing this program. Nothing is installed until you confirm." :
            "The chosen program file was not found. You can review an installation; setup will first check for remaining program folders and settings.";
    }

    private bool? ShowChild(Window child)
    {
        child.Owner = this;
        _childOpen = true;
        try { return child.ShowDialog(); }
        finally { _childOpen = false; }
    }

    private void Discover_Click(object sender, RoutedEventArgs e) => OpenSettings(discover: true);
    private void Paths_Click(object sender, RoutedEventArgs e) => OpenSettings(discover: false);
    private void OpenSettings(bool discover)
    {
        if (_childOpen) return;
        try
        {
            var window = new SettingsWindow(_state.Draft.Copy(), discoverOnLoad: discover, showApplicationPaths: !discover);
            if (ShowChild(window) == true)
            {
                _state.AcceptSettings(window.Settings);
                LoadPreferences();
                SaveCompletedAction("Your chosen program and folder locations were saved. Existing program settings were not imported or moved.");
            }
            else StatusText.Text = "Settings closed without saving new file or folder choices.";
        }
        catch (Exception ex) { StatusText.Text = "Settings could not be opened: " + ex.Message; }
        RefreshPaths();
    }

    private void Profile_Click(object sender, RoutedEventArgs e)
    {
        if (_childOpen || _state.RestartRequired) return;
        try
        {
            var window = new AppConfigWindow(_state.Draft.Copy(), _state.Draft.EuroscopeDataPath);
            if (ShowChild(window) == true)
            {
                _state.AcceptControllerProfile(window.Settings);
                SaveCompletedAction("Controller profile saved. Any file changes reported by that dialog have already taken effect.");
            }
            else StatusText.Text = "Controller profile closed. No saved profile was returned.";
        }
        catch (Exception ex) { StatusText.Text = "Controller profile could not be opened: " + ex.Message; }
    }

    private void EuroScope_Click(object sender, RoutedEventArgs e)
    {
        if (_childOpen || _state.RestartRequired) return;
        try
        {
            var window = new EuroScopeInstallWindow(_state.Draft.Copy());
            ShowChild(window);
            if (window.RestartRequired) _state.RequireRestart();
            if (window.Result is { InstallationCompleted: true } result)
            {
                _state.RecordInstallation(SetupWizardApplication.EuroScope, result.ExecutablePath, result.RestartRequired, result.BackupFolder);
                SaveCompletedAction("EuroScope setup completed and its program location was saved. You can open it later from Launchpad.");
            }
            else
            {
                _state.RecordIncompleteSetup("EuroScope");
                StatusText.Text = "EuroScope setup closed without a confirmed installation. Follow any restart or recovery instructions from its setup window.";
            }
            RefreshPaths();
            if (_state.RestartRequired) StatusText.Text += " Restart Windows before further installation or profile changes.";
            if (window.AdoptionRequested && !_state.RestartRequired) OpenSettings(discover: true);
            else if (window.RemovalRequested && !_state.RestartRequired) RequestRemoval();
        }
        catch (Exception ex) { StatusText.Text = "EuroScope setup could not finish: " + ex.Message; }
    }

    private void Software_Click(object sender, RoutedEventArgs e)
    {
        if (_childOpen || _state.RestartRequired || sender is not FrameworkElement { Tag: string tag } ||
            !Enum.TryParse<SoftwareApp>(tag, out var app)) return;
        try
        {
            var window = new FreshSoftwareInstallWindow(app, _state.Draft.Copy());
            ShowChild(window);
            if (window.RestartRequired) _state.RequireRestart();
            if (window.Result is { } result)
            {
                var wizardApp = app switch
                {
                    SoftwareApp.Vacs => SetupWizardApplication.Vacs,
                    SoftwareApp.Vatis => SetupWizardApplication.Vatis,
                    _ => SetupWizardApplication.TrackAudio
                };
                _state.RecordInstallation(wizardApp, result.ExecutablePath, result.RestartRequired, result.BackupFolder);
                SaveCompletedAction("Installation completed and the program location was saved. You can open it later from Launchpad.");
            }
            else
            {
                _state.RecordIncompleteSetup(app == SoftwareApp.Vacs ? "VACS" : app == SoftwareApp.Vatis ? "vATIS" : "TrackAudio");
                StatusText.Text = "Setup closed without a confirmed installation. Follow any restart or recovery instructions from its setup window.";
            }
            RefreshPaths();
            if (_state.RestartRequired) StatusText.Text += " Restart Windows before further installation or profile changes.";
            if (window.AdoptionRequested && !_state.RestartRequired) OpenSettings(discover: true);
            else if (window.RemovalRequested && !_state.RestartRequired) RequestRemoval();
        }
        catch (Exception ex) { StatusText.Text = "Application setup could not finish: " + ex.Message; }
    }

    private void SaveCompletedAction(string message)
    {
        try
        {
            SettingsService.Save(_state.Dismiss());
            StatusText.Text = message;
        }
        catch
        {
            StatusText.Text = "The action completed, but Launchpad could not save its choices. Finish setup to retry saving; completed installations and profile changes have not been undone.";
        }
    }

    private void RequestRemoval()
    {
        RemovalRequested = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_childOpen || OwnedWindows.Cast<Window>().Any(w => w.IsVisible))
        {
            e.Cancel = true;
            base.OnClosing(e);
            return;
        }
        if (!Finished)
        {
            Dismissed = true;
            Settings = _state.Dismiss();
        }
        App.SetTheme(Settings.IsDarkMode);
        base.OnClosing(e);
    }
}
