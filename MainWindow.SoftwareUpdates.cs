using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.IO;
using System.Windows;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    // Redirects are followed only after the update service validates the destination.
    private readonly HttpClient _softwareHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromMinutes(20)
    };
    private readonly SoftwareUpdateService _softwareUpdates;
    private bool _softwareActionBusy;

    private async Task CheckSoftwareAsync()
    {
        foreach (var row in _results.Where(r => r.HasSoftwareUpdate))
        {
            _windowLifetime.Token.ThrowIfCancellationRequested();
            await _softwareUpdates.CheckAsync(row.SoftwareApp!.Value, SoftwareExecutablePath(row), _windowLifetime.Token);
        }
    }

    private static string SoftwareExecutablePath(CheckResult row) => row.IsLocalUrl ? row.SoftwareExecutablePath : row.LaunchPath;

    private void ResetSoftwareChecks()
    {
        foreach (var row in _results.Where(r => r.HasSoftwareUpdate))
        {
            row.SoftwareUpdate = null;
            row.LatestIsPrerelease = false;
            row.Status = CheckStatus.Unknown;
            row.StatusMessage = "Check for updates after changing application paths.";
            row.InstalledVersion = row.LatestVersion = "—";
        }
        UpdateSoftwareActions();
    }

    private void SoftwareUpdate_StateChanged(SoftwareUpdateState state)
    {
        if (_windowClosed) return;
        if (Dispatcher.CheckAccess()) ApplySoftwareState(state);
        else Dispatcher.InvokeAsync(() => { if (!_windowClosed) ApplySoftwareState(state); });
    }

    private void ApplySoftwareState(SoftwareUpdateState state)
    {
        _setupRestartRequired |= _softwareUpdates.RestartRequired;
        var row = _results.Single(r => r.SoftwareApp == state.App);
        if (state.App == SoftwareApp.VatEfs) row.SoftwareBlockingReason = VatEfsInstaller.GetBlockingReason();
        row.SoftwareUpdate = state;
        row.InstalledVersion = FormatLaunchpadVersion(state.InstalledVersion);
        row.LatestVersion = FormatLaunchpadVersion(state.LatestVersion);
        row.LatestIsPrerelease = state.Release?.IsPrerelease == true;
        row.StatusMessage = state.Message;
        row.Status = state.Phase switch
        {
            SoftwareUpdatePhase.Available => CheckStatus.UpdateAvailable,
            SoftwareUpdatePhase.Completed or SoftwareUpdatePhase.Current => CheckStatus.UpToDate,
            SoftwareUpdatePhase.Error => CheckStatus.Error,
            SoftwareUpdatePhase.Unavailable => CheckStatus.Unsupported,
            _ when state.IsBusy => CheckStatus.Checking,
            _ => CheckStatus.Unknown
        };
        UpdateSelfUpdateActionEnabled();
    }

    private bool CanRunSoftwareAction() => !_setupRestartRequired && !_windowClosed && !_isChecking && !_softwareActionBusy &&
        !_softwareUpdates.IsBusy && !_selfUpdateActionBusy && _openDialogCount == 0 && IsEnabled &&
        !System.Windows.Interop.ComponentDispatcher.IsThreadModal && OwnedWindows.Count == 0 &&
        _launchpadUpdates.State.Status is not (LaunchpadUpdateStatus.Checking or LaunchpadUpdateStatus.Downloading);

    private void UpdateSoftwareActions()
    {
        RestartNotice.Visibility = _setupRestartRequired ? Visibility.Visible : Visibility.Collapsed;
        var allowed = CanRunSoftwareAction();
        foreach (var row in _results.Where(r => r.HasSoftwareUpdate || r.HasEuroScopeManagement || r.HasFontsCheck || r.IsLocalUrl)) row.SoftwareActionsAllowed = allowed;
        var busy = _isChecking || _softwareActionBusy || _softwareUpdates.IsBusy;
        AppConfigButton.IsEnabled = !busy && !_setupRestartRequired;
        SettingsButton.IsEnabled = !busy;
        CheckButton.IsEnabled = !_isChecking && !busy;
        MaintenanceButton.IsEnabled = CanRunSoftwareAction() && CanRunSelfUpdateAction();
        SetupButton.IsEnabled = CanRunSoftwareAction() && CanRunSelfUpdateAction();
    }

    private async void SoftwareUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CheckResult row } || row.SoftwareApp is not { } app ||
            !CanRunSoftwareAction()) return;

        if (row.SoftwareNeedsSetup)
        {
            SoftwareInstall_Click(sender, e);
            return;
        }

        if (app == SoftwareApp.VatEfs && row.SoftwareUpdate?.CanUpdate == true &&
            VatEfsInstaller.GetBlockingReason() is { } blockingReason)
        {
            row.SoftwareBlockingReason = blockingReason;
            return;
        }
        if (!TryAcquireMaintenanceGuard(out var lease)) return;
        using var guard = lease;

        if (row.SoftwareUpdate?.Phase == SoftwareUpdatePhase.Unavailable)
        {
            OpenSoftwareDownloads(row, app);
            return;
        }

        _softwareActionBusy = true;
        UpdateSelfUpdateActionEnabled();
        try
        {
            if (row.SoftwareUpdate?.CanUpdate == true)
                await _softwareUpdates.UpdateAsync(app, _windowLifetime.Token);
            else
                await _softwareUpdates.CheckAsync(app, SoftwareExecutablePath(row), _windowLifetime.Token);

            var state = _softwareUpdates.GetState(app);
            ApplySoftwareState(state);
            if (state.Phase == SoftwareUpdatePhase.Completed && state.Installation is { } installed)
            {
                if (!row.IsLocalUrl) row.LaunchPath = installed.ExePath;
                else row.SoftwareExecutablePath = installed.ExePath;
                switch (app)
                {
                    case SoftwareApp.Vacs: _settings.VacsExePath = installed.ExePath; break;
                    case SoftwareApp.Vatis: _settings.VatisExePath = installed.ExePath; break;
                    case SoftwareApp.TrackAudio: _settings.TrackAudioExePath = installed.ExePath; break;
                    case SoftwareApp.VatEfs: _settings.VatEfsPath = Path.GetDirectoryName(installed.ExePath)!; break;
                }
                try { SettingsService.Save(_settings); }
                catch { row.StatusMessage = "The update succeeded, but the application path could not be saved. Set the path in Settings before reopening Launchpad."; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            row.StatusMessage = "The update could not finish: " + ex.Message;
        }
        finally
        {
            _setupRestartRequired |= _softwareUpdates.RestartRequired;
            _softwareActionBusy = false;
            if (!_windowClosed)
            {
                UpdateSelfUpdateActionEnabled();
                if (_setupRestartRequired) LastCheckedText.Text = "Restart Windows before further installation, removal or profile changes.";
            }
        }
    }

    private void SoftwareCancel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CheckResult { SoftwareApp: { } app } })
            _softwareUpdates.Cancel(app);
    }

    private void SoftwareDownloads_Click(object sender, RoutedEventArgs e)
    {
        if (CanRunSoftwareAction() && sender is FrameworkElement { DataContext: CheckResult row } && row.SoftwareApp is { } app)
            OpenSoftwareDownloads(row, app);
    }

    private static void OpenSoftwareDownloads(CheckResult row, SoftwareApp app)
    {
        var url = app switch
        {
            SoftwareApp.Vacs => "https://github.com/vacs-project/vacs/releases",
            SoftwareApp.Vatis => "https://vatis.app/",
            SoftwareApp.VatEfs => "https://github.com/minsulander/vatefs/releases",
            _ => "https://github.com/pierr3/TrackAudio/releases"
        };
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { row.StatusMessage = "Could not open the vendor's download page. Try again."; }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Do not abandon a vendor installer or its post-install verification. The user can minimize.
        if (_softwareActionBusy || _softwareUpdates.IsBusy || _openDialogCount > 0)
        {
            e.Cancel = true;
            LastCheckedText.Text = "Finish the current operation and close its dialog before closing Launchpad. You can minimize it.";
        }
        base.OnClosing(e);
    }
}
