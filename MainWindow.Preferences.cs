using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    private PreferencesWorkspaceControl? _preferencesWorkspace;

    private void InitializePreferencesWorkspace(PreferencesWorkspaceControl control)
    {
        _preferencesWorkspace = control;
        control.PreferencesChanged += SaveWorkspacePreferences;
        control.RetrySaveRequested += () => SaveWorkspacePreferences(null);
        control.SetupGuideRequested += OpenGettingStarted;
        control.PrivacyRequested += () =>
        {
            if (CanOpenReporting()) ShowOwnedDialog(new ReportingPreferencesWindow { Owner = this });
        };
        control.ReportsRequested += () =>
        {
            if (CanOpenReporting()) ShowOwnedDialog(new ReportingHistoryWindow { Owner = this });
        };
        RefreshPreferencesWorkspace();
    }

    private void RefreshPreferencesWorkspace()
    {
        if (_settings is not null) _preferencesWorkspace?.SetPreferences(_settings);
    }

    private void SaveWorkspacePreferences(AppSettings? draft)
    {
        if (_preferencesWorkspace is null) return;
        if (!CanControlRunningApplication() || !TryAcquireMaintenanceGuard(out var lease))
        {
            RefreshPreferencesWorkspace();
            _preferencesWorkspace.SetSaveResult(false, "Finish the current operation, then change your preference again.");
            return;
        }
        using var guard = lease;
        if (draft is not null)
        {
            // Copy only this control's choices, preserving the current paths and controller settings.
            _settings.CheckOnStartup = draft.CheckOnStartup;
            _settings.TextScale = draft.TextScale;
            _settings.CompactLayout = draft.CompactLayout;
            _settings.AlwaysOnTop = draft.AlwaysOnTop;
            foreach (var name in ApplicationPreferences.OptionalApplications)
                ApplicationPreferences.SetNotUsed(_settings, name, ApplicationPreferences.IsNotUsed(draft, name));
        }
        ApplyPresentationPreferences();
        bool saved = SaveSessionSettings("Preferences saved.");
        if (!saved) LastCheckedText.Text = "Preferences apply for this session. Try saving again in Setup → Preferences.";
        _preferencesWorkspace.SetSaveResult(saved);
        RefreshBulkActionAvailability();
    }
}
