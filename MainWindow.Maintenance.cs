using System.Windows;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    private bool TryAcquireMaintenanceGuard(out IDisposable? lease)
    {
        if (MaintenanceLock.TryAcquire(out lease)) return true;
        LastCheckedText.Text = "Another Launchpad operation or removal review is active. Finish it first.";
        return false;
    }

    private void Maintenance_Click(object sender, RoutedEventArgs e)
    {
        if (!CanRunSoftwareAction() || !CanRunSelfUpdateAction()) return;
        if (SaamanNeedsSaveRetry || _vatEfsNeedsSaveRetry)
        {
            if (!SettingsService.TrySave(_settings))
            {
                SetupWorkspace.ShowSection("Applications");
                ShowWorkspace("Setup");
                SetupWorkspace.ShowNotice("Plugin changes are applied, but their choices have not been saved. Retry saving before removing or resetting applications.");
                return;
            }
            if (SaamanNeedsSaveRetry) _pluginFeedback.Remove("SAAMAN");
            if (_vatEfsNeedsSaveRetry) _pluginFeedback.Remove("VatEFS");
            SaamanNeedsSaveRetry = _vatEfsNeedsSaveRetry = false;
        }
        _processTimer.Stop();
        try
        {
            var window = new MaintenanceWindow(_settings) { Owner = this };
            ShowOwnedDialog(window);
            _setupRestartRequired |= window.RestartRequired;
            if (window.MadeChanges)
            {
                // Reload after reset so a subsequent preference write cannot resurrect removed settings.
                _settings = SettingsService.Load();
                _pluginFeedback.Clear();
                _vatEfsNeedsSaveRetry = SaamanNeedsSaveRetry = SaamanNeedsRuntime = false;
                if (window.ControllerProfileReset) DiscardControllerProfileDraft();
                RefreshActivityNotice();
                _isDarkMode = _settings.IsDarkMode;
                ApplyDensity();
                App.SetTheme(_isDarkMode);
                ApplyPresentationPreferences();
                ThemeToggleButton.Content = _isDarkMode ? "☀" : "☽";
                ApplyLaunchPaths();
                RefreshEuroscopeProfiles();
                ResetSoftwareChecks();
                UpdateAppConfigButton();
                LastCheckedText.Text = "Removal finished or stopped. Check the result before reinstalling or updating.";
            }
        }
        finally
        {
            UpdateSelfUpdateActionEnabled();
            if (_setupRestartRequired) LastCheckedText.Text = "Restart Windows before further installation, removal or profile changes. Keep the recovery export.";
            if (!_windowClosed) _processTimer.Start();
        }
    }
}
