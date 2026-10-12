using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class ActivityWindow : Window
{
    private readonly string _gngDataFolder;
    private readonly bool _recoveryAllowed;
    public ActivityEntry? RecoveryRequested { get; private set; }

    public ActivityWindow(string gngDataFolder, bool recoveryAllowed = true)
    {
        InitializeComponent();
        _gngDataFolder = gngDataFolder;
        _recoveryAllowed = recoveryAllowed;
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        var history = ActivityHistoryService.Read();
        HistoryNotice.Text = history.Unavailable ? "Saved activity could not be read. Existing backups have not been changed."
            : ActivityHistoryService.LastWriteFailed ? "The last outcome could not be saved. Keep any recovery folder shown in the operation's window."
            : history.Entries.Count == 0 ? "No changes recorded yet. Future installations, updates, removals and recovery actions will appear here."
            : "Activity starts when this feature is installed. Older backups remain available through the GNG and removal tools.";
        HistoryGrid.ItemsSource = history.Entries;
        if (history.Entries.Count > 0) HistoryGrid.SelectedIndex = 0;
    }

    private void History_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not ActivityEntry entry) return;
        SelectionHeading.Text = entry.Summary;
        SelectionGuidance.Text = entry.Guidance + (entry.RestartRequired
            ? " Windows requested a restart at that time. This history cannot tell whether Windows has restarted since." : "");
        RecoveryPath.Text = entry.RecoveryFolder ?? "";
        RecoveryPath.Visibility = entry.RecoveryFolder is null ? Visibility.Collapsed : Visibility.Visible;
        var available = ActivityHistoryService.CanOpenRecoveryFolder(entry.RecoveryFolder);
        OpenRecoveryButton.Visibility = entry.RecoveryFolder is null ? Visibility.Collapsed : Visibility.Visible;
        OpenRecoveryButton.IsEnabled = available;
        var gng = entry.Application == ActivityApplication.Gng && entry.RecoveryFolder is not null;
        GngRecoveryButton.Visibility = gng ? Visibility.Visible : Visibility.Collapsed;
        GngRecoveryButton.IsEnabled = gng && available && _recoveryAllowed && ActivityHistoryService.SameDataFolder(entry.DataFolder, _gngDataFolder);
        RecoveryGuidance.Text = entry.RecoveryFolder is null ? "No recovery folder was recorded for this action."
            : !available ? "This recovery folder is missing or cannot be opened safely. It may have been moved or removed."
            : gng ? "GNG recovery opens its existing review window with this backup selected. Choose Restore backup there; files are checked again before any changes. An unfinished recovery takes priority." +
                (!_recoveryAllowed ? " Restart Windows before further recovery changes." :
                    GngRecoveryButton.IsEnabled ? "" : " First select the original EuroScope data folder in App settings.")
            : "Open this folder and read RESTORE.txt if provided. Vendor recovery is manual; copying old files over an installed application can break it.";
    }

    private void OpenRecovery_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not ActivityEntry entry ||
            !ActivityHistoryService.CanOpenRecoveryFolder(entry.RecoveryFolder))
        { RecoveryGuidance.Text = "The recovery folder is no longer available."; return; }
        try { Process.Start(new ProcessStartInfo(entry.RecoveryFolder!) { UseShellExecute = true }); }
        catch { RecoveryGuidance.Text = "Windows could not open the recovery folder. You can copy its location above."; }
    }

    private void GngRecovery_Click(object sender, RoutedEventArgs e)
    {
        if (!_recoveryAllowed || HistoryGrid.SelectedItem is not ActivityEntry { Application: ActivityApplication.Gng } entry ||
            !ActivityHistoryService.CanOpenRecoveryFolder(entry.RecoveryFolder) ||
            !ActivityHistoryService.SameDataFolder(entry.DataFolder, _gngDataFolder)) return;
        RecoveryRequested = entry;
        DialogResult = true;
    }
}
