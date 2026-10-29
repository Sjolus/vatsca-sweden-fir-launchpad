using System.IO;
using System.Windows;
using System.Windows.Controls;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

// Production XAML with in-memory examples only. No installer, network or profile service is linked.
public partial class SaamanWindow : Window
{
    private bool _busy, _restart, _profileEnabled, _hasRecovery, _ready;
    private string? _blocked;
    public SaamanWindow()
    {
        InitializeComponent();
        DllPathText.Text = @"C:\Synthetic\EuroScope\ESAA\Plugins\SAAMAN\SAAMAN.dll";
        ProfilePathText.Text = @"C:\Synthetic\EuroScope\ESAA APP ACC.prf";
        ProfileStateText.Text = "Enabled · first plugin in this profile.";
        ProfileEnabledCheck.IsChecked = true;
        RuntimeButton.Content = "WebView2 is available";
        RuntimeButton.IsEnabled = false;
        _ready = true;
    }
    internal void SetScenario(string scenario)
    {
        _busy = scenario == "downloading";
        _restart = scenario == "restart";
        _profileEnabled = scenario != "install";
        _hasRecovery = scenario.StartsWith("restore", StringComparison.Ordinal);
        _blocked = scenario is "blocked" or "restore-blocked" ? "Close every EuroScope instance before changing SAAMAN." : null;
        ReviewPanel.Visibility = ConfirmChange.Visibility = RestoreButton.Visibility = CancelReviewButton.Visibility =
            _hasRecovery ? Visibility.Visible : Visibility.Collapsed;
        ReviewText.Text = "Restore the previous synthetic SAAMAN DLL. The current DLL is preserved in the recovery record.";
        ConfirmChangeText.Text = "I have reviewed this restore.";
        ConfirmChange.IsChecked = false;
        StatusText.Text = _busy ? "Downloading SAAMAN.dll — 64%" : _hasRecovery ? "Review the previous DLL before restoring it." : "Choose an existing DLL or install the latest stable release.";
        Activity.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        Activity.Value = 64;
        OptionsPanel.IsEnabled = !_busy;
        ProfileEnabledCheck.IsChecked = _profileEnabled;
        CancelOperationButton.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        RefreshSyntheticActions();
    }
    private void RefreshSyntheticActions() => PresentActionAvailability(_busy, _restart, _profileEnabled, _hasRecovery, _blocked);
    private void Install_Click(object sender, RoutedEventArgs e) { }
    private void CancelOperation_Click(object sender, RoutedEventArgs e) => SetScenario("update");
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Adopt_Click(object sender, RoutedEventArgs e) { }
    private void ReleaseNotes_Click(object sender, RoutedEventArgs e) { }
    private void Runtime_Click(object sender, RoutedEventArgs e) { }
    private void ChooseProfile_Click(object sender, RoutedEventArgs e) { }
    private void ConfirmationChanged(object sender, RoutedEventArgs e) { if (_ready) RefreshSyntheticActions(); }
    private void Recovery_Click(object sender, RoutedEventArgs e) => SetScenario("restore");
    private void Restore_Click(object sender, RoutedEventArgs e) { }
    private void ProfileChoiceChanged(object sender, RoutedEventArgs e) { }
    private void CancelReview_Click(object sender, RoutedEventArgs e) => SetScenario("update");
    private void ProfileInfo_Click(object sender, RoutedEventArgs e) { }
    private void ProfileInfo_Close(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) { }

}

