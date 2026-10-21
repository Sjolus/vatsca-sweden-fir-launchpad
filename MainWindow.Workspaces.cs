using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    private string _workspace = "Apps";
    private ActivityWorkspaceControl ActivityWorkspace => SetupWorkspace.Activity;
    private ConfiguredPathValidation? _pendingLocation;
    private string _previousLocation = "";
    private bool _controllerProfileNeedsReview;
    private ReportingApplication? _locationReportingApp;

    private void InitializeWorkspaces()
    {
        SetupWorkspace.Requested += SetupWorkspace_Requested;
        SetupWorkspace.LocationAccepted += SaveWorkspaceLocation;
        SetupWorkspace.LocationCancelled += (_, _) => { _pendingLocation = null; FinishLocationReport(ReportingOutcome.Cancelled); };
        SetupWorkspace.SectionChanged += () =>
        {
            if (_workspace != "Setup") return;
            if (SetupGuide.RememberSection(_settings, SetupWorkspace.SelectedSection)) SaveSessionSettings("Setup progress saved.");
            PresentWorkspace(_workspace);
            ReportWorkspaceView();
            RefreshWorkspace();
        };
        InitializePreferencesWorkspace(SetupWorkspace.Preferences);
        InitializeControllerProfileWorkspace();
        ActivityWorkspace.Requested += ActivityWorkspace_Requested;
        ActivityWorkspace.SelectionChanged += RefreshWorkspaceRecovery;
        RefreshWorkspace();
        ShowWorkspace("Apps");
    }

    private void AppsWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (_windowClosed || _openDialogCount > 0) return;
        ShowWorkspace("Apps");
    }

    private void OpenGettingStarted()
    {
        if (!CanRunSoftwareAction() || !CanRunSelfUpdateAction()) return;
        if (SetupGuide.Start(_settings)) SaveSessionSettings("Getting started. Existing locations and application settings are kept.");
        SetupWorkspace.ShowSection(SetupGuide.Section(_settings));
        ShowWorkspace("Setup");
        UpdateAppConfigButton();
    }

    private void AdvanceSetupGuide()
    {
        if (!_settings.SetupGuideActive) return;
        var next = SetupGuide.Next(_settings, SetupWorkspace.SelectedSection);
        if (next.Changed) SaveSessionSettings(next.Destination == "Apps"
            ? "Setup guide finished. You can change anything in Setup."
            : "Setup progress saved.");
        if (next.Destination == "Apps") ShowWorkspace("Apps");
        else { SetupWorkspace.ShowSection(next.Destination); ShowWorkspace("Setup"); }
        UpdateAppConfigButton();
    }

    private void LeaveSetupGuideForLater()
    {
        if (SetupGuide.Later(_settings)) SaveSessionSettings("Setup paused. Use Help → Getting started to continue when ready.");
        ShowWorkspace("Apps");
        UpdateAppConfigButton();
    }
    private void ShowWorkspace(string page)
    {
        if (_windowClosed || _openDialogCount > 0) return;
        if (page == "Activity")
        {
            page = "Setup";
            SetupWorkspace.ShowSection("Activity");
        }
        if (page == "Setup" && _workspace != "Setup") ReportingEvents.Feature(ReportingFeature.SetupGuide);
        _workspace = page;
        if (page == "Setup" && SetupGuide.RememberSection(_settings, SetupWorkspace.SelectedSection))
            SaveSessionSettings("Setup progress saved.");
        PresentWorkspace(page);
        ReportWorkspaceView();
        RefreshWorkspace();
    }

    private void RefreshWorkspace()
    {
        if (_results is null || _settings is null) return;
        SetupWorkspace.SetGuide(SetupGuide.Present(_settings, SetupWorkspace.SelectedSection));
        SetupWorkspace.SetApplications(_results.Where(r => r.AppName is "EuroScope" or "TrackAudio" or "VACS" or "vATIS" or "VatEFS" or "SAAMAN")
            .Select(BuildSetupApplication).ToArray());
        SetupWorkspace.SetSummary(_settings.EuroscopeDataPath,
            string.IsNullOrWhiteSpace(_settings.EuroscopeDataPath) ? "Choose an existing data folder, or get a package to set up a new one."
                : "Downloads use the matching saved package when available. You review file changes before installation.",
            (string.IsNullOrWhiteSpace(_settings.VatsimCid) ? "Not configured" : "CID " + _settings.VatsimCid + " · " + _settings.VatsimName) +
                (_controllerProfileNeedsReview ? " · Review controller profile" : ""),
            _settings.PatchSaaman, _settings.PatchVatEfs);
        RefreshPreferencesWorkspace();
        RefreshControllerProfileWorkspace();
        if (_workspace == "Setup" && SetupWorkspace.SelectedSection == "Activity")
        {
            var history = ActivityHistoryService.Read();
            ActivityWorkspace.SetHistory(history.Entries, history.Unavailable ? "Saved activity could not be read. Recovery backups are unchanged."
                : ActivityHistoryService.LastWriteFailed ? "The last result could not be saved. Keep its recovery folder."
                : "Recent changes on this PC. Select a result for details and recovery.");
        }
    }

    private static ConfiguredPathKind PathKind(string key) => key switch
    {
        "EuroScope" => ConfiguredPathKind.EuroScopeExecutable,
        "EuroScope (GNG Pack)" => ConfiguredPathKind.EuroScopeDataFolder,
        "TrackAudio" => ConfiguredPathKind.TrackAudioExecutable,
        "VACS" => ConfiguredPathKind.VacsExecutable,
        "vATIS" => ConfiguredPathKind.VatisExecutable,
        "VatEFS" => ConfiguredPathKind.VatEfsFolder,
        _ => throw new ArgumentException("This application has no selectable location.", nameof(key))
    };

    private async void SetupWorkspace_Requested(object? sender, SetupWorkspaceRequest request)
    {
        if (request.Action == SetupWorkspaceAction.PluginCancel && request.Application == "SAAMAN")
        { CancelSaamanOperation(); return; }
        if (!CanRunSoftwareAction() || !CanRunSelfUpdateAction()) return;
        try
        {
            var row = _results.FirstOrDefault(r => r.AppName == request.Application);
            var source = new Button { DataContext = row };
            var args = new RoutedEventArgs();
            switch (request.Action)
            {
                case SetupWorkspaceAction.GuideNext: AdvanceSetupGuide(); break;
                case SetupWorkspaceAction.GuideLater: LeaveSetupGuideForLater(); break;
                case SetupWorkspaceAction.Discover: await DiscoverWorkspaceLocations(); break;
                case SetupWorkspaceAction.ChangeLocation:
                    if (request.Application == "SAAMAN") await ChooseSaamanDllAsync();
                    else if (request.Application != null) await ChooseWorkspaceLocation(request.Application);
                    break;
                case SetupWorkspaceAction.Manage:
                    if (row?.HasSaamanManagement == true) await UpdateSaamanAsync();
                    else if (row?.HasEuroScopeManagement == true) EuroScopeManage_Click(source, args);
                    else if (row?.SoftwareApp != null)
                    {
                        if (File.Exists(SoftwareExecutablePath(row)))
                        {
                            if (row.IsNotUsed)
                            {
                                // The hidden application's button explicitly says "Show in Apps".
                                if (!TryAcquireMaintenanceGuard(out var usageLease)) return;
                                using (usageLease)
                                {
                                    ApplicationPreferences.SetNotUsed(_settings, row.AppName, false);
                                    ApplyApplicationUsage();
                                    SaveSessionSettings(row.DisplayName + " is included again.");
                                }
                            }
                            ShowWorkspace("Apps"); FocusApplicationRow(row);
                            if (row.SoftwareUpdate is null) await _softwareUpdates.CheckAsync(row.SoftwareApp.Value, SoftwareExecutablePath(row), _windowLifetime.Token);
                        }
                        else SoftwareInstall_Click(source, args);
                    }
                    break;
                case SetupWorkspaceAction.Gng: GngUpdate_Click(source, args); break;
                case SetupWorkspaceAction.Cleanup: GngCleanup_Click(source, args); break;
                case SetupWorkspaceAction.Fonts: Fonts_Click(source, args); break;
                case SetupWorkspaceAction.ControllerProfile: AppConfig_Click(source, args); RefreshWorkspace(); break;
                case SetupWorkspaceAction.Saaman: SaamanManage_Click(source, args); break;
                case SetupWorkspaceAction.SaamanGuide: SaamanGuide_Click(source, args); break;
                case SetupWorkspaceAction.TogglePlugin:
                    if (request.Enabled is not { } enabled) break;
                    RefreshWorkspace(); // Show the applied choice until the requested change succeeds.
                    if (request.Application == "SAAMAN") await SetSaamanEnabledAsync(enabled);
                    else if (request.Application == "VatEFS") await SetVatEfsEnabledAsync(enabled);
                    break;
                case SetupWorkspaceAction.PluginRetrySave:
                    if (request.Application == "SAAMAN") await RetrySaamanSaveAsync();
                    else if (request.Application == "VatEFS") RetryVatEfsSave();
                    break;
                case SetupWorkspaceAction.PluginRuntime:
                    if (request.Application == "SAAMAN") await InstallSaamanRuntimeAsync();
                    break;
            }
        }
        catch (OperationCanceledException) when (_windowClosed) { }
        catch (Exception ex)
        {
            if (_locationReportingApp is { } app) ReportingEvents.RecordFailure(ex, app, ReportingOperation.Adopt);
            FinishLocationReport(ReportingEvents.FailureOutcome(ex), ReportingEvents.FailureReason(ex));
            SetupWorkspace.ShowNotice("The action could not finish: " + ex.Message);
        }
    }

    private async Task ChooseWorkspaceLocation(string key)
    {
        FinishLocationReport(ReportingOutcome.Cancelled);
        _locationReportingApp = ReportingApplicationFor(key);
        ReportingEvents.Start(_locationReportingApp.Value, ReportingOperation.Adopt, ReportingSource.Browse);
        var kind = PathKind(key);
        string previous = ConfiguredPathValidationService.PathFor(kind, _settings);
        string selected;
        if (kind is ConfiguredPathKind.EuroScopeDataFolder or ConfiguredPathKind.VatEfsFolder)
        {
            var picker = new OpenFolderDialog { Title = key == "VatEFS" ? "Choose the folder containing VatEFS.dll" : "Choose your EuroScope data folder", Multiselect = false };
            if (Directory.Exists(previous)) picker.InitialDirectory = previous;
            if (picker.ShowDialog(this) != true) { FinishLocationReport(ReportingOutcome.Cancelled); return; }
            selected = picker.FolderName;
        }
        else
        {
            var picker = new OpenFileDialog { Title = "Choose the installed " + key + " program", Filter = "Windows applications (*.exe)|*.exe", CheckFileExists = true, Multiselect = false };
            if (File.Exists(previous)) picker.InitialDirectory = Path.GetDirectoryName(previous);
            if (picker.ShowDialog(this) != true) { FinishLocationReport(ReportingOutcome.Cancelled); return; }
            selected = picker.FileName;
        }
        _softwareActionBusy = true; UpdateSelfUpdateActionEnabled();
        try
        {
            var validation = await Task.Run(() => new ConfiguredPathValidationService().Validate(kind, selected));
            if (_windowClosed) return;
            _pendingLocation = validation; _previousLocation = previous;
            SetupWorkspace.ReviewLocation(key, previous, validation);
            if (validation.Status is PathValidationStatus.Invalid or PathValidationStatus.NotSelected)
                FinishLocationReport(ReportingOutcome.Blocked, ReportingReason.VerificationFailed);
        }
        catch (Exception ex)
        {
            if (_locationReportingApp is { } app) ReportingEvents.RecordFailure(ex, app, ReportingOperation.Adopt, ReportingStage.Verify);
            FinishLocationReport(ReportingEvents.FailureOutcome(ex), ReportingEvents.FailureReason(ex));
            throw;
        }
        finally { _softwareActionBusy = false; if (!_windowClosed) UpdateSelfUpdateActionEnabled(); }
    }

    private async void SaveWorkspaceLocation(object? sender, EventArgs e)
    {
        if (_pendingLocation is not { } reviewed || !CanRunSoftwareAction() || !TryAcquireMaintenanceGuard(out var lease)) return;
        using var guard = lease;
        if (_locationReportingApp is null)
        {
            _locationReportingApp = ReportingApplicationFor(SetupWorkspaceKey(reviewed.Kind));
            ReportingEvents.Start(_locationReportingApp.Value, ReportingOperation.Adopt, ReportingSource.Browse);
        }
        _softwareActionBusy = true; UpdateSelfUpdateActionEnabled();
        try
        {
            if (!ConfiguredPathValidationService.PathFor(reviewed.Kind, _settings).Equals(_previousLocation, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The saved location changed. Select it again before saving.");
            var current = await Task.Run(() => new ConfiguredPathValidationService().Validate(reviewed.Kind, reviewed.Path));
            if (_windowClosed) return;
            if (current.Status is PathValidationStatus.Invalid or PathValidationStatus.NotSelected || current.Status != reviewed.Status)
            { _pendingLocation = current; SetupWorkspace.ReviewLocation(reviewed.Kind.ToString(), _previousLocation, current); FinishLocationReport(ReportingOutcome.Blocked, ReportingReason.VerificationFailed); return; }
            var next = _settings.Copy();
            switch (reviewed.Kind)
            {
                case ConfiguredPathKind.EuroScopeExecutable: next.EuroscopeExePath = current.Path; break;
                case ConfiguredPathKind.EuroScopeDataFolder: next.EuroscopeDataPath = current.Path; break;
                case ConfiguredPathKind.TrackAudioExecutable: next.TrackAudioExePath = current.Path; break;
                case ConfiguredPathKind.VacsExecutable: next.VacsExePath = current.Path; break;
                case ConfiguredPathKind.VatisExecutable: next.VatisExePath = current.Path; break;
                case ConfiguredPathKind.VatEfsFolder: next.VatEfsPath = current.Path; break;
            }
            var (pluginRelocated, saved) = await SaveWorkspaceLocationsAsync(next);
            _pendingLocation = null; SetupWorkspace.ClearLocationReview();
            SetupWorkspace.ShowNotice(!saved ? "Profile references updated. Selected locations are kept for this session; use Retry save beside VatEFS."
                : pluginRelocated ? "VatEFS location and profile references updated."
                : "Location saved. Existing application settings are unchanged.");
            FinishLocationReport(saved ? ReportingOutcome.Completed : ReportingOutcome.Partial, saved ? null : ReportingReason.SaveFailed);
            ReportingEvents.Feature(ReportingFeature.Adopt, ReportingApplicationFor(SetupWorkspaceKey(reviewed.Kind)));
        }
        catch (Exception ex)
        {
            if (_locationReportingApp is { } app) ReportingEvents.RecordFailure(ex, app, ReportingOperation.Adopt, ReportingStage.Apply);
            FinishLocationReport(ex is VatEfsProfileRecoveryException ? ReportingOutcome.Partial : ReportingEvents.FailureOutcome(ex),
                ex is VatEfsProfileRecoveryException ? ReportingEvents.FailureReason(ex) : ex is IOException or UnauthorizedAccessException ? ReportingReason.SaveFailed : ReportingEvents.FailureReason(ex));
            SetupWorkspace.ShowNotice("Location not saved: " + ex.Message);
        }
        finally { _softwareActionBusy = false; if (!_windowClosed) UpdateSelfUpdateActionEnabled(); }
    }

    private async Task DiscoverWorkspaceLocations()
    {
        if (!TryAcquireMaintenanceGuard(out var lease)) return;
        using var guard = lease;
        ReportingEvents.Start(ReportingApplication.Launchpad, ReportingOperation.Adopt, ReportingSource.Discovery);
        _softwareActionBusy = true; UpdateSelfUpdateActionEnabled();
        SetupWorkspace.ShowNotice("Finding installed applications…");
        try
        {
            var candidates = await Task.Run(() => new InstallationDiscoveryService().Discover());
            if (_windowClosed) return;
            var window = new InstallationDiscoveryWindow(candidates, _settings.Copy());
            if (ShowOwnedDialog(window) != true || window.SelectedCandidates.Count == 0)
            { ReportingEvents.Complete(ReportingApplication.Launchpad, ReportingOperation.Adopt, ReportingOutcome.Cancelled); SetupWorkspace.ShowNotice("Your saved locations are unchanged."); return; }
            var selection = new InstallationDiscoverySelection(_settings, window.SelectedCandidates);
            foreach (var candidate in window.SelectedCandidates)
            {
                var option = selection.Options.First(o => o.Candidate.Id == candidate.Id && string.Equals(o.Path, InstallationDiscoverySelection.CandidatePath(candidate), StringComparison.OrdinalIgnoreCase));
                selection.Choose(option);
            }
            var next = selection.ApplyTo(_settings);
            var checks = new ConfiguredPathValidationService().Validate(next).Where(v => ConfiguredPathValidationService.NeedsAttention(v, _settings)).ToArray();
            if (checks.Any(v => v.Status == PathValidationStatus.Invalid)) throw new InvalidOperationException("A selected location is no longer valid. Find installed apps again.");
            if (checks.Any(v => v.Status == PathValidationStatus.Warning) && MessageBox.Show(this,
                string.Join("\n\n", checks.Select(v => v.Path + "\n" + v.Message)) + "\n\nUse these locations?", "Review selected locations", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            { ReportingEvents.Complete(ReportingApplication.Launchpad, ReportingOperation.Adopt, ReportingOutcome.Cancelled); SetupWorkspace.ShowNotice("Your saved locations are unchanged."); return; }
            var (pluginRelocated, saved) = await SaveWorkspaceLocationsAsync(next);
            SetupWorkspace.ShowNotice(!saved ? "Profile references updated. Selected locations are kept for this session; use Retry save beside VatEFS."
                : pluginRelocated ? "Selected locations saved. Enabled VatEFS profile references now use the selected copy."
                : "Selected locations saved.");
            ReportingEvents.Complete(ReportingApplication.Launchpad, ReportingOperation.Adopt,
                saved ? ReportingOutcome.Completed : ReportingOutcome.Partial, saved ? null : ReportingReason.SaveFailed);
            ReportingEvents.Feature(ReportingFeature.Adopt);
        }
        catch (Exception ex)
        {
            if (ex is VatEfsProfileRecoveryException)
            {
                ReportingEvents.RecordFailure(ex, ReportingApplication.Launchpad, ReportingOperation.Adopt, ReportingStage.Apply);
                ReportingEvents.Complete(ReportingApplication.Launchpad, ReportingOperation.Adopt,
                    ReportingOutcome.Partial, ReportingEvents.FailureReason(ex), details: new ReportingDetails { RecoveryAvailable = true });
            }
            else ReportingEvents.Stop(ex, ReportingApplication.Launchpad, ReportingOperation.Adopt);
            SetupWorkspace.ShowNotice("Locations not saved: " + ex.Message);
        }
        finally { _softwareActionBusy = false; if (!_windowClosed) UpdateSelfUpdateActionEnabled(); }
    }

    // Both callers hold the maintenance lease. Only an explicitly changed, enabled VatEFS location follows into profiles.
    private async Task<(bool PluginRelocated, bool Saved)> SaveWorkspaceLocationsAsync(AppSettings next)
    {
        var previous = _settings;
        VatEfsProfileResult? pluginChange = null;
        bool saved = true;
        if (InstallationDiscoverySelection.RequiresVatEfsProfileRelocation(previous, next))
        {
            ReportingEvents.Start(ReportingApplication.Vatefs, ReportingOperation.ConfigurePluginProfile);
            try
            {
                pluginChange = await Task.Run(() => VatEfsProfileService.Apply(next, true));
                saved = SettingsService.TrySave(next);
                _vatEfsNeedsSaveRetry = !saved;
                ReportingEvents.Complete(ReportingApplication.Vatefs, ReportingOperation.ConfigurePluginProfile,
                    saved ? ReportingOutcome.Completed : ReportingOutcome.Partial, saved ? null : ReportingReason.SaveFailed,
                    details: new ReportingDetails { RecoveryAvailable = pluginChange.RecoveryFolder is not null });
            }
            catch (Exception ex)
            {
                RecordVatEfsProfileFailure(ex, next.EuroscopeDataPath);
                throw;
            }
        }
        else SettingsService.Save(next);

        // Profile writes have completed. Keep their selected locations even if saving Launchpad's settings failed.
        _settings = next;
        RefreshAfterLocationsChanged(previous);
        if (pluginChange is not null)
        {
            ActivityHistoryService.Record(ActivityApplication.VatEfs, ActivityOperation.Configure,
                saved ? ActivityOutcome.Completed : ActivityOutcome.NeedsAttention,
                recoveryFolder: pluginChange.RecoveryFolder, dataFolder: next.EuroscopeDataPath);
            SetPluginStatus("VatEFS", saved ? "Location and EuroScope profiles updated. VatEFS remains enabled."
                : "Profiles now use the selected DLL, but Launchpad could not save the location. Retry saving.", !saved);
        }
        return (pluginChange is not null, saved);
    }

    private void FinishLocationReport(ReportingOutcome outcome, ReportingReason? reason = null)
    {
        if (_locationReportingApp is not { } app) return;
        ReportingEvents.Complete(app, ReportingOperation.Adopt, outcome, reason);
        _locationReportingApp = null;
    }

    private static string SetupWorkspaceKey(ConfiguredPathKind kind) => kind switch
    {
        ConfiguredPathKind.EuroScopeExecutable => "EuroScope", ConfiguredPathKind.EuroScopeDataFolder => "EuroScope (GNG Pack)",
        ConfiguredPathKind.VacsExecutable => "VACS", ConfiguredPathKind.VatisExecutable => "vATIS",
        ConfiguredPathKind.TrackAudioExecutable => "TrackAudio", _ => "VatEFS"
    };

    private void RefreshAfterLocationsChanged(AppSettings previous)
    {
        ApplyLaunchPaths(); RefreshEuroscopeProfiles();
        ResetChangedSoftwareChecks(previous);
        foreach (var (index, kind) in new[] { (0, ConfiguredPathKind.EuroScopeExecutable), (1, ConfiguredPathKind.EuroScopeDataFolder) })
            if (!string.Equals(ConfiguredPathValidationService.PathFor(kind, previous), ConfiguredPathValidationService.PathFor(kind, _settings), StringComparison.OrdinalIgnoreCase))
            {
                _results[index].Status = CheckStatus.Unknown;
                _results[index].InstalledVersion = "—";
                _results[index].StatusMessage = "Location saved. Check for updates to inspect this copy.";
            }
        UpdateAppConfigButton();
        ApplyApplicationUsage(); RefreshWorkspace();
    }

    private void RefreshWorkspaceRecovery(ActivityEntry entry)
    {
        bool available = ActivityHistoryService.CanOpenRecoveryFolder(entry.RecoveryFolder);
        bool canRestore = available && !_setupRestartRequired && ActivityHistoryService.SameDataFolder(entry.DataFolder, _settings.EuroscopeDataPath);
        string guidance = entry.Guidance;
        if (entry.Application == ActivityApplication.Saaman && entry.Operation == ActivityOperation.Install && entry.PreviousVersion is null)
            guidance += " This was a fresh installation; there is no earlier DLL to restore.";
        if (entry.RestartRequired) guidance += " A Windows restart was requested at the time.";
        if (entry.RecoveryFolder != null)
            guidance += !available ? " The recovery folder is missing or unavailable."
                : entry.Operation == ActivityOperation.Configure ? " This copy contains the original profile. Restore it manually only with EuroScope closed; doing so also replaces any later edits to that profile."
                : entry.Application is ActivityApplication.Gng or ActivityApplication.Saaman ? canRestore ? " Review restore checks this backup before changing files." : " Use the original EuroScope data folder and complete any required restart before recovery."
                : " Vendor recovery is manual; read RESTORE.txt in the recovery folder if provided.";
        ActivityWorkspace.SetRecoveryState(available, canRestore, guidance);
    }

    private async void ActivityWorkspace_Requested(ActivityWorkspaceRequest request)
    {
        if (!CanControlRunningApplication()) return;
        var entry = request.Entry;
        if (request.Action == ActivityWorkspaceAction.Report)
        {
            OpenProblemReport(ReportingApplicationFor(entry.ApplicationName), entry.Application == ActivityApplication.Gng ? ReportingCategory.Gng
                : entry.Operation == ActivityOperation.Update ? ReportingCategory.Update : entry.Operation == ActivityOperation.Install ? ReportingCategory.Installation : ReportingCategory.Other);
        }
        else if (request.Action == ActivityWorkspaceAction.OpenFolder)
        {
            if (!ActivityHistoryService.CanOpenRecoveryFolder(entry.RecoveryFolder)) { RefreshWorkspaceRecovery(entry); return; }
            try { Process.Start(new ProcessStartInfo(entry.RecoveryFolder!) { UseShellExecute = true }); }
            catch { ActivityWorkspace.SetRecoveryState(false, false, "Windows could not open the recovery folder. You can copy its location above."); }
        }
        else if (entry.Application == ActivityApplication.Saaman && !_setupRestartRequired && ActivityHistoryService.CanOpenRecoveryFolder(entry.RecoveryFolder) && ActivityHistoryService.SameDataFolder(entry.DataFolder, _settings.EuroscopeDataPath))
            await RestoreSaamanFromActivityAsync(entry);
        else if (entry.Application == ActivityApplication.Gng && !_setupRestartRequired && ActivityHistoryService.CanOpenRecoveryFolder(entry.RecoveryFolder) && ActivityHistoryService.SameDataFolder(entry.DataFolder, _settings.EuroscopeDataPath))
        {
            _requestedActivityRecovery = entry;
            try
            {
                if (entry.Operation is ActivityOperation.Cleanup or ActivityOperation.RestoreCleanup) GngCleanup_Click(this, new());
                else GngUpdate_Click(this, new());
            }
            finally { _requestedActivityRecovery = null; }
        }
    }
}
