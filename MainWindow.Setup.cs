using System.Windows;
using System.Windows.Automation;
using VatscaUpdateChecker.Services;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    private async void SetupWizard_Click(object sender, RoutedEventArgs e) => await OpenSetupWizard();

    private async Task OpenSetupWizard()
    {
        if (!CanRunSoftwareAction() || !CanRunSelfUpdateAction() || !TryAcquireMaintenanceGuard(out var lease)) return;
        bool removalRequested = false, finished = false, saved = true, workflowFailed = false;
        ReportingEvents.Start(ReportingApplication.Launchpad, ReportingOperation.Setup);
        ReportingEvents.Feature(ReportingFeature.SetupGuide);
        var state = new Models.SetupWizardState(_settings);
        _softwareActionBusy = true;
        _processTimer.Stop();
        try
        {
            while (!_windowClosed)
            {
                bool gngRequested;
                using (lease)
                {
                    var wizard = new SetupWizardWindow(state) { Owner = this };
                    ShowOwnedDialog(wizard);
                    _settings = wizard.Settings;
                    _setupRestartRequired |= wizard.RestartRequired;
                    removalRequested = wizard.RemovalRequested && !_setupRestartRequired;
                    gngRequested = wizard.GngSetupRequested && !_setupRestartRequired;
                    finished = wizard.Finished;
                    _settings.SetupWizardCompleted = finished;
                    _settings.SetupWizardDismissed = !finished && !gngRequested;
                    saved = SettingsService.TrySave(_settings);
                }
                lease = null;
                if (!gngRequested || _windowClosed) break;

                // The package service owns its mutation lease. Release the wizard lease
                // before entering GNG, then reacquire it before profile/font actions.
                await OpenWizardGng(state);
                _settings = state.Dismiss();
                saved &= SettingsService.TrySave(_settings);
                if (_windowClosed || !TryAcquireMaintenanceGuard(out lease)) break;
            }

            _isDarkMode = _settings.IsDarkMode;
            App.SetTheme(_isDarkMode);
            ThemeToggleButton.Content = _isDarkMode ? "☀" : "☽";
            ApplyDensity();
            ApplyPresentationPreferences();
            ApplyLaunchPaths();
            RefreshEuroscopeProfiles();
            ResetSoftwareChecks();
            UpdateAppConfigButton();
            AppList.ItemsSource = null;
            AppList.ItemsSource = _results;
            LastCheckedText.Text = !saved
                ? "Your choices are available this session, but could not be saved. Open Settings and try saving again."
                : _setupRestartRequired
                    ? "Restart Windows before further installation or controller-profile changes."
                    : finished
                        ? "Setup choices saved. Reopen Setup any time to review a skipped step."
                        : "Your place in setup and completed actions are kept. Reopen Setup guide to continue.";
        }
        catch (Exception ex)
        {
            workflowFailed = true;
            _settings = state.Dismiss();
            saved = SettingsService.TrySave(_settings);
            ReportingEvents.RecordFailure(ex, ReportingApplication.Launchpad, ReportingOperation.Setup);
            LastCheckedText.Text = "Setup could not finish: " + ex.Message;
        }
        finally
        {
            ReportingService.RecordUsage(ReportingEventName.OperationFinished, new ReportingDetails
            { OperationId = ReportingEvents.CurrentOperationId(ReportingApplication.Launchpad, ReportingOperation.Setup),
                Application = ReportingApplication.Launchpad, Operation = ReportingOperation.Setup,
                Outcome = workflowFailed || !saved ? ReportingOutcome.Failed : finished ? ReportingOutcome.Completed : ReportingOutcome.Unknown,
                DurationBucket = ReportingEvents.Finish(ReportingApplication.Launchpad, ReportingOperation.Setup) });
            if (!saved) ReportingService.RecordError(ReportingErrorCode.SettingsSaveFailed,
                new ReportingDetails { Application = ReportingApplication.Launchpad, Operation = ReportingOperation.Save });
            lease?.Dispose();
            _softwareActionBusy = false;
            if (!_windowClosed)
            {
                _processTimer.Start();
                UpdateSelfUpdateActionEnabled();
                ReportPresentationPreferences();
            }
        }
        if (removalRequested && !_windowClosed) Maintenance_Click(this, new RoutedEventArgs());
    }

    private async Task OpenWizardGng(Models.SetupWizardState state)
    {
        string root = state.Draft.EuroscopeDataPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            string defaultRoot = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EuroScope");
            var picker = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Choose the EuroScope data folder for the Swedish GNG package",
                InitialDirectory = System.IO.Directory.Exists(defaultRoot) ? defaultRoot : System.IO.Path.GetDirectoryName(defaultRoot)!,
                Multiselect = false
            };
            if (picker.ShowDialog(this) != true) return;
            root = picker.FolderName;
        }

        var window = new GngUpdateWindow(root, state.Draft.LastEuroscopeProfile,
            GngProtectedPaths.ForUpdate(state.Draft), CleanupProtectedPaths()) { Owner = this };
        ShowOwnedDialog(window);
        if (window.RestartRequired)
        {
            _setupRestartRequired = true;
            state.RequireRestart();
        }
        if (window.InstallationCompleted)
        {
            state.AcceptGngFolder(window.DataFolder, installed: true);
            state.MoveTo(Models.SetupWizardStep.Profile);
        }
        if (window.AttemptedChanges)
        {
            var fonts = FontService.Check(state.Draft.EuroscopeDataPath);
            _results[1].FontsState = fonts.State;
            _results[1].FontsTooltip = fonts.Tooltip;
            await UpdateChecker.CheckGng(_results[1], state.Draft.EuroscopeDataPath);
        }
    }

    private void ApplyDensity()
    {
        Tag = _settings.CompactLayout ? "Compact" : "Comfortable";
        DensityToggleButton.Content = _settings.CompactLayout ? "Compact" : "Expanded";
        DensityToggleButton.ToolTip = _settings.CompactLayout ? "Show short explanations in each row" : "Use compact rows with details below";
        AutomationProperties.SetName(DensityToggleButton, $"Layout: {DensityToggleButton.Content}. {DensityToggleButton.ToolTip}.");
        UpdateApplicationDetailsLayout();
    }

    private void DensityToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!TryAcquireMaintenanceGuard(out var lease)) return;
        using var guard = lease;
        _settings.CompactLayout = !_settings.CompactLayout;
        ApplyDensity();
        ReportingService.RecordUsage(ReportingEventName.PreferenceChanged, new ReportingDetails
        { Application = ReportingApplication.Launchpad, Source = _settings.CompactLayout ? ReportingSource.Compact : ReportingSource.Expanded });
        ReportPresentationPreferences();
        try { SettingsService.Save(_settings); }
        catch { LastCheckedText.Text = "Layout changed for this session, but the preference could not be saved."; }
    }
}