internal static class SaamanLayoutReview
{
    public static string Run(string? output)
    {
        if (output != null) Directory.CreateDirectory(output);
        int count = 0;
        foreach (bool dark in new[] { true, false })
        {
            if (UiFixture.Dark != dark) UiFixture.ToggleTheme();
            foreach (double textScale in new[] { 1d, 1.25d, 1.5d })
            foreach (bool minimum in new[] { false, true })
            foreach (var scenario in new[] { "install", "update", "downloading", "blocked", "restart", "restore", "restore-blocked" })
            {
                UiFixture.SetTextScale(textScale);
                var window = new SaamanWindow();
                window.SetScenario(scenario);
                if (minimum) { window.Width = window.MinWidth; window.Height = window.MinHeight; }
                try
                {
                    LayoutValidation.CheckWindowContent(window);
                    var content = (FrameworkElement)window.Content;
                    foreach (var name in scenario.StartsWith("restore", StringComparison.Ordinal) ? new[] { "InstallButton", "CloseButton", "ConfirmChange" } : new[] { "InstallButton", "CloseButton" })
                    {
                        var control = (FrameworkElement)window.FindName(name);
                        var bounds = control.TransformToAncestor(content).TransformBounds(new Rect(control.RenderSize));
                        if (bounds.Width < 20 || bounds.Height < (name == "ConfirmChange" ? 14 : 20) || bounds.Left < -1 || bounds.Top < -1 ||
                            bounds.Right > content.ActualWidth + 1 || bounds.Bottom > content.ActualHeight + 1)
                            throw new InvalidDataException($"SAAMAN {scenario}: {name} clipped at {textScale:P0}, minimum={minimum}.");
                    }
                    bool restore = scenario.StartsWith("restore", StringComparison.Ordinal);
                    bool available = scenario is not ("blocked" or "restart" or "downloading" or "restore-blocked");
                    if (window.FindName("ApplyButton") is not null || window.FindName("ReviewProfileButton") is not null)
                        throw new InvalidDataException("SAAMAN normal actions must not reintroduce a second review/apply step.");
                    if (((FrameworkElement)window.FindName("ReviewPanel")).Visibility != (restore ? Visibility.Visible : Visibility.Collapsed) ||
                        ((FrameworkElement)window.FindName("ConfirmChange")).Visibility != (restore ? Visibility.Visible : Visibility.Collapsed))
                        throw new InvalidDataException("Only restoring an earlier DLL requires the review panel and confirmation.");
                    if (((Button)window.FindName("InstallButton")).IsEnabled != available || ((CheckBox)window.FindName("ProfileEnabledCheck")).IsEnabled != available)
                        throw new InvalidDataException("SAAMAN mutations must follow the detected process, restart and busy guards.");
                    if (((Button)window.FindName("ChooseProfileButton")).IsEnabled != (scenario == "install"))
                        throw new InvalidDataException("A managed enabled profile cannot be silently switched.");
                    var confirm = (CheckBox)window.FindName("ConfirmChange");
                    var restoreButton = (Button)window.FindName("RestoreButton");
                    if (restoreButton.IsEnabled) throw new InvalidDataException("Restore must start unconfirmed.");
                    if (restore)
                    {
                        if (((TextBlock)window.FindName("ConfirmChangeText")).Text != "I have reviewed this restore.")
                            throw new InvalidDataException("Restore confirmation must describe the restore.");
                        confirm.IsChecked = true;
                        if (restoreButton.IsEnabled != available) throw new InvalidDataException("Restore confirmation must not bypass process guards.");
                        confirm.IsChecked = false;
                        restoreButton.BringIntoView(); LayoutValidation.CheckWindowContent(window);
                    }
                    if (scenario is "blocked" or "restore-blocked" or "restart" && ((TextBlock)window.FindName("ProcessBlockText")).Visibility != Visibility.Visible)
                        throw new InvalidDataException("Blocked SAAMAN actions must explain the actual guard.");
                    if (output != null && textScale == 1 && !minimum)
                        LayoutSnapshots.SaveSaamanWindow(window, Path.Combine(output, $"{(dark ? "dark" : "light")}-saaman-{scenario}.png"));
                    count++;
                }
                finally { window.Close(); }
            }
            UiFixture.SetTextScale(1);
            UiFixture.Minimum = false;
            UiFixture.Blank = false;
            UiFixture.Scale = 1;
            var main = new MainWindow { Width = 1000, Height = 620, Tag = "Compact" };
            try
            {
                var rows = ((ItemsControl)main.FindName("AppList")).Items.Cast<CheckResult>().ToArray();
                var row = rows.Single(r => r.HasSaamanManagement);
                var launchpad = rows.Single(r => r.HasSelfUpdate);
                launchpad.InstalledVersion = "2.5.0 preview";
                launchpad.LatestVersion = "2.4.0";
                launchpad.ShowSelfUpdateAction = false;
                launchpad.SelfUpdateSummary = "Development preview";
                main.FocusSyntheticRow(row);
                LayoutValidation.CheckWindowContent(main);
                if (row.ShowLaunch || row.ShowRunningActions) throw new InvalidDataException("SAAMAN must not expose a standalone process action.");
                main.ShowSyntheticWorkspace("Setup");
                main.SetupWorkspace.ShowSection("Applications");
                LayoutValidation.CheckWindowContent(main);
                if (main.SetupWorkspace.SelectedSection != "Applications") throw new InvalidDataException("SAAMAN configuration must remain reachable in Setup.");
                main.ShowSyntheticWorkspace("Apps");
                LayoutValidation.CheckWindowContent(main);
                var originalStatus = row.Status;
                foreach (var scenario in new[]
                {
                    (State: new SaamanOperationState("Downloading SAAMAN.dll…", Busy: true, Percent: 64, CanCancel: true),
                        Text: "Updating 64%", Action: CheckRowAction.SaamanCancel),
                    (State: new SaamanOperationState("Applying the verified DLL…", Busy: true),
                        Text: "Updating…", Action: CheckRowAction.None),
                    (State: new SaamanOperationState("The download failed. The installed DLL is unchanged.", Warning: true),
                        Text: "Needs attention", Action: CheckRowAction.SaamanManage),
                    (State: new SaamanOperationState("SAAMAN updated. Its recovery copy is available in Activity & recovery."),
                        Text: "Updated", Action: CheckRowAction.None)
                })
                {
                    // A local version refresh must not hide an operation failure or change tabs.
                    row.Status = CheckStatus.UpToDate;
                    row.SoftwareActionsAllowed = row.ApplicationActionsAllowed = !scenario.State.Busy;
                    row.SaamanOperation = scenario.State;
                    LayoutValidation.CheckWindowContent(main);
                    if (((FrameworkElement)main.FindName("AppsWorkspace")).Visibility != Visibility.Visible)
                        throw new InvalidDataException("SAAMAN update feedback must remain in the Apps view.");
                    var list = (ItemsControl)main.FindName("AppList");
                    var controls = LayoutValidation.Descendants(list).OfType<FrameworkElement>()
                        .Where(control => ReferenceEquals(control.DataContext, row)).ToArray();
                    var status = controls.OfType<TextBox>().Single(control => control.Name == "RowStatusMessage");
                    var action = controls.OfType<Button>().Single(control => control.Name == "RowActionButton");
                    var progress = controls.OfType<ProgressBar>().Single(control =>
                        System.Windows.Data.BindingOperations.GetBinding(control, ProgressBar.ValueProperty)?.Path?.Path == nameof(CheckResult.RowProgressValue));
                    if (status.Text != scenario.Text || row.RowAction != scenario.Action ||
                        action.Visibility != (scenario.Action == CheckRowAction.None ? Visibility.Collapsed : Visibility.Visible) ||
                        action.IsEnabled != (scenario.Action != CheckRowAction.None))
                        throw new InvalidDataException("SAAMAN row actions or status do not match the active operation.");
                    if (scenario.Action == CheckRowAction.SaamanCancel && !Equals(action.Content, "Cancel") ||
                        scenario.Action == CheckRowAction.SaamanManage && !Equals(action.Content, "Retry"))
                        throw new InvalidDataException("SAAMAN must expose Cancel or Retry directly in its Apps row.");
                    if (progress.Visibility != (scenario.State.Busy ? Visibility.Visible : Visibility.Collapsed) ||
                        progress.IsIndeterminate != (scenario.State.Busy && !scenario.State.Percent.HasValue) ||
                        progress.Value != (scenario.State.Percent ?? 0))
                        throw new InvalidDataException("SAAMAN row progress does not match the active operation.");
                    if (System.Windows.Automation.AutomationProperties.GetHelpText(status).Contains(scenario.State.Message, StringComparison.Ordinal) != true)
                        throw new InvalidDataException("SAAMAN operation details must remain accessible from the row status.");
                    count++;
                }
                row.SaamanOperation = null;
                row.Status = originalStatus;
                row.SoftwareActionsAllowed = row.ApplicationActionsAllowed = true;
                LayoutValidation.CheckWindowContent(main);
                if (output != null) LayoutSnapshots.SaveSaamanWindow(main, Path.Combine(output, $"{(dark ? "dark" : "light")}-saaman-main.png"));
                count++;
            }
            finally { main.Close(); }
            var profile = new AppConfigWindow();
            try
            {
                ((TextBox)profile.ProfileEditor.FindName("TargetPathText")).Text = @"C:\Synthetic\EuroScope";
                ((TextBlock)profile.ProfileEditor.FindName("SyncText")).Text = "Saved profile differs from EuroScope. Preview before applying.";
                if (profile.ProfileEditor.FindName("PatchVatEfsBox") is not null) throw new InvalidDataException("Plugin choices belong in Setup Applications, not controller identity.");
                LayoutValidation.CheckWindowContent(profile);
                ((ScrollViewer)profile.ProfileEditor.FindName("ProfileFormScroll")).ScrollToBottom();
                if (output != null) LayoutSnapshots.SaveSaamanWindow(profile, Path.Combine(output, $"{(dark ? "dark" : "light")}-saaman-profile.png"));
                count++;
            }
            finally { profile.Close(); }
            var wizard = new SetupWizardWindow();
            try
            {
                wizard.SyntheticNext(); wizard.SyntheticNext(); wizard.SyntheticNext();
                LayoutValidation.CheckWindowContent(wizard);
                if (output != null) LayoutSnapshots.SaveSaamanWindow(wizard, Path.Combine(output, $"{(dark ? "dark" : "light")}-saaman-setup.png"));
                count++;
            }
            finally { wizard.Close(); }
        }
        return $"{count} SAAMAN layouts passed; production XAML with synthetic data, no windows shown.";
    }
}

internal static partial class LayoutSnapshots
{
    internal static void SaveSaamanWindow(Window window, string path) => Save(window, path);
}
