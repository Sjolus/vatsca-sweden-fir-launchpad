using System.IO;
using System.Windows;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    private void SoftwareLocation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CheckResult row } || !CanRunSoftwareAction()) return;
        if (row.SoftwareNeedsSetup) { SoftwareInstall_Click(sender, e); return; }
        SetupWorkspace.ShowSection("Applications");
        ShowWorkspace("Setup");
        SetupWorkspace.ShowNotice("Choose Location beside " + row.DisplayName + " to select a different copy.");
    }

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
                    var previous = _settings.Copy();
                    switch (app)
                    {
                        case SoftwareApp.Vacs: _settings.VacsExePath = result.ExecutablePath; break;
                        case SoftwareApp.Vatis: _settings.VatisExePath = result.ExecutablePath; break;
                        case SoftwareApp.TrackAudio: _settings.TrackAudioExePath = result.ExecutablePath; break;
                        case SoftwareApp.VatEfs: _settings.VatEfsPath = Path.GetDirectoryName(result.ExecutablePath)!; break;
                    }
                    if (app != SoftwareApp.VatEfs)
                        ApplicationPreferences.SetNotUsed(_settings, app switch
                        { SoftwareApp.Vacs => "VACS", SoftwareApp.Vatis => "vATIS", _ => "TrackAudio" }, false);
                    try
                    {
                        SettingsService.Save(_settings);
                        string name = app switch { SoftwareApp.Vacs => "VACS", SoftwareApp.Vatis => "vATIS", SoftwareApp.VatEfs => "VatEFS", _ => "TrackAudio" };
                        LastCheckedText.Text = name + " installation verified.";
                    }
                    catch { LastCheckedText.Text = failureMessage = "Installation succeeded, but its location could not be saved. Choose it under Setup → Applications."; }
                    ApplyLaunchPaths(); ResetChangedSoftwareChecks(previous);
                    var row = _results.Single(r => r.SoftwareApp == app);
                    row.SoftwareUpdate = null;
                    row.Status = CheckStatus.Installed;
                    row.StatusMessage = "Installation completed and was verified. " + window.ConfigurationMessage +
                        " Check for updates to compare with the latest release.";
                    if (window.ConfigurationMessage.Length > 0)
                    {
                        LastCheckedText.Text = (failureMessage == null ? "" : failureMessage + " ") + window.ConfigurationMessage;
                        SetupWorkspace.ShowNotice(LastCheckedText.Text);
                    }
                    try
                    {
                        var installed = new SoftwareInstaller().Inspect(app, result.ExecutablePath);
                        row.InstalledVersion = string.IsNullOrWhiteSpace(installed.Version) ? "Installed" : "v" + installed.Version;
                    }
                    catch { row.InstalledVersion = "Installed"; }
                }
                ApplyLaunchPaths(); ApplyApplicationUsage(); UpdateAppConfigButton(); RefreshWorkspace();
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
        if (!_windowClosed && adopt) DiscoverInstallations_Click(sender, e);
        else if (!_windowClosed && remove) Maintenance_Click(sender, e);
    }
}
