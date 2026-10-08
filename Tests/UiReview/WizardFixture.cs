using System.Windows;
using System.Windows.Controls;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

// Presentation-only navigation for the linked production XAML. There are deliberately
// no settings persistence, services, native adapters or production dialog actions here.
public partial class SetupWizardWindow : Window
{
    private bool _ready, _restartRequired;
    private bool? _syntheticExecutableExists;
    private AppSettings? _syntheticGuideSettings;
    internal int SyntheticPage { get; private set; }
    internal bool SyntheticGngSetupRequested { get; private set; }

    public SetupWizardWindow()
    {
        InitializeComponent();
        _ready = true;
        UiFixture.Initialize(this);
    }

    internal void RefreshSyntheticScenario()
    {
        if (!_ready) return;
        DarkModeChoice.IsChecked = UiFixture.Dark;
        CompactChoice.IsChecked = true;
        StartupChoice.IsChecked = false;
        RenderSyntheticPage();
    }

    internal void SetSyntheticRestart(bool required)
    {
        _restartRequired = required;
        StatusText.Text = required ? "Synthetic restart required. Further setup is unavailable until Windows restarts." : "";
        RenderSyntheticPage();
    }

    // Presence is injected; never probe real client paths in this fixture.
    internal void SetSyntheticExecutablePresence(bool? exists)
    {
        _syntheticExecutableExists = exists;
        RenderSyntheticPage();
    }

    internal void SetSyntheticGuideSettings(AppSettings? settings)
    {
        _syntheticGuideSettings = settings?.Copy();
        RenderSyntheticPage();
    }

    internal void SyntheticNext()
    {
        if (SyntheticPage == 3)
        {
            SyntheticGngSetupRequested = !_restartRequired && GngAfterFinishChoice.IsChecked == true;
            StatusText.Text = SyntheticGngSetupRequested
                ? "Synthetic finish: GNG setup was explicitly requested. No window, browser, download or installer was started."
                : "Synthetic finish only. No preferences were saved and no clients were started.";
            return;
        }
        SyntheticPage = SyntheticPage == 0 && ManualChoice.IsChecked == true ? 2 : SyntheticPage + 1;
        RenderSyntheticPage();
    }

    internal void SyntheticBack()
    {
        if (SyntheticPage == 0) return;
        SyntheticPage = SyntheticPage == 2 && ManualChoice.IsChecked == true ? 0 : SyntheticPage - 1;
        RenderSyntheticPage();
    }

