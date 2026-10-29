using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

public partial class GngUpdateControl
{
    private IReadOnlyList<GngUpdateFile> _reviewFiles = [];

    private GngReviewAssessment? _reviewAssessment;
    private bool _reviewIsCurrent;
    private bool _showingReviewDetails;
    public bool HasWarning => HasError || _reviewIsCurrent && _reviewAssessment?.NeedsAttention == true ||
        _euroScopeBlockingReason is not null && (ReviewPanel?.Visibility == Visibility.Visible || RestoreConfirmPanel?.Visibility == Visibility.Visible);

    private void SetPackageNotes(IReadOnlyList<string> warnings, string backupRoot)
    {
        WarningsText.Text = "Recovery copies: " + backupRoot + "\n\n" + string.Join(Environment.NewLine, warnings);
        WarningsText.ScrollToHome();
    }

    private void SetReviewAssessment(GngReviewAssessment assessment)
    {
        _reviewAssessment = assessment;
        _reviewIsCurrent = true;
        UpdateReviewReadiness(true);
    }

    private void InvalidateReviewAssessment(string? message = null)
    {
        _reviewAssessment = new(true, "Update needs attention", [message ?? "Files or the package may have changed. Refresh changes to check them again."]);
        _reviewIsCurrent = false;
        UpdateReviewReadiness(false);
    }

