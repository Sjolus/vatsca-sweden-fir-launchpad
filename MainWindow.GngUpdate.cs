using System.IO;
using System.Windows;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    private async void GngUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!CanRunSoftwareAction() || !CanRunSelfUpdateAction()) return;
        _softwareActionBusy = true;
        UpdateSelfUpdateActionEnabled();
        _processTimer.Stop();
        try
        {
            var root = _settings.EuroscopeDataPath;
            if (string.IsNullOrWhiteSpace(root))
            {
                // This explicit setup action may prepare the standard folder. Only a
                // completed installation adopts it; existing selected folders are kept.
                root = GngSetupDestination.PrepareDefault();
            }

            // Installation/recovery acquire their own shared lease. Keeping a lease while
            // the user signs in would also prevent those operations from acquiring it.
            var window = new GngUpdateWindow(root, _settings.LastEuroscopeProfile,
                GngProtectedPaths.ForUpdate(_settings), CleanupProtectedPaths(), _requestedActivityRecovery?.RecoveryFolder,
                saamanSettings: _settings) { Owner = this };
            ShowOwnedDialog(window);
            _setupRestartRequired |= window.RestartRequired;
            bool saved = true;
            if (window.InstallationCompleted)
            {
                _settings.EuroscopeDataPath = window.DataFolder;
                saved = SettingsService.TrySave(_settings);
                ApplyLaunchPaths();
            }
            if (window.AttemptedChanges)
            {
                RefreshEuroscopeProfiles();
                var fonts = FontService.Check(_settings.EuroscopeDataPath);
                _results[1].FontsState = fonts.State;
                _results[1].FontsTooltip = fonts.Tooltip;
                UpdateAppConfigButton();
                await UpdateChecker.CheckGng(_results[1], _settings.EuroscopeDataPath);
            }
            LastCheckedText.Text = !saved
                ? "GNG was installed, but its folder could not be saved. Open Setup → Swedish GNG and save the EuroScope data folder before reopening Launchpad."
                : _setupRestartRequired
                    ? "Restart Windows before further installation, removal or profile changes."
                    : window.InstallationCompleted
                        ? "GNG installed. Check fonts and your Controller profile before opening EuroScope."
                        : "GNG setup closed. Follow any recovery instructions shown before trying another update.";
        }
        catch (Exception ex) { LastCheckedText.Text = "GNG setup could not finish: " + ex.Message; }
        finally
        {
            _softwareActionBusy = false;
            if (!_windowClosed)
            {
                _processTimer.Start();
                UpdateSelfUpdateActionEnabled();
            }
        }
    }
}
