using System.IO;
using System.Windows;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    private void GngCleanup_Click(object sender, RoutedEventArgs e)
    {
        if (!CanRunSoftwareAction() || !CanRunSelfUpdateAction()) return;
        _processTimer.Stop();
        try
        {
            // The dialog owns the local modal gate. Archive and restore take MaintenanceLock
            // inside the service so their filesystem validation shares the same lease.
            var window = new GngCleanupWindow(_settings.EuroscopeDataPath, _settings.LastEuroscopeProfile,
                CleanupProtectedPaths()) { Owner = this };
            ShowOwnedDialog(window);
            if (window.AttemptedChanges)
            {
                RefreshEuroscopeProfiles();
                var fonts = FontService.Check(_settings.EuroscopeDataPath);
                _results[1].FontsState = fonts.State;
                _results[1].FontsTooltip = fonts.Tooltip;
                UpdateAppConfigButton();
                LastCheckedText.Text = "GNG cleanup finished or stopped. Keep its backup and check the result before controlling.";
            }
        }
        catch (Exception ex) { LastCheckedText.Text = "GNG cleanup could not finish: " + ex.Message; }
        finally
        {
            if (!_windowClosed)
            {
                _processTimer.Start();
                UpdateSelfUpdateActionEnabled();
            }
        }
    }

    private IEnumerable<string> CleanupProtectedPaths()
    {
        yield return _settings.LastEuroscopeProfile;
        yield return _settings.VatEfsPath;
        foreach (var path in GngProtectedPaths.ForUpdate(_settings)) yield return path;
    }
}
