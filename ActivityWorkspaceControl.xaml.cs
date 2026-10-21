using System.Windows;
using System.Windows.Controls;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

public enum ActivityWorkspaceAction { OpenFolder, ReviewRestore, Report }
public sealed record ActivityWorkspaceRequest(ActivityWorkspaceAction Action, ActivityEntry Entry);

// Presentation only; recovery eligibility and actions stay with the owner and services.
public partial class ActivityWorkspaceControl : UserControl
{
    public event Action<ActivityWorkspaceRequest>? Requested;
    public event Action<ActivityEntry>? SelectionChanged;
    public ActivityWorkspaceControl() => InitializeComponent();
    public void SetHistory(IReadOnlyList<ActivityEntry> entries, string notice)
    {
        var selected = (HistoryGrid.SelectedItem as ActivityEntry)?.Id;
        HistoryNotice.Text = notice;
        HistoryGrid.ItemsSource = entries;
        HistoryGrid.SelectedItem = entries.FirstOrDefault(x => x.Id == selected) ?? entries.FirstOrDefault();
        if (entries.Count == 0) { SelectionHeading.Text = "No recorded changes"; SelectionGuidance.Text = "Installations, updates and recovery actions will appear here."; RecoveryPath.Visibility = OpenRecoveryButton.Visibility = GngRecoveryButton.Visibility = ReportButton.Visibility = Visibility.Collapsed; }
    }
    public void SetRecoveryState(bool folderAvailable, bool canRestore, string guidance)
    {
        OpenRecoveryButton.IsEnabled = folderAvailable;
        GngRecoveryButton.IsEnabled = canRestore;
        SelectionGuidance.Text = guidance;
    }
    private void History_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not ActivityEntry entry) return;
        SelectionHeading.Text = entry.Summary;
        SelectionGuidance.Text = entry.Guidance;
        RecoveryPath.Text = entry.RecoveryFolder ?? "";
        RecoveryPath.Visibility = OpenRecoveryButton.Visibility = entry.RecoveryFolder is null ? Visibility.Collapsed : Visibility.Visible;
        GngRecoveryButton.Visibility = (entry.Application == ActivityApplication.Gng ||
            entry.Application == ActivityApplication.Saaman && entry.PreviousVersion is not null && entry.Operation is ActivityOperation.Install or ActivityOperation.Update) &&
            entry.RecoveryFolder != null ? Visibility.Visible : Visibility.Collapsed;
        ReportButton.Visibility = Visibility.Visible;
        OpenRecoveryButton.IsEnabled = GngRecoveryButton.IsEnabled = false;
        SelectionChanged?.Invoke(entry);
    }
    private void Request(ActivityWorkspaceAction action) { if (HistoryGrid.SelectedItem is ActivityEntry entry) Requested?.Invoke(new(action, entry)); }
    private void OpenRecovery_Click(object sender, RoutedEventArgs e) => Request(ActivityWorkspaceAction.OpenFolder);
    private void GngRecovery_Click(object sender, RoutedEventArgs e) => Request(ActivityWorkspaceAction.ReviewRestore);
    private void Report_Click(object sender, RoutedEventArgs e) => Request(ActivityWorkspaceAction.Report);
}
