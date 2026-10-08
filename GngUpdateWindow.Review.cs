using System.Windows;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

public partial class GngUpdateWindow
{
    private IReadOnlyList<GngUpdateFile> _reviewFiles = [];

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
