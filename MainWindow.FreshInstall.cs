using System.Windows;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    private void SoftwareInstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CheckResult { SoftwareApp: { } app } } ||
            !CanRunSoftwareAction() || !CanRunSelfUpdateAction() || !TryAcquireMaintenanceGuard(out var lease)) return;
        bool adopt = false, remove = false;
        string? failureMessage = null;
        _processTimer.Stop();
        try
        {
            using (lease)
            {
                var window = new FreshSoftwareInstallWindow(app, _settings) { Owner = this };
                ShowOwnedDialog(window);
                _setupRestartRequired |= window.RestartRequired;
                adopt = window.AdoptionRequested && !_setupRestartRequired;
                remove = window.RemovalRequested && !_setupRestartRequired;
                if (window.Result is { } result)
                {
                    switch (app)
                    {
                        case SoftwareApp.Vacs: _settings.VacsExePath = result.ExecutablePath; break;
                        case SoftwareApp.Vatis: _settings.VatisExePath = result.ExecutablePath; break;
                        case SoftwareApp.TrackAudio: _settings.TrackAudioExePath = result.ExecutablePath; break;
                    }
                    try
                    {
                        SettingsService.Save(_settings);
                        string name = app switch { SoftwareApp.Vacs => "VACS", SoftwareApp.Vatis => "vATIS", _ => "TrackAudio" };
                        LastCheckedText.Text = name + " installation verified. The application remains closed. Check for updates to refresh versions.";
                    }
                    catch { LastCheckedText.Text = failureMessage = "Installation succeeded, but its path could not be saved. Set it in Settings."; }
                }
                ApplyLaunchPaths(); ResetSoftwareChecks();
            }
        }
        catch (Exception ex) { LastCheckedText.Text = failureMessage = "Application setup could not finish: " + ex.Message; }
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
        if (!_windowClosed && adopt) OpenSettings(discover: true);
        else if (!_windowClosed && remove) Maintenance_Click(sender, e);
    }
}
