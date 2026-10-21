using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    private bool _controllerCredentialLoadFailed;
    private bool _controllerApplyNeedsAttention;
    private void InitializeControllerProfileWorkspace()
    {
        SetupWorkspace.ControllerProfile.AcquireAction = AcquireControllerProfileAction;
        SetupWorkspace.ControllerProfile.Applied += ControllerProfileApplied;
        SetupWorkspace.ControllerProfile.RetryLoadRequested += () =>
        {
            if (!CanRunSoftwareAction()) return;
            _controllerCredentialLoadFailed = false;
            RefreshControllerProfileWorkspace();
        };
    }

    private void RefreshControllerProfileWorkspace()
    {
        var editor = SetupWorkspace.ControllerProfile;
        if (!editor.HasDraft)
        {
            if (_workspace != "Setup" || SetupWorkspace.SelectedSection != "Profile" || _controllerCredentialLoadFailed) return;
            string password = "", hoppie = "";
            try
            {
                password = CredentialManagerService.LoadForEditing(CredentialManagerService.TargetVatsim) ?? "";
                hoppie = CredentialManagerService.LoadForEditing(CredentialManagerService.TargetHoppie) ?? "";
            }
            catch
            {
                _controllerCredentialLoadFailed = true;
                editor.SetLoadUnavailable();
                return;
            }
            editor.Initialize(_settings, _settings.EuroscopeDataPath, password, hoppie);
        }
        else editor.RefreshContext(_settings);
    }

    private void DiscardControllerProfileDraft()
    {
        SetupWorkspace.ControllerProfile.DiscardDraft();
        _controllerCredentialLoadFailed = false;
        _controllerApplyNeedsAttention = false;
    }

    private IDisposable? AcquireControllerProfileAction()
    {
        if (!CanRunSoftwareAction() || !CanRunSelfUpdateAction() || !TryAcquireMaintenanceGuard(out var lease)) return null;
        _softwareActionBusy = true;
        UpdateSelfUpdateActionEnabled();
        return new ControllerProfileActionLease(this, lease!);
    }

    private void ControllerProfileApplied(AppSettings edited, string message, bool needsAttention)
    {
        // Other Setup sections may have changed since this draft was opened. Never replace their settings.
        _settings.VatsimName = edited.VatsimName;
        _settings.VatsimRating = edited.VatsimRating;
        _settings.VatsimCid = edited.VatsimCid;
        _settings.ObsCallsign = edited.ObsCallsign;
        ReportingEvents.Start(ReportingApplication.Launchpad, ReportingOperation.Save);
        bool saved = SaveSessionSettings(message);
        ReportingEvents.Complete(ReportingApplication.Launchpad, ReportingOperation.Save,
            !saved ? ReportingOutcome.Failed : needsAttention ? ReportingOutcome.Partial : ReportingOutcome.Completed,
            saved ? null : ReportingReason.SaveFailed);
        _controllerApplyNeedsAttention = needsAttention || !saved;
        SetupWorkspace.ControllerProfile.SetPersistenceResult(saved, message);
        UpdateAppConfigButton();
        RefreshWorkspace();
    }

    private sealed class ControllerProfileActionLease(MainWindow owner, IDisposable lease) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lease.Dispose();
            owner._softwareActionBusy = false;
            if (!owner._windowClosed) owner.UpdateSelfUpdateActionEnabled();
        }
    }
}
