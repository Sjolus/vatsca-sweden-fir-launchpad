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
        if (_workspace != "Apps") ActivityNotice.Visibility = Visibility.Collapsed;
        if (_workspace == "Setup" && SetupWorkspace.SelectedSection == "Activity") RefreshWorkspace();
    }

    private void Activity_Click(object sender, RoutedEventArgs e)
    {
        ShowWorkspace("Activity");
    }

}
