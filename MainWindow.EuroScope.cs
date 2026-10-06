using System.Windows;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    private async void EuroScopeManage_Click(object sender, RoutedEventArgs e)
    {
        if (!CanRunSoftwareAction() || !CanRunSelfUpdateAction() || !TryAcquireMaintenanceGuard(out var lease)) return;
        bool remove = false, adopt = false;
        string? failureMessage = null;
        _processTimer.Stop();
        try
        {
            using (lease)
            {
                var window = new EuroScopeInstallWindow(_settings) { Owner = this };
                ShowOwnedDialog(window);
                _setupRestartRequired |= window.RestartRequired;
                remove = window.RemovalRequested && !_setupRestartRequired;
                adopt = window.AdoptionRequested && !_setupRestartRequired;
                if (window.Result is { InstallationCompleted: true } result)
                {
                    _settings.EuroscopeExePath = result.ExecutablePath;
                    try { SettingsService.Save(_settings); }
                    catch { LastCheckedText.Text = failureMessage = "EuroScope installation succeeded, but its path could not be saved. Set it in Settings."; }
                }
                // Failed replacement can still have removed the previous executable/data.
                // Refresh launch visibility and profile choices on every outcome.
                ApplyLaunchPaths();
                RefreshEuroscopeProfiles();
                await UpdateChecker.CheckEuroscope(_results[0], _settings.EuroscopeExePath);
            }
        }
        catch (Exception ex) { LastCheckedText.Text = failureMessage = "EuroScope management could not finish: " + ex.Message; }
        finally
        {
            if (!_windowClosed)
            {
                _processTimer.Start();
                UpdateSelfUpdateActionEnabled();
                if (_setupRestartRequired)
                    LastCheckedText.Text = (failureMessage == null ? "" : failureMessage + " ") +
                        "Restart Windows before further installation or controller-profile changes.";
            }
        }
        if (adopt && !_windowClosed) OpenSettings(discover: true);
        else if (remove && !_windowClosed) Maintenance_Click(sender, e);
    }
}
