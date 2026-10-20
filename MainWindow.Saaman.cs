using System.Diagnostics;
using System.IO;
using System.Windows;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    private readonly SaamanReleaseService _saamanReleases = new();
    private const string SaamanGuideUrl = "https://github.com/Kaskobiii/SAAMAN/blob/main/docs/controller-guide.md";

    private async Task CheckSaamanAsync()
    {
        var row = SaamanRow;
        row.Status = CheckStatus.Checking;
        row.ObservedLocalVersion = null;
        row.InstalledVersion = "—";
        row.LatestVersion = "—";
        row.LatestIsPrerelease = false;
        string path = _settings.SaamanDllPath;
        try
        {
            SaamanDllInfo? installed = string.IsNullOrWhiteSpace(path) || !File.Exists(path)
                ? null : await Task.Run(() => SaamanInstallService.InspectDll(path), _windowLifetime.Token);
            if (_windowClosed) return;
            row.ObservedLocalVersion = installed?.Version;
            row.InstalledVersion = installed is null ? "Not installed" : "v" + installed.Version;
            var release = await _saamanReleases.GetLatestAsync(_windowLifetime.Token);
            if (_windowClosed) return;
            row.LatestVersion = release is null ? "—" : "v" + release.Version;
            row.DownloadUrl = release?.ReleaseUrl ?? SaamanReleaseService.ReleasesUrl;
            row.Status = installed is null ? CheckStatus.NotConfigured :
                release is null ? CheckStatus.Installed :
                SoftwareVersion.TryParse(installed.Version, out var current) &&
                SoftwareVersion.TryParse(release.Version, out var latest) && current.CompareTo(latest) < 0
                    ? CheckStatus.UpdateAvailable : CheckStatus.UpToDate;
            row.StatusMessage = installed is null
                ? "Install SAAMAN or choose an existing SAAMAN.dll. It runs inside EuroScope."
                : "Installed SAAMAN " + installed.Version + ". It runs inside EuroScope.";
            row.StatusMessage += _settings.PatchSaaman
                ? "\n\nSAAMAN profile integration is selected. Profile options previews the target, plugin order and MAESTRO replacement."
                : "\n\nProfile integration is optional. Choose Profile options to enable SAAMAN first in APP/ACC and replace MAESTRO.";
            if (release is null) row.StatusMessage += "\n\nNo published stable release is available.";
        }
        catch (OperationCanceledException) when (_windowClosed) { }
        catch (Exception)
        {
            if (_windowClosed) return;
            row.Status = CheckStatus.Error;
            row.StatusMessage = "SAAMAN could not be checked. Open Set up / update to review the DLL and retry the official release check.";
        }
    }

    private async void SaamanManage_Click(object sender, RoutedEventArgs e)
    {
        if (!CanRunSoftwareAction() || !CanRunSelfUpdateAction()) return;
        _softwareActionBusy = true;
        _processTimer.Stop();
        UpdateSelfUpdateActionEnabled();
        try
        {
            // Each mutation takes its own cross-process lease and revalidates the preview.
            var window = new SaamanWindow(_settings.Copy()) { Owner = this };
            ShowOwnedDialog(window);
            _setupRestartRequired |= window.RequiresRestart;
            if (window.SettingsChanged)
            {
                _settings.SaamanDllPath = window.Settings.SaamanDllPath;
                _settings.SaamanProfilePath = window.Settings.SaamanProfilePath;
                _settings.PatchSaaman = window.Settings.PatchSaaman;
                _settings.SaamanProfileManaged = window.Settings.SaamanProfileManaged;
                ApplicationPreferences.SetNotUsed(_settings, "SAAMAN", false);
                SaveSessionSettings("SAAMAN choices saved. Profile integration is reviewed separately.");
            }
            await CheckSaamanAsync();
        }
        catch (Exception ex) { LastCheckedText.Text = "SAAMAN setup could not finish: " + ex.Message; }
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

    private void SaamanGuide_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(SaamanGuideUrl) { UseShellExecute = true }); }
        catch { SaamanRow.StatusMessage = "The controller guide could not open. It is available in the SAAMAN GitHub repository."; }
    }
}
