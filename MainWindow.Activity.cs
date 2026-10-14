using System.Windows;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    private ActivityEntry? _requestedActivityRecovery;
    private void InitializeActivityHistory()
    {
        ActivityHistoryService.Changed += ActivityHistoryChanged;
        Closed += (_, _) => ActivityHistoryService.Changed -= ActivityHistoryChanged;
        RefreshActivityNotice();
    }

    private void ActivityHistoryChanged()
    {
        if (_windowClosed) return;
        if (Dispatcher.CheckAccess()) RefreshActivityNotice();
        else Dispatcher.InvokeAsync(() => { if (!_windowClosed) RefreshActivityNotice(); });
    }

    private void RefreshActivityNotice()
    {
        var history = ActivityHistoryService.Read();
        var latest = history.Entries.FirstOrDefault();
        ActivityNotice.Text = ActivityHistoryService.LastWriteFailed
            ? "The latest outcome could not be saved to Activity. Keep any recovery location shown in the operation window."
            : history.Unavailable ? "Saved activity could not be read. Recovery backups are unchanged."
            : latest is null ? "" : $"{latest.DisplayTime} · {latest.Summary}";
        ActivityNotice.Visibility = string.IsNullOrEmpty(ActivityNotice.Text) ? Visibility.Collapsed : Visibility.Visible;
        ActivityNotice.ToolTip = "Open Activity to see recent changes and recovery options.";
    }

    private void Activity_Click(object sender, RoutedEventArgs e)
    {
        if (_windowClosed || _softwareActionBusy || _softwareUpdates.IsBusy || _selfUpdateActionBusy || _openDialogCount > 0 || !IsEnabled) return;
        var window = new ActivityWindow(_settings.EuroscopeDataPath, !_setupRestartRequired) { Owner = this };
        ShowOwnedDialog(window);
        RefreshActivityNotice();
        if (window.ReportRequested is { } problem)
        {
            OpenProblemReport(ReportingApplicationFor(problem.ApplicationName), problem.Application == ActivityApplication.Gng
                ? ReportingCategory.Gng : problem.Operation == ActivityOperation.Update ? ReportingCategory.Update
                : problem.Operation == ActivityOperation.Install ? ReportingCategory.Installation : ReportingCategory.Other);
            return;
        }
        if (window.RecoveryRequested is not { } requested ||
            !ActivityHistoryService.SameDataFolder(requested.DataFolder, _settings.EuroscopeDataPath)) return;
        // Opening these windows never starts recovery. Their original review, process and
        // maintenance guards remain authoritative, including the final explicit confirmation.
        _requestedActivityRecovery = requested;
        try
        {
            if (requested.Operation is ActivityOperation.Cleanup or ActivityOperation.RestoreCleanup)
                GngCleanup_Click(sender, e);
            else GngUpdate_Click(sender, e);
        }
        finally { _requestedActivityRecovery = null; }
    }
}
