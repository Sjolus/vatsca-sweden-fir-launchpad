using System.IO;
using System.Windows;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    private async Task RestoreSaamanFromActivityAsync(ActivityEntry entry)
    {
        if (!CanRunSoftwareAction() || !CanRunSelfUpdateAction() || entry.Operation is not
            (ActivityOperation.Install or ActivityOperation.Update or ActivityOperation.Restore)) return;
        var installer = new SaamanInstallService();
        try
        {
            var recovery = installer.FindRecovery(SaamanInstallService.GetSuggestedPath(_settings));
            if (recovery is null || !ActivityHistoryService.SameDataFolder(Path.GetDirectoryName(recovery.ReceiptPath), entry.RecoveryFolder))
                throw new InvalidOperationException("This is not the current restorable SAAMAN change. Select the latest update with a recovery copy.");
            EuroScopeProcessGuard.RequireClosed();
            if (MessageBox.Show(this, $"Restore SAAMAN {recovery.PreviousVersion}, replacing {recovery.Version}?\n\n" +
                "Your EuroScope profile and SAAMAN settings will be kept.", "Restore previous SAAMAN DLL",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _softwareActionBusy = true;
            UpdateSelfUpdateActionEnabled();
            ReportingEvents.Start(ReportingApplication.Saaman, ReportingOperation.Restore);
            try
            {
                var settings = _settings.Copy();
                await Task.Run(() => installer.Restore(recovery, settings));
                ReportingEvents.Complete(ReportingApplication.Saaman, ReportingOperation.Restore, ReportingOutcome.Completed);
                ActivityHistoryService.Record(ActivityApplication.Saaman, ActivityOperation.Restore, ActivityOutcome.Completed,
                    recovery.PreviousVersion, Path.GetDirectoryName(recovery.ReceiptPath), dataFolder: settings.EuroscopeDataPath,
                    previousVersion: recovery.Version);
                await RefreshSaamanLocalAsync();
                ActivityWorkspace.SetRecoveryState(true, false, "Previous SAAMAN DLL restored. Your profile and settings were kept.");
            }
            catch (Exception ex)
            {
                ReportingEvents.Stop(ex, ReportingApplication.Saaman, ReportingOperation.Restore, ReportingStage.Apply);
                ActivityWorkspace.SetRecoveryState(true, false, "Restore stopped: " + ex.Message);
            }
            finally { _softwareActionBusy = false; if (!_windowClosed) UpdateSelfUpdateActionEnabled(); }
        }
        catch (Exception ex) { ActivityWorkspace.SetRecoveryState(true, false, ex.Message); }
    }
}
