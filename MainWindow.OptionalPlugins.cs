using System.IO;
using System.Windows.Controls;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    private readonly Dictionary<string, SetupPluginFeedback> _pluginFeedback = new();
    private bool _vatEfsNeedsSaveRetry;

    private SetupWorkspaceApplication BuildSetupApplication(CheckResult row)
    {
        string key = row.AppName;
        string path = key == "SAAMAN" ? _settings.SaamanDllPath
            : ConfiguredPathValidationService.PathFor(PathKind(key), _settings);
        bool exists = key == "VatEFS" ? File.Exists(Path.Combine(path, "efs.exe")) : File.Exists(path);
        bool available = CanRunSoftwareAction() && CanRunSelfUpdateAction();
        string status = row.IsNotUsed ? "Not used" : string.IsNullOrWhiteSpace(path) ? "Not installed"
            : exists ? "Installed" : "Saved location missing";
        SetupPluginOption? plugin = null;
        if (key is "SAAMAN" or "VatEFS")
        {
            bool enabled = key == "SAAMAN" ? _settings.PatchSaaman : _settings.PatchVatEfs;
            string? blocked = _setupRestartRequired ? "Restart Windows before changing plugins."
                : EuroScopeProcessGuard.GetBlockingReason();
            bool needsGng = blocked is null && (string.IsNullOrWhiteSpace(_settings.EuroscopeDataPath) ||
                !Directory.Exists(_settings.EuroscopeDataPath) && !(key == "SAAMAN" && enabled));
            if (needsGng)
                blocked = "Set up the Swedish GNG folder first.";
            string help = key == "SAAMAN"
                ? "On: SAAMAN loads first in the APP/ACC profile, replacing MAESTRO. Off: restores MAESTRO. The DLL and personal settings are kept."
                : "On: adds VatEFS to your EuroScope profiles. Off: removes those plugin entries. The installed application and settings are kept.";
            if (key == "SAAMAN" && !string.IsNullOrWhiteSpace(_settings.SaamanProfilePath))
                help += "\nProfile: " + _settings.SaamanProfilePath;
            plugin = new(enabled, key == "SAAMAN" ? "Use in APP / ACC" : "Use in EuroScope", help,
                available && blocked is null, blocked, needsGng);
            status = enabled ? "Enabled" : "Off";
            if (!string.IsNullOrWhiteSpace(path) && !File.Exists(key == "VatEFS" ? Path.Combine(path, "VatEFS.dll") : path))
                status += " · DLL missing";
        }
        _pluginFeedback.TryGetValue(key, out var feedback);
        if (key == "SAAMAN" && feedback is not null)
            feedback = feedback with { CanCancel = SaamanCanCancel, CanRetrySave = SaamanNeedsSaveRetry, RuntimeRequired = SaamanNeedsRuntime };
        else if (key == "VatEFS" && feedback is not null)
            feedback = feedback with { CanRetrySave = _vatEfsNeedsSaveRetry };
        string action = key == "SAAMAN" ? exists ? "Update" : _settings.PatchSaaman ? "Repair" : ""
            : row.HasEuroScopeManagement ? exists ? "Manage…" : "Install…"
            : exists ? row.IsNotUsed ? "Show in Apps" : "Updates…" : "Install…";
        return new(key, row.DisplayName, path, status, action, row.IsNotUsed, plugin, feedback, available);
    }

    private void SetPluginStatus(string key, string message, bool warning = false, double? percent = null, bool busy = false)
    {
        if (_windowClosed) return;
        _pluginFeedback[key] = new(message, warning, busy, percent);
        LastCheckedText.Text = key + ": " + message.ReplaceLineEndings(" ");
        RefreshWorkspace();
    }

    private async Task SetVatEfsEnabledAsync(bool enabled)
    {
        if (!CanRunSoftwareAction() || !CanRunSelfUpdateAction()) return;
        if (enabled && !File.Exists(Path.Combine(_settings.VatEfsPath, "VatEFS.dll")))
        {
            // The standard installer keeps its own confirmation, prerequisite and recovery flow.
            SoftwareInstall_Click(new Button { DataContext = _results.Single(row => row.SoftwareApp == SoftwareApp.VatEfs) }, new());
            if (_windowClosed) return;
            if (_setupRestartRequired || !File.Exists(Path.Combine(_settings.VatEfsPath, "VatEFS.dll")))
            {
                SetPluginStatus("VatEFS", _setupRestartRequired ? "Restart Windows, then enable VatEFS."
                    : "VatEFS was not enabled. Install it or choose an existing copy.", true);
                return;
            }
        }
        if (!TryAcquireMaintenanceGuard(out var lease)) return;
        using var guard = lease;
        _softwareActionBusy = true;
        _processTimer.Stop();
        UpdateSelfUpdateActionEnabled();
        ReportingEvents.Start(ReportingApplication.Vatefs, ReportingOperation.ConfigurePluginProfile);
        SetPluginStatus("VatEFS", enabled ? "Enabling VatEFS…" : "Disabling VatEFS…", busy: true);
        try
        {
            var settings = _settings.Copy();
            var result = await Task.Run(() => VatEfsProfileService.Apply(settings, enabled));
            _settings.PatchVatEfs = enabled;
            ApplicationPreferences.SetNotUsed(_settings, "VatEFS", !enabled);
            _vatEfsNeedsSaveRetry = !SettingsService.TrySave(_settings);
            ActivityHistoryService.Record(ActivityApplication.VatEfs, ActivityOperation.Configure,
                _vatEfsNeedsSaveRetry ? ActivityOutcome.NeedsAttention : ActivityOutcome.Completed,
                recoveryFolder: result.RecoveryFolder, dataFolder: settings.EuroscopeDataPath);
            ReportingEvents.Complete(ReportingApplication.Vatefs, ReportingOperation.ConfigurePluginProfile,
                _vatEfsNeedsSaveRetry ? ReportingOutcome.Partial : ReportingOutcome.Completed,
                _vatEfsNeedsSaveRetry ? ReportingReason.SaveFailed : null);
            SetPluginStatus("VatEFS", (enabled ? "Enabled in EuroScope." : "Disabled in EuroScope. Installed files and settings kept.") +
                (_vatEfsNeedsSaveRetry ? " The profile changed, but Launchpad could not save this choice. Retry saving." : ""), _vatEfsNeedsSaveRetry);
            ApplyApplicationUsage();
            UpdateAppConfigButton();
        }
        catch (Exception ex)
        {
            RecordVatEfsProfileFailure(ex);
            SetPluginStatus("VatEFS", ex.Message, true);
        }
        finally
        {
            _softwareActionBusy = false;
            if (!_windowClosed) { _processTimer.Start(); UpdateSelfUpdateActionEnabled(); }
        }
    }

    private void RetryVatEfsSave()
    {
        if (!_vatEfsNeedsSaveRetry || !CanRunSoftwareAction()) return;
        _vatEfsNeedsSaveRetry = !SettingsService.TrySave(_settings);
        SetPluginStatus("VatEFS", _vatEfsNeedsSaveRetry ? "Launchpad still could not save this choice. Profile changes remain applied."
            : "Choice saved.", _vatEfsNeedsSaveRetry);
    }

    private void RecordVatEfsProfileFailure(Exception error, string? dataFolder = null)
    {
        if (error is VatEfsProfileRecoveryException recovery)
        {
            ReportingEvents.RecordFailure(error, ReportingApplication.Vatefs, ReportingOperation.ConfigurePluginProfile, ReportingStage.Apply);
            ReportingEvents.Complete(ReportingApplication.Vatefs, ReportingOperation.ConfigurePluginProfile,
                ReportingOutcome.Partial, ReportingEvents.FailureReason(error),
                details: new ReportingDetails { RecoveryAvailable = true });
            ActivityHistoryService.Record(ActivityApplication.VatEfs, ActivityOperation.Configure, ActivityOutcome.NeedsAttention,
                recoveryFolder: recovery.RecoveryFolder, dataFolder: dataFolder ?? _settings.EuroscopeDataPath);
        }
        else ReportingEvents.Stop(error, ReportingApplication.Vatefs, ReportingOperation.ConfigurePluginProfile, ReportingStage.Apply);
    }
}
