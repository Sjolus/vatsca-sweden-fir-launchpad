using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
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
    private FontsCheckResult? _fonts;
    private sealed record DiscoveredCopy(string Path, string Label);
    public AppSettings Settings { get; private set; }
    public bool Finished { get; private set; }
    public bool Dismissed { get; private set; }
    public bool HasSavedActions => _state.HasSavedActions;
    public bool RestartRequired => _state.RestartRequired;
    public bool RemovalRequested { get; private set; }
    public bool GngSetupRequested { get; private set; }

    public SetupWizardWindow(AppSettings current) : this(new SetupWizardState(current)) { }

    internal SetupWizardWindow(SetupWizardState state)
    {
        _state = state;
        _page = (int)state.Step;
        Settings = state.Dismiss();
        InitializeComponent();
        Closed += (_, _) => { _closed = true; _pathCheckGeneration++; };
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        LoadPreferences();
        ExistingChoice.IsChecked = _state.Route == SetupWizardRoute.Existing;
        FreshChoice.IsChecked = _state.Route == SetupWizardRoute.Fresh;
        ManualChoice.IsChecked = _state.Route == SetupWizardRoute.Manual;
        App.SetTheme(_state.Draft.IsDarkMode);
        _ready = true;
        RenderPage();
        if (_state.RestartRequired) StatusText.Text = "Windows restart required. Further installations, controller-profile changes and font previews are unavailable until Windows restarts.";
    }

    private void LoadPreferences()
    {
        bool wasReady = _ready;
        _ready = false;
        DarkModeChoice.IsChecked = _state.Draft.IsDarkMode;
        CompactChoice.IsChecked = _state.Draft.CompactLayout;
        StartupChoice.IsChecked = _state.Draft.CheckOnStartup;
        foreach (var choice in new[] { EuroScopeNotUsed, TrackAudioNotUsed, VacsNotUsed, VatisNotUsed, VatEfsNotUsed })
            choice.IsChecked = _state.IsNotUsed((string)choice.Tag);
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
        if (_page == 5)
        {
            Settings = _state.Finish();
            Finished = true;
            DialogResult = true;
            return;
        }
        _page = _state.Route == SetupWizardRoute.Manual ? (_page == 0 ? 3 : 5) : _page + 1;
        SaveProgress();
        RenderPage();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_childOpen || _page == 0) return;
        _page = _state.Route == SetupWizardRoute.Manual ? (_page == 5 ? 3 : 0) : _page - 1;
        SaveProgress();
        RenderPage();
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateStepHeading()
    {
        int steps = _state.Route == SetupWizardRoute.Manual ? 3 : 6;
        int step = _state.Route == SetupWizardRoute.Manual ? (_page == 0 ? 1 : _page == 3 ? 2 : 3) : _page + 1;
        string title = _page switch { 0 => "Welcome", 1 => "Applications", 2 => "Swedish GNG", 3 => "Controller profile", 4 => "Fonts", _ => "Finish" };
        StepText.Text = $"Step {step} of {steps} · {title}";
    }

    private void RenderPage()
    {
        var pages = new[] { WelcomePage, ApplicationsPage, GngPage, PreferencesPage, FontsPage, SummaryPage };
        for (int i = 0; i < pages.Length; i++) pages[i].Visibility = i == _page ? Visibility.Visible : Visibility.Collapsed;
        UpdateStepHeading();
        BackButton.IsEnabled = _page > 0;
        NextButton.Content = _page == 5 ? "Finish" : "Next";
        ApplicationsHint.Text = _state.Route == SetupWizardRoute.Existing
            ? "Find the copies you already use, choose another location, or install a missing program. Not used keeps optional applications out of your attention list."
            : "Install the applications you need one at a time. If you already have a copy, find or choose it instead. Each installation needs your confirmation.";
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
            (Kind: ConfiguredPathKind.VatisExecutable, Path: _state.Draft.VatisExePath, Label: VatisPath),
            (Kind: ConfiguredPathKind.VatEfsFolder, Path: _state.Draft.VatEfsPath, Label: VatEfsPath),
            (Kind: ConfiguredPathKind.EuroScopeDataFolder, Path: _state.Draft.EuroscopeDataPath, Label: GngPath)
        };
        foreach (var choice in choices)
            choice.Label.Text = string.IsNullOrWhiteSpace(choice.Path) ? "No program chosen. You can skip this program." : "Checking program file: " + choice.Path;
        EuroScopeButton.IsEnabled = ProfileButton.IsEnabled = GngButton.IsEnabled = !_state.RestartRequired;
        FontsInstallButton.IsEnabled = !_state.RestartRequired && _fonts?.Entries.Any(f => !f.IsUpToDate && f.SourcePath != null) == true;
        SetFreshAvailability(VacsButton, _state.Draft.VacsExePath);
        SetFreshAvailability(TrackAudioButton, _state.Draft.TrackAudioExePath);
        SetFreshAvailability(VatisButton, _state.Draft.VatisExePath);
        SetFreshAvailability(VatEfsButton, string.IsNullOrWhiteSpace(_state.Draft.VatEfsPath) ? "" : Path.Combine(_state.Draft.VatEfsPath, "efs.exe"),
            !string.IsNullOrWhiteSpace(_state.Draft.VatEfsPath) && File.Exists(Path.Combine(_state.Draft.VatEfsPath, "VatEFS.dll")));
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

    private void SetFreshAvailability(System.Windows.Controls.Button button, string path, bool otherApplicationFileExists = false)
    {
        bool executableExists = File.Exists(path) || otherApplicationFileExists;
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

    private async void Discover_Click(object sender, RoutedEventArgs e)
    {
        if (_childOpen) return;
        _childOpen = true;
        DiscoveryButton.IsEnabled = false;
        DiscoveryStatus.Text = GngDiscoveryStatus.Text = "Looking for installed applications and GNG in known locations…";
        try
        {
            var candidates = await Task.Run(() => new InstallationDiscoveryService().Discover());
            if (_closed) return;
            foreach (var kind in Enum.GetValues<ConfiguredPathKind>())
            {
                var copies = candidates.Where(c => CandidateKind(c.Id) == kind).Select(c =>
                {
                    string path = kind is ConfiguredPathKind.EuroScopeDataFolder or ConfiguredPathKind.VatEfsFolder ? c.DataPath ?? "" : c.ExecutablePath;
                    return new DiscoveredCopy(path, path);
                }).DistinctBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToArray();
                var (list, panel) = DiscoveryControls(kind);
                list.ItemsSource = copies;
                list.SelectedIndex = copies.Length > 0 ? 0 : -1;
                panel.Visibility = copies.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            DiscoveryStatus.Text = candidates.Count == 0
                ? "Nothing found in the usual locations. Choose a custom location or install the application you need."
                : "Found copies are shown below. Use this copy checks and saves only that location; configuration stays in place.";
            GngDiscoveryStatus.Text = candidates.Any(c => c.Id == AtcRemovalApp.Gng)
                ? "Choose a folder above, then Use this copy to check it."
                : "No GNG found in the usual locations. Choose its data folder, or install GNG below.";
        }
        catch { DiscoveryStatus.Text = GngDiscoveryStatus.Text = "The search could not finish. You can choose a location directly instead."; }
        finally { _childOpen = false; if (!_closed) DiscoveryButton.IsEnabled = true; }
    }

    private static ConfiguredPathKind? CandidateKind(AtcRemovalApp app) => app switch
    {
        AtcRemovalApp.EuroScope => ConfiguredPathKind.EuroScopeExecutable,
        AtcRemovalApp.Gng => ConfiguredPathKind.EuroScopeDataFolder,
        AtcRemovalApp.Vacs => ConfiguredPathKind.VacsExecutable,
        AtcRemovalApp.TrackAudio => ConfiguredPathKind.TrackAudioExecutable,
        AtcRemovalApp.Vatis => ConfiguredPathKind.VatisExecutable,
        AtcRemovalApp.VatEfs => ConfiguredPathKind.VatEfsFolder,
        _ => null
    };

    private (ComboBox List, Grid Panel) DiscoveryControls(ConfiguredPathKind kind) => kind switch
    {
        ConfiguredPathKind.EuroScopeExecutable => (EuroScopeCopies, EuroScopeDiscovery),
        ConfiguredPathKind.EuroScopeDataFolder => (GngCopies, GngDiscovery),
        ConfiguredPathKind.VacsExecutable => (VacsCopies, VacsDiscovery),
        ConfiguredPathKind.TrackAudioExecutable => (TrackAudioCopies, TrackAudioDiscovery),
        ConfiguredPathKind.VatisExecutable => (VatisCopies, VatisDiscovery),
        ConfiguredPathKind.VatEfsFolder => (VatEfsCopies, VatEfsDiscovery),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private async void UseCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_childOpen || sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<ConfiguredPathKind>(tag, out var kind)) return;
        if (DiscoveryControls(kind).List.SelectedItem is DiscoveredCopy copy) await AdoptLocationAsync(kind, copy.Path);
    }

    private async void BrowseLocation_Click(object sender, RoutedEventArgs e)
    {
        if (_childOpen || sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<ConfiguredPathKind>(tag, out var kind)) return;
        string? path = null;
        if (kind is ConfiguredPathKind.EuroScopeDataFolder or ConfiguredPathKind.VatEfsFolder)
        {
            var picker = new OpenFolderDialog { Title = kind == ConfiguredPathKind.EuroScopeDataFolder ? "Choose the EuroScope data folder containing ESAA" : "Choose the folder containing VatEFS.dll" };
            if (picker.ShowDialog(this) == true) path = picker.FolderName;
        }
        else
        {
            var picker = new OpenFileDialog { Title = "Choose the installed application's program file (not its installer)", Filter = "Application (*.exe)|*.exe", CheckFileExists = true };
            if (picker.ShowDialog(this) == true) path = picker.FileName;
        }
        if (path != null) await AdoptLocationAsync(kind, path);
    }

    private async Task AdoptLocationAsync(ConfiguredPathKind kind, string path)
    {
        _childOpen = true;
        StatusText.Text = "Checking this location without starting the application…";
        try
        {
            var result = await Task.Run(() => _pathValidator.Validate(kind, path));
            if (_closed) return;
            if (result.Status is PathValidationStatus.Invalid or PathValidationStatus.NotSelected) { StatusText.Text = result.Message; return; }
            if (result.Status == PathValidationStatus.Warning && MessageBox.Show(this,
                result.Message + "\n\n" + path + "\n\nUse this location anyway? Only continue if you recognise this copy.",
                "Check the selected location", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            if (kind == ConfiguredPathKind.EuroScopeDataFolder)
            {
                _state.AcceptGngFolder(path, installed: false);
                _fonts = null;
                FontsStatus.Text = "The GNG folder changed. Choose Check fonts to compare against this package.";
            }
            else _state.AcceptLocation(kind switch
            {
                ConfiguredPathKind.EuroScopeExecutable => SetupWizardApplication.EuroScope,
                ConfiguredPathKind.VacsExecutable => SetupWizardApplication.Vacs,
                ConfiguredPathKind.VatisExecutable => SetupWizardApplication.Vatis,
                ConfiguredPathKind.TrackAudioExecutable => SetupWizardApplication.TrackAudio,
                ConfiguredPathKind.VatEfsFolder => SetupWizardApplication.VatEfs,
                _ => throw new InvalidOperationException("Unsupported application location.")
            }, path);
            SaveCompletedAction("Location saved. Existing configuration and credentials were left in place.");
            LoadPreferences();
            RefreshPaths();
        }
        catch { StatusText.Text = "The location could not be checked. Choose another copy or try again."; }
        finally { _childOpen = false; }
    }

    private void NotUsed_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not CheckBox { Tag: string appName } choice) return;
        _state.SetNotUsed(appName, choice.IsChecked == true);
        SaveCompletedAction(choice.IsChecked == true ? appName + " marked as not used. Its location and configuration are kept." : appName + " included in your application list again.");
    }

    private void Gng_Click(object sender, RoutedEventArgs e)
    {
        if (_childOpen || _state.RestartRequired) return;
        SaveProgress();
        GngSetupRequested = true;
        Close();
    }

    private void FontsCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_childOpen) return;
        _fonts = FontService.Check(_state.Draft.EuroscopeDataPath);
        FontsStatus.Text = _fonts.State == FontsState.Unknown ? "GNG font files were not found. Go Back to choose or install GNG first." :
            string.Join(Environment.NewLine, _fonts.Entries.Select(f => f.FileName + " — " + (f.SourcePath == null ? "not supplied by this GNG folder" : f.IsUpToDate ? "installed; no update needed" : "missing or older than the GNG copy")));
        FontsInstallButton.IsEnabled = !_state.RestartRequired && _fonts.Entries.Any(f => !f.IsUpToDate && f.SourcePath != null);
    }

    private void FontsInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_childOpen || _state.RestartRequired || _fonts == null) return;
        // Recheck source paths immediately before opening their Windows previews.
        _fonts = FontService.Check(_state.Draft.EuroscopeDataPath);
        FontService.InstallMissing(_fonts);
        StatusText.Text = "Choose Install in each Windows font preview, then Check fonts again here. Nothing is installed automatically.";
    }

    private void SaveProgress()
    {
        _state.MoveTo((SetupWizardStep)_page);
        try { SettingsService.Save(_state.Dismiss()); }
        catch { StatusText.Text = "Your place could not be saved. Completed actions remain in effect; Finish will retry saving."; }
    }
    private void Profile_Click(object sender, RoutedEventArgs e)
    {
        if (_childOpen || _state.RestartRequired) return;
        try
        {
            if (!string.IsNullOrWhiteSpace(_state.Draft.EuroscopeDataPath))
                GngUpdateService.RequireNoPendingUpdate(_state.Draft.EuroscopeDataPath);
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
            LoadPreferences();
            RefreshPaths();
            if (_state.RestartRequired) StatusText.Text += " Restart Windows before further installation or profile changes.";
            if (window.AdoptionRequested && !_state.RestartRequired) Discover_Click(this, new RoutedEventArgs());
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
                    SoftwareApp.VatEfs => SetupWizardApplication.VatEfs,
                    _ => SetupWizardApplication.TrackAudio
                };
                _state.RecordInstallation(wizardApp, result.ExecutablePath, result.RestartRequired, result.BackupFolder);
                SaveCompletedAction(app == SoftwareApp.VatEfs
                    ? "VatEFS installation completed and its folder was saved. Use Controller profile to enable its EuroScope plugin; its browser opens after the plugin starts."
                    : "Installation completed and the program location was saved. You can open it later from Launchpad.");
            }
            else
            {
                _state.RecordIncompleteSetup(app == SoftwareApp.Vacs ? "VACS" : app == SoftwareApp.Vatis ? "vATIS" : app == SoftwareApp.VatEfs ? "VatEFS" : "TrackAudio");
                StatusText.Text = "Setup closed without a confirmed installation. Follow any restart or recovery instructions from its setup window.";
            }
            LoadPreferences();
            RefreshPaths();
            if (_state.RestartRequired) StatusText.Text += " Restart Windows before further installation or profile changes.";
            if (window.AdoptionRequested && !_state.RestartRequired) Discover_Click(this, new RoutedEventArgs());
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
