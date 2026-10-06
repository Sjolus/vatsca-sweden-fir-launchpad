using System.Windows;
using System.Windows.Automation;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    private void SetupWizard_Click(object sender, RoutedEventArgs e) => OpenSetupWizard();

    private void OpenSetupWizard()
    {
        if (!CanRunSoftwareAction() || !CanRunSelfUpdateAction() || !TryAcquireMaintenanceGuard(out var lease)) return;
        bool removalRequested = false;
        _processTimer.Stop();
        try
        {
            using (lease)
            {
                var wizard = new SetupWizardWindow(_settings) { Owner = this };
                ShowOwnedDialog(wizard);
                // Saved child-dialog choices and verified installations are real actions even
                // when the user skips the rest of the wizard. Do not roll those choices back.
                _settings = wizard.Settings;
                _settings.SetupWizardCompleted |= wizard.Finished;
                _settings.SetupWizardDismissed = !wizard.Finished;
                _setupRestartRequired |= wizard.RestartRequired;
                removalRequested = wizard.RemovalRequested && !_setupRestartRequired;
                bool saved = true;
                try { SettingsService.Save(_settings); }
                catch { saved = false; }

                _isDarkMode = _settings.IsDarkMode;
                App.SetTheme(_isDarkMode);
                ThemeToggleButton.Content = _isDarkMode ? "☀" : "☽";
                ApplyDensity();
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
                        : wizard.Finished
                            ? "Setup choices saved. You can reopen Setup any time; applications stay closed."
                            : "Setup skipped. Any choices you explicitly saved are kept. Reopen Setup any time.";
            }
        }
        catch (Exception ex) { LastCheckedText.Text = "Setup could not finish: " + ex.Message; }
        finally
        {
            if (!_windowClosed)
            {
                _processTimer.Start();
                UpdateSelfUpdateActionEnabled();
            }
        }
        if (removalRequested && !_windowClosed) Maintenance_Click(this, new RoutedEventArgs());
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
        try { SettingsService.Save(_settings); }
        catch { LastCheckedText.Text = "Layout changed for this session, but the preference could not be saved."; }
    }
}