    private void UpdateReviewReadiness(bool canInstall)
    {
        if (AssessmentHeading is null || _reviewAssessment is null) return;
        var blocked = _euroScopeBlockingReason is not null;
        var uncertain = _reviewAssessment.NeedsAttention;
        AssessmentHeading.Text = blocked ? "Close EuroScope first" : !_reviewIsCurrent ? _reviewAssessment.Heading :
            !canInstall ? "Checking update…" : _reviewAssessment.Heading;
        AssessmentGlyph.Text = blocked || !_reviewIsCurrent || uncertain ? "!" : canInstall ? "✓" : "…";
        var color = blocked || !_reviewIsCurrent || uncertain ? "ApplicationWarningText" : canInstall ? "ApplicationSuccessText" : "ApplicationUpdateText";
        AssessmentHeading.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, color);
        AssessmentGlyph.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, color);
        var points = !_reviewIsCurrent ? _reviewAssessment.Points :
            blocked ? new[] { _euroScopeBlockingReason! }.Concat(uncertain ? _reviewAssessment.Points : []).ToArray() : _reviewAssessment.Points;
        AttentionText.Text = string.Join(Environment.NewLine, points.Select(point => "• " + point));
        AttentionText.Visibility = points.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SummaryInstructionText.Visibility = blocked || !_reviewIsCurrent ? Visibility.Visible : Visibility.Collapsed;
        SummaryInstructionText.Text = blocked ? "Close EuroScope, then choose Install update here." : !_reviewIsCurrent ? "Nothing will be installed until a new review is ready." :
            "Choose " + InstallButton.Content + " when you are ready.";
    }

    private void ViewDetails_Click(object sender, RoutedEventArgs e) => ShowReviewDetails(true, resetTab: true);
    private void ReviewInfo_Open(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ToolTip: ToolTip tip } target) return;
        tip.PlacementTarget = target;
        tip.IsOpen = true;
    }

    private void ReviewInfo_Close(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ToolTip: ToolTip tip }) tip.IsOpen = false;
    }

    private void ReviewInfo_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape || sender is not FrameworkElement { ToolTip: ToolTip { IsOpen: true } tip }) return;
        tip.IsOpen = false;
        e.Handled = true;
    }

    private void BackToReview_Click(object sender, RoutedEventArgs e) => ShowReviewDetails(false);
    private void ShowReviewDetails(bool show, bool resetTab = false)
    {
        if (!show) CancelFilePreviewNavigation();
        if (show && resetTab) FilesTab.IsSelected = true;
        FlowHeading.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        _showingReviewDetails = show;
        ReviewInformationScroll.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        ReviewDetailsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ViewDetailsButton.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        UpdateDetailsMaintenance();
        OtherOptions.IsExpanded = false;
        UpdateReviewContentLayout();
        if (!show) QueueReviewInformationStart();
    }

    private void OperationBody_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateReviewContentLayout();
    private void UpdateReviewContentLayout()
    {
        if (OperationContentRow is null || ReviewPanel is null || ActionFooter is null || FlowHeading is null) return;
        bool compact = ReviewPanel.Visibility == Visibility.Visible && !_showingReviewDetails;
        OperationContentRow.Height = compact ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        ReviewInformationScroll.MaxHeight = compact
            ? Math.Max(100, OperationBody.ActualHeight - FlowHeading.ActualHeight - ActionFooter.ActualHeight -
                (DownloadActivityPanel.Visibility == Visibility.Visible ? DownloadActivityPanel.ActualHeight : 0) - 36)
            : double.PositiveInfinity;
    }

    private bool _settingReviewFiles;
    private void SetReviewFiles(IReadOnlyList<GngUpdateFile> files)
    {
        _reviewFiles = files;
        _settingReviewFiles = true;
        FileSearchBox.Text = string.Empty;
        FileFilterBox.SelectedIndex = 0;
        FilesGrid.SelectedItem = null;
        FilesGrid.Items.SortDescriptions.Clear();
        foreach (var column in FilesGrid.Columns) column.SortDirection = null;
        _settingReviewFiles = false;
        UpdateFileReview();
        QueueReviewInformationStart();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!ReferenceEquals(_reviewFiles, files)) return;
            FindFileScrollViewer(FilesGrid)?.ScrollToTop();
        }));
    }

    private void DetailsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, DetailsTabs))
        {
            if (_readingFilePreview && !FilesTab.IsSelected) CancelFilePreviewNavigation();
            UpdateDetailsMaintenance();
        }
    }

    private void UpdateDetailsMaintenance()
    {
        if (OtherOptions is null || ReviewPanel is null || OptionsTab is null) return;
        OtherOptions.Visibility = _showingReviewDetails && OptionsTab.IsSelected ? Visibility.Visible : Visibility.Collapsed;
        if (OtherOptions.Visibility != Visibility.Visible) OtherOptions.IsExpanded = false;
    }

    private void FileSearchChanged(object sender, TextChangedEventArgs e) => UpdateFileReview();
    private void FileFilterChanged(object sender, SelectionChangedEventArgs e) => UpdateFileReview();

    private static ScrollViewer? FindFileScrollViewer(DependencyObject node)
    {
        if (node is ScrollViewer scroll) return scroll;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            if (FindFileScrollViewer(VisualTreeHelper.GetChild(node, index)) is { } child) return child;
        return null;
    }

    private void QueueReviewInformationStart()
    {
        // A new plan may still be hidden until its caller switches away from the browser.
        // Position after layout, without moving keyboard focus or the independent file list.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (ReviewPanel.Visibility != Visibility.Visible) return;
            ReviewInformationScroll.UpdateLayout();
            ReviewInformationScroll.ScrollToHome();
        }));
    }

    private void UpdateFileReview()
    {
        if (_settingReviewFiles || FilesGrid is null || NoFileChangesText is null || FileSearchBox is null || FileFilterBox is null) return;
        string search = FileSearchBox.Text.Trim();
        int filter = Math.Max(0, FileFilterBox.SelectedIndex);
        var matching = _reviewFiles.Where(file => filter == 3 || file.WritesFile);
        if (filter == 1) matching = matching.Where(file => file.RelativePath.EndsWith(".prf", StringComparison.OrdinalIgnoreCase));
        if (filter == 2) matching = matching.Where(file => file.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        if (search.Length > 0) matching = matching.Where(file => file.RelativePath.Contains(search, StringComparison.OrdinalIgnoreCase) || file.Action.Contains(search, StringComparison.OrdinalIgnoreCase));
        var visible = matching.ToArray();
        var selectedPath = (FilesGrid.SelectedItem as GngUpdateFile)?.RelativePath;
        var sorting = FilesGrid.Items.SortDescriptions.ToArray();
        FilesGrid.ItemsSource = visible;
        FilesGrid.Items.SortDescriptions.Clear();
        foreach (var sort in sorting) FilesGrid.Items.SortDescriptions.Add(sort);
        FilesGrid.SelectedItem = visible.FirstOrDefault(file => string.Equals(file.RelativePath, selectedPath, StringComparison.OrdinalIgnoreCase)) ?? visible.FirstOrDefault();
        FilesGrid.Visibility = visible.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        NoFileChangesText.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoFileChangesText.Text = _reviewFiles.Count == 0 ? "The file comparison is unavailable until the update checks succeed." : search.Length > 0 ? "No files match this search." :
            filter == 1 ? "No changed profiles in this update." : filter == 2 ? "No changed plugins in this update." : "All files already match. No installation is needed.";
        int total = filter == 3 ? _reviewFiles.Count : _reviewFiles.Count(file => file.WritesFile);
        FileSummaryText.Text = $"{visible.Length:N0} of {total:N0} {(filter == 3 ? "files" : "changes")}";
    }
}