    private void RenderSyntheticPage()
    {
        if (!_ready) return;
        var pages = new[] { WelcomePage, ApplicationsPage, PreferencesPage, SummaryPage };
        for (var index = 0; index < pages.Length; index++)
            pages[index].Visibility = index == SyntheticPage ? Visibility.Visible : Visibility.Collapsed;
        bool manual = ManualChoice.IsChecked == true;
        int step = manual && SyntheticPage > 0 ? SyntheticPage : SyntheticPage + 1;
        StepText.Text = $"Step {step} of {(manual ? 3 : 4)} — " + new[] { "Welcome", "Applications", "Profile and preferences", "Finish" }[SyntheticPage];
        BackButton.IsEnabled = SyntheticPage > 0;
        NextButton.Content = SyntheticPage == 3 ? "Finish" : "Next";
        ApplicationsHint.Text = ExistingChoice.IsChecked == true
            ? "First choose the copies you already use. You can also review installing a missing program below."
            : "Choose a program to install below. If it is already on your PC, find or choose that copy instead. Each installation needs your confirmation.";
        foreach (var (button, path) in new[] { (EuroScopeButton, EuroScopePath), (VacsButton, VacsPath), (TrackAudioButton, TrackAudioPath), (VatisButton, VatisPath), (VatEfsButton, VatEfsPath) })
        {
            bool exists = _syntheticExecutableExists ?? !UiFixture.Blank;
            button.IsEnabled = !_restartRequired && (ReferenceEquals(button, EuroScopeButton) || !exists);
            string configuredPath = @"C:\Synthetic ATC applications\" + path.Name + @"\application.exe";
            path.Text = UiFixture.Blank ? "No program file chosen. You can skip this program."
                : exists ? "Chosen program file: " + configuredPath + "\nUse Check for Updates in Launchpad to check this copy."
                : "Program file not found: " + configuredPath + "\nChoose its current location in Settings, or review installation below.";
            button.ToolTip = _restartRequired ? "Restart Windows before further setup."
                : exists ? "A file exists at the chosen program location. Use Check for Updates in Launchpad, or choose a different file in Settings."
                : "The chosen program file was not found. You can review an installation; setup will first check for remaining program folders and settings.";
        }
        ProfileButton.IsEnabled = !_restartRequired;
        GngAfterFinishChoice.IsEnabled = !_restartRequired;
        if (_restartRequired) GngAfterFinishChoice.IsChecked = false;
        var guideSettings = _syntheticGuideSettings ?? (UiFixture.Blank ? new AppSettings() : new AppSettings
        {
            EuroscopeExePath = @"C:\Synthetic ATC applications\EuroScope.exe",
            TrackAudioExePath = @"C:\Synthetic ATC applications\TrackAudio.exe",
            VatisExePath = @"C:\Synthetic ATC applications\vATIS.exe"
        });
        var guides = new SetupWizardState(guideSettings).NextSteps();
        NextStepsPanel.Visibility = guides.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EuroScopeGuidePanel.Visibility = guides.Contains(SetupWizardGuide.EuroScopeGng) ? Visibility.Visible : Visibility.Collapsed;
        TrackAudioGuidePanel.Visibility = guides.Contains(SetupWizardGuide.TrackAudio) ? Visibility.Visible : Visibility.Collapsed;
        VatisGuidePanel.Visibility = guides.Contains(SetupWizardGuide.Vatis) ? Visibility.Visible : Visibility.Collapsed;
        var summarySettings = guideSettings.Copy();
        summarySettings.IsDarkMode = DarkModeChoice.IsChecked == true;
        summarySettings.CompactLayout = CompactChoice.IsChecked == true;
        summarySettings.CheckOnStartup = StartupChoice.IsChecked == true;
        SummaryText.Text = "SYNTHETIC CHOICES — no settings are saved\n\n" +
            string.Join(Environment.NewLine + Environment.NewLine, new SetupWizardState(summarySettings).Summary());
        ContentScroll.ScrollToTop();
    }

    private void Route_Changed(object sender, RoutedEventArgs e) { if (_ready) RenderSyntheticPage(); }
    private void Preferences_Changed(object sender, RoutedEventArgs e) { if (_ready && SyntheticPage == 3) RenderSyntheticPage(); }
    private void Skip_Click(object sender, RoutedEventArgs e) => Close();
    private void Back_Click(object sender, RoutedEventArgs e) => SyntheticBack();
    private void Next_Click(object sender, RoutedEventArgs e) => SyntheticNext();
    private void Discover_Click(object sender, RoutedEventArgs e) => SyntheticAction("Discovery");
    private void Paths_Click(object sender, RoutedEventArgs e) => SyntheticAction("Path selection");
    private void EuroScope_Click(object sender, RoutedEventArgs e) => SyntheticAction("EuroScope setup review");
    private void Software_Click(object sender, RoutedEventArgs e) => SyntheticAction((sender as Button)?.Tag + " setup review");
    private void Profile_Click(object sender, RoutedEventArgs e) => SyntheticAction("Controller profile review");
    private void Guide_Click(object sender, RoutedEventArgs e) => SyntheticAction("Guide " + (sender as FrameworkElement)?.Tag);
    private void SyntheticAction(string action) => StatusText.Text = action + " is inert in this synthetic fixture. No files, credentials, network or applications were accessed.";
}
