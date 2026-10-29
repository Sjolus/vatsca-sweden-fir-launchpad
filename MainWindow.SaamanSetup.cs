using System.IO;
using Microsoft.Win32;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    private readonly SaamanInstallService _saamanInstaller = new();
    private CancellationTokenSource? _saamanCancellation;
    private bool SaamanCanCancel { get; set; }
    private bool SaamanNeedsRuntime { get; set; }
    private bool SaamanNeedsSaveRetry { get; set; }

    private sealed class SaamanSetupAction
    {
        public ReportingStage Stage { get; set; } = ReportingStage.Prepare;
        public string Message { get; set; } = "SAAMAN choices saved.";
        public bool ChangedFiles { get; set; }
        public bool Saved { get; set; } = true;
        public bool Blocked { get; set; }
        public ReportingReason? Reason { get; set; }
        public string? RecoveryFolder { get; set; }
    }

    private Task SetSaamanEnabledAsync(bool enabled) => RunSaamanSetupAsync(
        ReportingOperation.ConfigurePluginProfile, async (action, token) =>
        {
            var choices = _settings.Copy();
            RequireSaamanProfileReady(choices, enabled);
            if (!enabled) SaamanNeedsRuntime = false;
            if (enabled)
            {
                if (!RequireSaamanRuntime(action)) return;
                string path = SaamanInstallService.GetSuggestedPath(choices);
                if (!File.Exists(path))
                    await InstallSaamanDllAsync(choices, action, token, reportInstallation: true);
                else
                {
                    var identity = _saamanInstaller.Adopt(path, choices);
                    choices.SaamanDllPath = identity.Path;
                }
            }
            token.ThrowIfCancellationRequested();
            choices.PatchSaaman = enabled;
            choices.SaamanProfileManaged = true;
            ApplySaamanProfile(choices, action);
            SaveSaamanChoices(choices, action, enabled);
            action.Message = enabled ? "SAAMAN is on for the APP / ACC profile."
                : "SAAMAN is off. Original MAESTRO entries were restored where previously replaced.";
            if (action.Reason == ReportingReason.MissingConfiguration)
                action.Message = "SAAMAN is off. The missing profile was not changed; moved or renamed profiles are unchanged.";
        });

    private Task UpdateSaamanAsync()
    {
        bool existing;
        try { existing = File.Exists(SaamanInstallService.GetSuggestedPath(_settings)); }
        catch { existing = false; }
        return RunSaamanSetupAsync(existing ? ReportingOperation.Update : ReportingOperation.Install,
            async (action, token) =>
            {
                if (!RequireSaamanRuntime(action)) return;
                var choices = _settings.Copy();
                RequireSaamanFilesClosed(choices);
                await InstallSaamanDllAsync(choices, action, token, reportInstallation: false);
                SaveSaamanChoices(choices, action);
            });
    }

    private async Task ChooseSaamanDllAsync()
    {
        if (!CanRunSoftwareAction() || !CanRunSelfUpdateAction()) return;
        var picker = new OpenFileDialog { Title = "Choose the existing SAAMAN plugin DLL",
            Filter = "SAAMAN plugin DLL (*.dll)|*.dll", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        await RunSaamanSetupAsync(_settings.PatchSaaman ? ReportingOperation.ConfigurePluginProfile : ReportingOperation.Adopt,
            (action, token) =>
            {
                var choices = _settings.Copy();
                RequireSaamanFilesClosed(choices);
                var selected = _saamanInstaller.Adopt(picker.FileName, choices);
                token.ThrowIfCancellationRequested();
                if (choices.PatchSaaman)
                {
                    var current = SaamanProfileService.Capture(choices, choices.EuroscopeDataPath)
                        ?? throw new InvalidOperationException("The enabled SAAMAN profile could not be identified.");
                    var plan = SaamanProfileService.BuildRelocationPlan(current, selected.Path, choices.EuroscopeDataPath);
                    if (!selected.Sha256.Equals(plan.DllHash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The selected SAAMAN DLL changed. Choose it again.");
                    ApplySaamanProfilePlan(plan, action);
                    choices.SaamanProfilePath = plan.Policy.ProfilePath;
                    choices.SaamanProfileManaged = true;
                }
                // Recheck identity before saving a selection that did not require a profile write.
                else if (_saamanInstaller.Adopt(selected.Path, choices) != selected)
                    throw new IOException("The selected SAAMAN DLL changed. Choose it again.");
                choices.SaamanDllPath = selected.Path;
                SaveSaamanChoices(choices, action);
                action.Message = choices.PatchSaaman ? "SAAMAN location and APP / ACC profile updated. SAAMAN remains on."
                    : "SAAMAN copy selected. Turn it on to use it in the APP / ACC profile.";
                return Task.CompletedTask;
            });
    }

    private void CancelSaamanOperation()
    {
        if (SaamanCanCancel) _saamanCancellation?.Cancel();
    }

    private Task RetrySaamanSaveAsync() => RunSaamanSetupAsync(ReportingOperation.Save, (action, _) =>
    {
        SaveSaamanChoices(_settings.Copy(), action);
        action.Message = "SAAMAN choices saved.";
        return Task.CompletedTask;
    });

    private Task InstallSaamanRuntimeAsync() => RunSaamanSetupAsync(ReportingOperation.Prerequisite, async (action, token) =>
    {
        if (WebViewRuntimeService.GetInstalledVersion() is not null)
        {
            SaamanNeedsRuntime = false;
            action.Message = "WebView2 is available. Turn SAAMAN on when ready.";
            action.Reason = ReportingReason.AlreadyConfigured;
            return;
        }
        if (!MaintenanceLock.TryAcquire(out var lease))
            throw new InvalidOperationException("Another Launchpad operation is active. Try again when it finishes.");
        using (lease)
        {
            action.Stage = ReportingStage.Apply;
            await WebViewRuntimeService.InstallAsync(new Progress<string>(message =>
                SetPluginStatus("SAAMAN", message, busy: true)), token,
                onRestartRequired: () => _setupRestartRequired = true);
        }
        SaamanNeedsRuntime = WebViewRuntimeService.GetInstalledVersion() is null;
        action.Message = _setupRestartRequired ? "Restart Windows before turning SAAMAN on."
            : "WebView2 is available. Turn SAAMAN on when ready.";
    });

    private async Task RunSaamanSetupAsync(ReportingOperation operation,
        Func<SaamanSetupAction, CancellationToken, Task> run)
    {
        if (!CanRunSoftwareAction() || !CanRunSelfUpdateAction()) return;
        var action = new SaamanSetupAction();
        BeginSaamanFeedback(operation);
        _softwareActionBusy = true;
        _processTimer.Stop();
        _saamanCancellation = new CancellationTokenSource();
        SaamanCanCancel = false;
        ReportingEvents.Start(ReportingApplication.Saaman, operation);
        SetPluginStatus("SAAMAN", "Preparing SAAMAN…", busy: true);
        UpdateSelfUpdateActionEnabled();
        try
        {
            await run(action, _saamanCancellation.Token);
            var outcome = action.Blocked
                ? ReportingOutcome.Blocked : !action.Saved ? action.ChangedFiles ? ReportingOutcome.Partial : ReportingOutcome.Failed
                : ReportingOutcome.Completed;
            ReportingEvents.Complete(ReportingApplication.Saaman, operation, outcome,
                !action.Saved ? ReportingReason.SaveFailed : action.Reason,
                details: new ReportingDetails { RecoveryAvailable = action.RecoveryFolder is not null });
            SetPluginStatus("SAAMAN", WithSaamanSaveWarning(action.Message),
                warning: !action.Saved || SaamanNeedsSaveRetry || outcome == ReportingOutcome.Blocked);
            if (operation == ReportingOperation.Prerequisite)
                ActivityHistoryService.Record(ActivityApplication.Saaman, ActivityOperation.Prerequisite, ActivityOutcome.Completed,
                    restartRequired: _setupRestartRequired);
        }
        catch (OperationCanceledException)
        {
            ReportingEvents.Complete(ReportingApplication.Saaman, operation,
                action.ChangedFiles ? ReportingOutcome.Partial : ReportingOutcome.Cancelled,
                details: new ReportingDetails { RecoveryAvailable = action.RecoveryFolder is not null });
            SetPluginStatus("SAAMAN", WithSaamanSaveWarning(action.ChangedFiles
                ? "SAAMAN setup stopped after the DLL was installed. Profile activation did not complete."
                : "SAAMAN preparation cancelled. No plugin or profile was changed."), warning: action.ChangedFiles, cancelled: !action.ChangedFiles);
        }
        catch (Exception ex)
        {
            if (action.ChangedFiles)
            {
                ReportingEvents.RecordFailure(ex, ReportingApplication.Saaman, operation, action.Stage);
                ReportingEvents.Complete(ReportingApplication.Saaman, operation, ReportingOutcome.Partial,
                    ReportingEvents.FailureReason(ex), details: new ReportingDetails { RecoveryAvailable = action.RecoveryFolder is not null });
            }
            else ReportingEvents.Stop(ex, ReportingApplication.Saaman, operation, action.Stage);
            string message = ex.Message;
            if (action.ChangedFiles) message += " Completed file changes were kept.";
            if (action.RecoveryFolder is not null) message += " Recovery is available in Activity & recovery.";
            SetPluginStatus("SAAMAN", WithSaamanSaveWarning(message), warning: true);
            if (operation == ReportingOperation.Prerequisite)
                ActivityHistoryService.Record(ActivityApplication.Saaman, ActivityOperation.Prerequisite, ActivityOutcome.Stopped,
                    restartRequired: _setupRestartRequired);
        }
        finally
        {
            SaamanCanCancel = false;
            _saamanCancellation.Dispose();
            _saamanCancellation = null;
            await RefreshSaamanLocalAsync();
            _softwareActionBusy = false;
            if (!_windowClosed)
            {
                ApplyApplicationUsage();
                _processTimer.Start();
                UpdateSelfUpdateActionEnabled();
                RefreshWorkspace();
            }
        }
    }

    private bool RequireSaamanRuntime(SaamanSetupAction action)
    {
        SaamanNeedsRuntime = WebViewRuntimeService.GetInstalledVersion() is null;
        if (!SaamanNeedsRuntime) return true;
        action.Message = "SAAMAN needs Microsoft Edge WebView2. Choose Install WebView2, then turn SAAMAN on.";
        action.Reason = ReportingReason.MissingConfiguration;
        action.Blocked = true;
        return false;
    }

    private static void RequireSaamanFilesClosed(AppSettings choices)
    {
        EuroScopeProcessGuard.RequireClosed();
        if (!string.IsNullOrWhiteSpace(choices.EuroscopeDataPath))
            GngUpdateService.RequireNoPendingUpdate(choices.EuroscopeDataPath);
    }

    private static void RequireSaamanProfileReady(AppSettings choices, bool enabled)
    {
        RequireSaamanFilesClosed(choices);
        string profile = string.IsNullOrWhiteSpace(choices.SaamanProfilePath)
            ? Path.Combine(choices.EuroscopeDataPath, SaamanProfileService.DefaultProfileName) : choices.SaamanProfilePath;
        var target = SaamanProfileService.ValidatePolicy(new(profile, choices.SaamanDllPath, false), choices.EuroscopeDataPath);
        if (enabled && !File.Exists(target.ProfilePath))
            throw new InvalidOperationException("The APP / ACC profile is missing. Get the Swedish GNG package or select its data folder before turning SAAMAN on.");
    }

    private void ApplySaamanProfile(AppSettings choices, SaamanSetupAction action)
    {
        // Static plugin identity must correspond to the exact bytes captured in the profile plan.
        var identity = choices.PatchSaaman ? _saamanInstaller.Adopt(choices.SaamanDllPath, choices) : null;
        var policy = SaamanProfileService.Capture(choices, choices.EuroscopeDataPath)
            ?? throw new InvalidOperationException("The SAAMAN profile could not be identified.");
        var plan = SaamanProfileService.BuildPlan(policy, choices.EuroscopeDataPath);
        if (identity is not null && !identity.Sha256.Equals(plan.DllHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("SAAMAN.dll changed while preparing the profile. Try again.");
        ApplySaamanProfilePlan(plan, action);
        choices.SaamanProfilePath = plan.Policy.ProfilePath;
        action.Reason = plan.ProfileMissing ? ReportingReason.MissingConfiguration
            : !plan.HasChanges && !action.ChangedFiles ? ReportingReason.AlreadyConfigured : null;
    }

    private void ApplySaamanProfilePlan(SaamanProfileChange plan, SaamanSetupAction action)
    {
        SaamanCanCancel = false;
        action.Stage = ReportingStage.Apply;
        SetPluginStatus("SAAMAN", "Updating the APP / ACC profile…", busy: true);
        if (!MaintenanceLock.TryAcquire(out var lease))
            throw new InvalidOperationException("Another Launchpad operation is active. Try again when it finishes.");
        using (lease)
        {
            GngUpdateService.RequireNoPendingUpdate(plan.DataFolder);
            EuroScopeProcessGuard.RequireClosed();
            var recovery = SaamanProfileService.Apply(plan);
            action.ChangedFiles |= plan.HasChanges;
            action.RecoveryFolder = recovery ?? action.RecoveryFolder;
            if (recovery is not null)
                ActivityHistoryService.Record(ActivityApplication.Saaman, ActivityOperation.Configure, ActivityOutcome.Completed,
                    recoveryFolder: recovery, dataFolder: plan.DataFolder);
        }
    }

    private void SaveSaamanChoices(AppSettings choices, SaamanSetupAction action, bool? enabled = null)
    {
        _settings.SaamanDllPath = choices.SaamanDllPath;
        _settings.SaamanProfilePath = choices.SaamanProfilePath;
        _settings.PatchSaaman = choices.PatchSaaman;
        _settings.SaamanProfileManaged = choices.SaamanProfileManaged;
        if (enabled.HasValue) ApplicationPreferences.SetNotUsed(_settings, "SAAMAN", !enabled.Value);
        action.Saved = SettingsService.TrySave(_settings);
        SaamanNeedsSaveRetry = !action.Saved;
        if (!action.Saved) ReportingService.RecordError(ReportingErrorCode.SettingsSaveFailed, new ReportingDetails
        { Application = ReportingApplication.Saaman, Operation = ReportingOperation.Save, Reason = ReportingReason.SaveFailed });
    }

    private string WithSaamanSaveWarning(string message) => SaamanNeedsSaveRetry
        ? message + " Launchpad could not save its choices. They remain active for this session; choose Retry save."
        : message;

    private async Task InstallSaamanDllAsync(AppSettings choices, SaamanSetupAction action, CancellationToken token, bool reportInstallation)
    {
        SaamanInstallPlan? plan = null;
        bool installing = false;
        bool acceptingProgress = false;
        var activeOperation = _saamanCancellation;
        var operation = File.Exists(SaamanInstallService.GetSuggestedPath(choices)) ? ActivityOperation.Update : ActivityOperation.Install;
        var reportOperation = operation == ActivityOperation.Update ? ReportingOperation.Update : ReportingOperation.Install;
        if (reportInstallation) ReportingEvents.Start(ReportingApplication.Saaman, reportOperation);
        try
        {
            SaamanCanCancel = true;
            action.Stage = ReportingStage.Check;
            SetPluginStatus("SAAMAN", "Checking the latest stable SAAMAN release…", busy: true);
            var release = await _saamanReleases.GetLatestAsync(token)
                ?? throw new InvalidOperationException("No published stable SAAMAN release is available.");
            _lastSaamanRelease = release; _saamanReleaseChecked = true; _saamanReleaseFailure = null;
            action.Stage = ReportingStage.Prepare;
            plan = _saamanInstaller.Preview(choices, release);
            token.ThrowIfCancellationRequested();
            if (plan.Previous is { } current && current.Version == release.Version && current.Size == release.Dll.Size &&
                current.Sha256.Equals(release.Dll.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                choices.SaamanDllPath = plan.TargetPath;
                action.Message = "SAAMAN is already up to date.";
                action.Reason = ReportingReason.AlreadyConfigured;
                if (reportInstallation) ReportingEvents.Complete(ReportingApplication.Saaman, reportOperation,
                    ReportingOutcome.Completed, ReportingReason.AlreadyConfigured);
                return;
            }
            installing = true;
            action.Stage = ReportingStage.Download;
            acceptingProgress = true;
            var result = await _saamanInstaller.InstallAsync(plan, choices, new Progress<SaamanProgress>(progress =>
            {
                if (!acceptingProgress || !ReferenceEquals(_saamanCancellation, activeOperation)) return;
                if (progress.Message.StartsWith("Applying", StringComparison.Ordinal))
                { SaamanCanCancel = false; action.Stage = ReportingStage.Apply; }
                SetPluginStatus("SAAMAN", progress.Message, percent: progress.Percent, busy: true);
            }), token);
            acceptingProgress = false;
            action.ChangedFiles = true;
            action.RecoveryFolder = Path.GetDirectoryName(result.ReceiptPath);
            choices.SaamanDllPath = result.DllPath;
            // Installation can succeed before activation fails. Keep the verified installed location.
            SaveSaamanChoices(choices, action);
            ActivityHistoryService.Record(ActivityApplication.Saaman, operation, ActivityOutcome.Completed,
                result.Version, action.RecoveryFolder, dataFolder: choices.EuroscopeDataPath, previousVersion: plan.Previous?.Version);
            action.Message = "SAAMAN " + result.Version + " installed.";
            if (reportInstallation) ReportingEvents.Complete(ReportingApplication.Saaman, reportOperation,
                action.Saved ? ReportingOutcome.Completed : ReportingOutcome.Partial, action.Saved ? null : ReportingReason.SaveFailed,
                details: new ReportingDetails { RecoveryAvailable = true, PackageVersion = ReportingEvents.PublicVersion(result.Version),
                    PreviousVersion = ReportingEvents.PublicVersion(plan.Previous?.Version) });
        }
        catch (Exception ex)
        {
            if (installing) action.RecoveryFolder = _saamanInstaller.LastRecoveryFolder;
            if (plan is not null) ActivityHistoryService.Record(ActivityApplication.Saaman, operation,
                ex is OperationCanceledException ? ActivityOutcome.Cancelled : ActivityOutcome.Stopped,
                plan.Release.Version, action.RecoveryFolder, dataFolder: choices.EuroscopeDataPath, previousVersion: plan.Previous?.Version);
            if (reportInstallation)
            {
                if (ex is OperationCanceledException) ReportingEvents.Complete(ReportingApplication.Saaman, reportOperation, ReportingOutcome.Cancelled);
                else ReportingEvents.Stop(ex, ReportingApplication.Saaman, reportOperation,
                    ex is InvalidDataException && installing ? ReportingStage.Verify : action.Stage);
            }
            throw;
        }
        finally { acceptingProgress = false; SaamanCanCancel = false; }
    }
}
