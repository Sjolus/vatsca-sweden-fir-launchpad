using System.Windows;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

public partial class GngUpdateWindow
{
    private IReadOnlyList<GngUpdateFile> _reviewFiles = [];

    private void SetPackageNotes(IReadOnlyList<string> warnings, string backupRoot)
    {
        var attention = GngReviewNotes.ForAttention(warnings);
        var remaining = warnings.Except(attention).ToArray();
        WarningsText.Text = (attention.Count == 0 ? "" : "Notes for this installation\n" + string.Join(Environment.NewLine, attention) + "\n\n") +
            "Recovery backups: " + backupRoot + "\n\nPackage rules\n" + string.Join(Environment.NewLine, remaining);
        WarningsText.ScrollToHome();
        ReviewDetails.Header = attention.Count == 0 ? "Package rules and full recovery details" :
            $"Review {attention.Count} package {(attention.Count == 1 ? "note" : "notes")} and recovery details";
        ReviewDetails.IsExpanded = attention.Count != 0;
        QueueReviewInformationStart();
    }

    private void SetReviewFiles(IReadOnlyList<GngUpdateFile> files)
    {
        _reviewFiles = files;
        var changes = files.Count(file => file.WritesFile);
        var kept = files.Count(file => file.Action == "Keep personal file");
        var unchanged = files.Count - changes - kept;
        FileSummaryText.Text = $"{changes:N0} {(changes == 1 ? "file" : "files")} to change · {unchanged:N0} unchanged" +
            (kept == 0 ? string.Empty : $" · {kept:N0} personal {(kept == 1 ? "file" : "files")} kept");
        ShowUnchangedFiles.IsEnabled = changes < files.Count;
        ShowUnchangedFiles.IsChecked = false;
        UpdateFileReview();
        QueueReviewInformationStart();
    }

    private void ReviewDetails_Expanded(object sender, RoutedEventArgs e) => QueueReviewInformationStart();

    private void QueueReviewInformationStart()
    {
        // A new plan may still be hidden until its caller switches away from the browser.
        // Position after layout, without moving keyboard focus or the independent file list.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (ReviewPanel.Visibility != Visibility.Visible) return;
            ReviewInformationScroll.UpdateLayout();
            FrameworkElement first = ReviewDetails.IsExpanded ? WarningsText : ReviewSummaryText;
            var top = first.TranslatePoint(new Point(), ReviewInformationScroll).Y;
            ReviewInformationScroll.ScrollToVerticalOffset(ReviewInformationScroll.VerticalOffset + top);
        }));
    }

    private void ReviewFilterChanged(object sender, RoutedEventArgs e) => UpdateFileReview();

    private void UpdateFileReview()
    {
        if (FilesGrid is null || NoFileChangesText is null) return;
        // Filter only the display. Installation still uses the complete, validated plan.
        var visibleFiles = ShowUnchangedFiles.IsChecked == true
            ? _reviewFiles
            : _reviewFiles.Where(file => file.WritesFile).ToArray();
        FilesGrid.ItemsSource = visibleFiles;
        FilesGrid.Visibility = visibleFiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        NoFileChangesText.Visibility = visibleFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
