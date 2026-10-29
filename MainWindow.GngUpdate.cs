using System.Windows;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    private GngUpdateControl? _gngUpdateFlow;
    private GngCleanupControl? _gngCleanupFlow;
    private string _gngFlowWorkspace = "Apps";
    private string _gngFlowSection = "Gng";
    private bool _gngResultRefreshed;
    private bool _gngRefreshInProgress;
    private int _gngObservedMutation;
    private int _gngObservedCompletion;
    private bool HasGngFlow => _gngUpdateFlow is not null || _gngCleanupFlow is not null;

    private async void GngUpdate_Click(object sender, RoutedEventArgs e) => await StartGngFlowAsync();

    private async Task StartGngFlowAsync(string mode = "Update")
    {
        if (HasGngFlow) { ReturnToGngFlow(); return; }
        if (!CanRunSoftwareAction() || !CanRunSelfUpdateAction()) return;
        _softwareActionBusy = true;
        _gngResultRefreshed = false;
        _gngObservedMutation = 0;
        _gngObservedCompletion = 0;
        _gngFlowWorkspace = _workspace;
        _gngFlowSection = SetupWorkspace.SelectedSection;
        try
        {
            var root = _settings.EuroscopeDataPath;
            if (string.IsNullOrWhiteSpace(root)) root = GngSetupDestination.PrepareDefault();
            var flow = new GngUpdateControl(root, _settings.LastEuroscopeProfile,
                GngProtectedPaths.ForUpdate(_settings), CleanupProtectedPaths(), _requestedActivityRecovery?.RecoveryFolder,
                saamanSettings: _settings, installedVersion: _results[1].InstalledVersion);
            _gngUpdateFlow = flow;
            flow.StateChanged += GngFlowStateChanged;
            flow.Dismissed += DismissGngFlow;
            flow.FollowUpRequested += GngFollowUpRequested;
            GngFlowHost.Content = flow;
            UpdateSelfUpdateActionEnabled();
            GngFlowStateChanged();
            if (mode == "Import") await flow.ImportPackageAsync();
            else if (mode == "Fresh") await flow.DownloadFreshAsync();
            else await flow.StartAsync();
        }
        catch (Exception ex)
        {
            var message = "GNG could not be prepared: " + ex.Message;
            if (_gngUpdateFlow?.CanDismiss != false) DismissGngFlow();
            LastCheckedText.Text = message;
        }
        finally
        {
            if (!HasGngFlow) _softwareActionBusy = false;
            if (!_windowClosed) UpdateSelfUpdateActionEnabled();
        }
    }

    private void GngFlowStateChanged()
    {
        if (_windowClosed || _gngUpdateFlow is not { } flow) return;
        _setupRestartRequired |= flow.RestartRequired;
        var row = _results[1];
        row.GngOperation = new(flow.Status, flow.Stage, flow.IsBusy, flow.CanCancel,
            flow.NeedsAttentionView, flow.ProgressIndeterminate ? null : flow.Progress, flow.HasWarning, flow.TransferSummary);
        LastCheckedText.Text = flow.Status;
        SetupWorkspace.SetGngState(row, flow.Status);
        UpdateGngPresentation();
        if (!_gngRefreshInProgress && !flow.IsBusy &&
            (flow.MutationRevision != _gngObservedMutation || flow.CompletionRevision != _gngObservedCompletion))
        {
            _gngRefreshInProgress = true;
            bool completedPackage = flow.CompletionRevision != _gngObservedCompletion;
            _gngObservedMutation = flow.MutationRevision;
            _gngObservedCompletion = flow.CompletionRevision;
            _gngResultRefreshed = false;
            try
            {
                bool saved = true;
                if (completedPackage)
                {
                    _settings.EuroscopeDataPath = flow.DataFolder;
                    saved = SettingsService.TrySave(_settings);
                    ApplyLaunchPaths();
                }
                UpdateChecker.RefreshGngLocal(row, flow.DataFolder);
                if (!ReferenceEquals(_gngUpdateFlow, flow) || _windowClosed) return;
                RefreshGngLocalState();
                if (completedPackage)
                {
                    var needsFonts = row.FontsState == FontsState.NeedsAction;
                    var resultText = flow.PackageAlreadyMatches ? "GNG already matches. No files were changed." : "GNG updated.";
                    var message = !saved
                        ? "The GNG folder could not be saved. Save it in Setup → Swedish GNG before reopening Launchpad."
                        : needsFonts && _controllerProfileNeedsReview
                            ? "Finish these two steps before opening EuroScope."
                            : needsFonts
                                ? "Install the updated fonts before opening EuroScope."
                                : _controllerProfileNeedsReview
                                    ? "Check your controller details for the updated profiles."
                                    : string.Empty;
                    flow.SetCompletionChecks(message, needsFonts, _controllerProfileNeedsReview);
                    LastCheckedText.Text = string.IsNullOrWhiteSpace(message) ? resultText : message;
                }
                _gngResultRefreshed = true;
            }
            catch (Exception ex) { LastCheckedText.Text = "GNG result could not be refreshed: " + ex.Message; }
            finally { _gngRefreshInProgress = false; if (!_windowClosed) UpdateSelfUpdateActionEnabled(); }
        }
    }

    private void RefreshGngLocalState()
    {
        RefreshEuroscopeProfiles();
        var fonts = FontService.Check(_settings.EuroscopeDataPath);
        _results[1].FontsState = fonts.State;
        _results[1].FontsTooltip = fonts.Tooltip;
        UpdateAppConfigButton();
        RefreshWorkspace();
    }

    private void DismissGngFlow()
    {
        if (_gngRefreshInProgress) return;
        if (_gngUpdateFlow?.CanDismiss == false || _gngCleanupFlow?.CanDismiss == false) return;
        bool attempted = _gngUpdateFlow?.AttemptedChanges == true || _gngCleanupFlow?.AttemptedChanges == true;
        var status = _gngUpdateFlow?.Status ?? _gngCleanupFlow?.Status;
        if (_gngUpdateFlow is { } flow)
        {
            flow.StateChanged -= GngFlowStateChanged;
            flow.Dismissed -= DismissGngFlow;
            flow.FollowUpRequested -= GngFollowUpRequested;
            flow.Dispose();
        }
        if (_gngCleanupFlow is { } cleanup)
        {
            cleanup.StateChanged -= GngCleanupStateChanged;
            cleanup.CloseRequested -= GngCleanupCloseRequested;
        }
        _gngUpdateFlow = null;
        _gngCleanupFlow = null;
        GngFlowHost.Content = null;
        SetupWorkspace.SetGngOperationContent(null);
        GngFlowHost.Visibility = Visibility.Collapsed;
        _results[1].GngOperation = null;
        _softwareActionBusy = false;
        if (attempted && !_gngResultRefreshed)
        {
            try { RefreshGngLocalState(); }
            catch (Exception ex) { status = "GNG result could not be refreshed: " + ex.Message; }
        }
        PresentWorkspace(_workspace);
        UpdateSelfUpdateActionEnabled();
        RefreshWorkspace();
        if (!string.IsNullOrWhiteSpace(status)) LastCheckedText.Text = status;
    }

    private void GngCleanupCloseRequested(object? sender, EventArgs e) => DismissGngFlow();

    private void GngFollowUpRequested(string action)
    {
        DismissGngFlow();
        if (HasGngFlow) return;
        SetupWorkspace.ShowSection(action == "Profile" ? "Profile" : "Gng");
        ShowWorkspace("Setup");
        if (action == "Fonts") Fonts_Click(this, new RoutedEventArgs());
    }

    private async Task CheckGngFromSetupAsync()
    {
        if (!CanRunSoftwareAction() || !CanRunSelfUpdateAction()) return;
        _softwareActionBusy = true;
        _results[1].Status = CheckStatus.Checking;
        UpdateSelfUpdateActionEnabled();
        try
        {
            await UpdateChecker.CheckGng(_results[1], _settings.EuroscopeDataPath);
            RefreshGngLocalState();
            SetupWorkspace.SetGngState(_results[1]);
        }
        finally { _softwareActionBusy = false; UpdateSelfUpdateActionEnabled(); }
    }
}
