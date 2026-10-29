using System.IO;
using System.Windows;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    private async void GngCleanup_Click(object sender, RoutedEventArgs e)
    {
        if (HasGngFlow) { ReturnToGngFlow(); return; }
        if (!CanRunSoftwareAction() || !CanRunSelfUpdateAction()) return;
        _softwareActionBusy = true;
        _gngFlowWorkspace = _workspace;
        _gngFlowSection = SetupWorkspace.SelectedSection;
        _gngResultRefreshed = false;
        try
        {
            var flow = new GngCleanupControl(_settings.EuroscopeDataPath, _settings.LastEuroscopeProfile,
                CleanupProtectedPaths(), recoveryFolder: _requestedActivityRecovery?.RecoveryFolder);
            _gngCleanupFlow = flow;
            flow.StateChanged += GngCleanupStateChanged;
            flow.CloseRequested += GngCleanupCloseRequested;
            GngFlowHost.Content = flow;
            UpdateSelfUpdateActionEnabled();
            GngCleanupStateChanged();
            await flow.StartAsync();
        }
        catch (Exception ex)
        {
            var message = "GNG cleanup could not finish: " + ex.Message;
            DismissGngFlow();
            LastCheckedText.Text = message;
        }
        finally
        {
            if (!HasGngFlow) _softwareActionBusy = false;
            if (!_windowClosed) UpdateSelfUpdateActionEnabled();
        }
    }

    private void GngCleanupStateChanged()
    {
        if (_windowClosed || _gngCleanupFlow is not { } flow) return;
        LastCheckedText.Text = flow.Status;
        _results[1].GngOperation = new(flow.Status, flow.IsBusy ? "Working…" : "Review cleanup",
            flow.IsBusy, NeedsAttention: true);
        SetupWorkspace.SetGngState(_results[1], flow.Status);
        UpdateGngPresentation();
        if (flow.IsBusy) _gngResultRefreshed = false;
        else if (flow.AttemptedChanges && !_gngResultRefreshed && !_gngRefreshInProgress)
        {
            _gngRefreshInProgress = true;
            _gngResultRefreshed = true;
            try
            {
                UpdateChecker.RefreshGngLocal(_results[1], _settings.EuroscopeDataPath);
                if (ReferenceEquals(_gngCleanupFlow, flow) && !_windowClosed) RefreshGngLocalState();
            }
            catch (Exception ex) { LastCheckedText.Text = "GNG result could not be refreshed: " + ex.Message; }
            finally { _gngRefreshInProgress = false; if (!_windowClosed) UpdateSelfUpdateActionEnabled(); }
        }
    }

    private IEnumerable<string> CleanupProtectedPaths()
    {
        yield return _settings.LastEuroscopeProfile;
        yield return _settings.VatEfsPath;
        if (_settings.SaamanProfileManaged || _settings.PatchSaaman)
            yield return string.IsNullOrWhiteSpace(_settings.SaamanProfilePath)
                ? Path.Combine(_settings.EuroscopeDataPath, SaamanProfileService.DefaultProfileName)
                : _settings.SaamanProfilePath;
        foreach (var path in GngProtectedPaths.ForUpdate(_settings)) yield return path;
        foreach (var path in SaamanProfileService.GetProtectedPaths(_settings)) yield return path;
    }
}
