using System.Windows;
using System.Windows.Controls;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

// Production XAML and display model only: no history store, shell actions or recovery services.
public partial class ActivityWindow : Window
{
    private void ReportProblem_Click(object sender, RoutedEventArgs e) { }
    public ActivityWindow()
    {
        InitializeComponent();
        UiFixture.Initialize(this);
    }

    internal void RefreshSyntheticScenario()
    {
        SelectionHeading.Text = SelectionGuidance.Text = RecoveryGuidance.Text = RecoveryPath.Text = "";
        RecoveryPath.Visibility = OpenRecoveryButton.Visibility = GngRecoveryButton.Visibility = Visibility.Collapsed;
        HistoryNotice.Text = UiFixture.Blank ? "No changes recorded yet. Future installations, updates, removals and recovery actions will appear here."
            : "Activity starts when this feature is installed. Older backups remain available through the GNG and removal tools.";
        HistoryGrid.ItemsSource = UiFixture.Blank ? [] : new[]
        {
            new ActivityEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, ActivityApplication.Gng, ActivityOperation.Install,
                ActivityOutcome.Completed, "2610/01 rev.3", @"C:\Synthetic\AppData\VatscaUpdateChecker\Gng\Installations\dated-backup", @"C:\Synthetic\EuroScope"),
            new ActivityEntry(Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-1), ActivityApplication.VatEfs, ActivityOperation.Update,
                ActivityOutcome.NeedsAttention, "0.0.15", @"C:\Synthetic\VatEfsRecovery\dated-backup", RestartRequired: true, PreviousVersion: "0.0.14"),
            new ActivityEntry(Guid.NewGuid(), DateTimeOffset.UtcNow.AddDays(-2), ActivityApplication.TrackAudio, ActivityOperation.Update,
                ActivityOutcome.Cancelled)
        };
        if (!UiFixture.Blank) HistoryGrid.SelectedIndex = 0;
    }

    private void History_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not ActivityEntry entry) return;
        SelectionHeading.Text = entry.Summary;
        SelectionGuidance.Text = entry.Guidance + (entry.RestartRequired
            ? " Windows requested a restart at that time. This history cannot tell whether Windows has restarted since." : "");
        var gng = entry.Application == ActivityApplication.Gng && entry.RecoveryFolder is not null;
        RecoveryGuidance.Text = entry.RecoveryFolder is null ? "No recovery folder was recorded for this action."
            : gng ? "GNG recovery opens its existing review window with this backup selected. Choose Restore backup there; files are checked again before any changes. An unfinished recovery takes priority."
            : "Open this folder and read RESTORE.txt if provided. Vendor recovery is manual; copying old files over an installed application can break it.";
        RecoveryPath.Text = entry.RecoveryFolder ?? "";
        RecoveryPath.Visibility = OpenRecoveryButton.Visibility = entry.RecoveryFolder is null ? Visibility.Collapsed : Visibility.Visible;
        GngRecoveryButton.Visibility = gng ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenRecovery_Click(object sender, RoutedEventArgs e) => RecoveryGuidance.Text = "Synthetic folder action only. No folder opens.";
    private void GngRecovery_Click(object sender, RoutedEventArgs e) => RecoveryGuidance.Text = "Synthetic recovery action only. No recovery runs.";
}
