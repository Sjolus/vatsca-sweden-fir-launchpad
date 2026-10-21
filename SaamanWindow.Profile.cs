using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class SaamanWindow
{
    private bool _refreshingProfile;

    private void RefreshProfileChoice()
    {
        _refreshingProfile = true;
        ProfileEnabledCheck.IsChecked = Settings.PatchSaaman;
        _refreshingProfile = false;
        ProfileStateText.Text = Settings.PatchSaaman
            ? "Enabled · first plugin in this profile."
            : Settings.SaamanProfileManaged ? "Disabled in Launchpad."
            : "Not enabled by Launchpad.";
    }

    private void ProfileChoiceChanged(object sender, RoutedEventArgs e)
    {
        if (_refreshingProfile || ProfileStateText is null) return;
        if (_busy || RequiresRestart) { RefreshProfileChoice(); return; }
        bool enabled = ProfileEnabledCheck.IsChecked == true;
        if (enabled == Settings.PatchSaaman) return;
        ApplyLocalChange(() =>
        {
            // Selection validation checks the static plugin identity without loading it.
            // Apply additionally pins and rechecks the exact DLL bytes from this plan.
            EuroScopeProcessGuard.RequireClosed();
            if (enabled && !File.Exists(Settings.SaamanDllPath))
                throw new InvalidOperationException("Install SAAMAN or choose your existing DLL first, then enable it here.");
            var identity = enabled ? _installer.Adopt(Settings.SaamanDllPath, Settings) : null;
            var requested = Settings.Copy();
            requested.PatchSaaman = enabled;
            requested.SaamanProfileManaged = true;
            var policy = SaamanProfileService.Capture(requested, requested.EuroscopeDataPath)!;
            var profile = SaamanProfileService.BuildPlan(policy, requested.EuroscopeDataPath);
            if (identity is not null && !string.Equals(identity.Sha256, profile.DllHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("SAAMAN.dll changed while preparing the profile. Try enabling it again.");
            return (profile, null);
        }, ReportingOperation.ConfigurePluginProfile);
    }

    private void ShowRecoveryReview()
    {
        ReviewHeading.Text = "Restore previous DLL";
        ReviewPanel.Visibility = ConfirmChange.Visibility = CancelReviewButton.Visibility = RestoreButton.Visibility = Visibility.Visible;
        ConfirmChangeText.Text = "I have reviewed this restore.";
        ConfirmChange.IsChecked = false;
        UpdateConfirmation();
        ContentScroll.ScrollToBottom();
    }

    private void ApplyLocalChange(Func<(SaamanProfileChange? Profile, SaamanDllInfo? Selected)> prepare, ReportingOperation operation)
    {
        if (_busy || RequiresRestart) return;
        InvalidateReview();
        ReportingEvents.Start(ReportingApplication.Saaman, operation);
        SetBusy(true);
        CancelOperationButton.Visibility = Visibility.Collapsed;
        try
        {
            var (profile, selected) = prepare();
            if (selected is not null)
            {
                var current = _installer.Adopt(selected.Path, Settings);
                if (current != selected)
                    throw new IOException("The selected SAAMAN DLL changed while being checked. Choose it again.");
            }
            string? recovery = null;
            if (!MaintenanceLock.TryAcquire(out var lease))
                throw new InvalidOperationException("Another maintenance operation is active. Try again when it finishes.");
            using (lease)
            {
                if (profile is not null)
                {
                    GngUpdateService.RequireNoPendingUpdate(Settings.EuroscopeDataPath);
                    EuroScopeProcessGuard.RequireClosed();
                    recovery = SaamanProfileService.Apply(profile);
                }
                // A failed profile write must never claim the new path or enabled state.
                if (selected is not null) Settings.SaamanDllPath = selected.Path;
                if (profile is not null)
                {
                    Settings.PatchSaaman = profile.Policy.Enabled;
                    Settings.SaamanProfileManaged = true;
                    Settings.SaamanProfilePath = profile.Policy.ProfilePath;
                }
                SettingsChanged = true;
            }
            InvalidateReview();
            RefreshPaths();
            StatusText.Text = profile is null ? "SAAMAN copy selected. No files were changed."
                : profile.ProfileMissing ? "SAAMAN disabled in Launchpad. The missing profile was not changed."
                : selected is not null ? "SAAMAN location and profile updated. SAAMAN remains enabled."
                : Settings.PatchSaaman ? "SAAMAN enabled in this profile."
                : "SAAMAN disabled. Original MAESTRO entries were restored where previously replaced.";
            if (recovery is not null) StatusText.Text += "\nProfile recovery: " + recovery;
            bool saved = SaveChangedChoices();
            ReportingEvents.Complete(ReportingApplication.Saaman, operation,
                saved ? ReportingOutcome.Completed : profile is null ? ReportingOutcome.Failed : ReportingOutcome.Partial,
                !saved ? ReportingReason.SaveFailed : profile is { HasChanges: false } ? ReportingReason.AlreadyConfigured : null);
        }
        catch (Exception ex) { ReportingEvents.Stop(ex, ReportingApplication.Saaman, operation); InvalidateReview(); RefreshPaths(); StatusText.Text = ex.Message; }
        finally { SetBusy(false); }
    }

    private void CancelReview_Click(object sender, RoutedEventArgs e)
    {
        InvalidateReview();
        RefreshPaths();
        StatusText.Text = "Change cancelled. Nothing was applied.";
    }

    private bool SaveChangedChoices()
    {
        if (_saveChoices is null) return true;
        try { _saveChoices(Settings.Copy()); return true; }
        catch
        {
            ReportingService.RecordError(ReportingErrorCode.SettingsSaveFailed, new ReportingDetails
            { Application = ReportingApplication.Saaman, Operation = ReportingOperation.Save, Reason = ReportingReason.SaveFailed });
            StatusText.Text += "\nLaunchpad could not save these choices. Close this window to retry saving. Completed file changes have not been undone.";
            return false;
        }
    }

    private void ProfileInfo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.ToolTip is ToolTip tip)
        { tip.PlacementTarget = button; tip.IsOpen = !tip.IsOpen; }
    }

    private void ProfileInfo_Close(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is Button button && button.ToolTip is ToolTip tip) tip.IsOpen = false;
    }
}
