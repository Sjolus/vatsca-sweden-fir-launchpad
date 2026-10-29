using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class GngUpdateControl
{
    internal Func<GngUpdatePlan, string, Task<(GngFilePreview Preview, GngTextDiffResult? Diff)>> FilePreviewLoader { get; set; } =
        static (plan, path) => Task.Run(() =>
        {
            var preview = GngUpdateService.PreviewFile(plan, path);
            return (preview, preview.HasText ? GngTextDiff.Create(preview.BeforeText!, preview.AfterText!) : null);
        });

    private long _filePreviewRequest;
    private long _filePreviewReadRequest;
    private bool _filePreviewInFlight;
    private GngUpdatePlan? _filePreviewPlan;
    private GngUpdateFile[] _filePreviewFiles = [];
    private GngFileDiffControl? _filePreviewView;
    private int _filePreviewIndex;
    private string _filePreviewReturnStatus = string.Empty;
    private string _filePreviewReturnStage = string.Empty;

    private async void ViewFileChanges_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: GngUpdateFile file })
            await ShowFilePreviewAsync(file);
    }

    private async void FilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(FilesGrid, source) is not DataGridRow { Item: GngUpdateFile file }) return;
        e.Handled = true;
        await ShowFilePreviewAsync(file);
    }

    private async void FilesGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None || FilesGrid.SelectedItem is not GngUpdateFile file) return;
        e.Handled = true;
        await ShowFilePreviewAsync(file);
    }

    internal Task ShowFilePreviewAsync(GngUpdateFile file)
    {
        if (!CanGetPackage || _filePreviewInFlight || _plan is not { } plan) return Task.CompletedTask;
        // Items reflects both the current filter and the user's chosen column sorting.
        var plannedPaths = plan.Files.Select(planned => planned.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = FilesGrid.Items.OfType<GngUpdateFile>().Where(candidate => plannedPaths.Contains(candidate.RelativePath)).ToArray();
        int index = Array.FindIndex(files, candidate => string.Equals(candidate.RelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return Task.CompletedTask;
        _filePreviewPlan = plan;
        _filePreviewFiles = files;
        _filePreviewReturnStatus = StatusText.Text;
        _filePreviewReturnStage = DownloadStageText.Text;
        _preparationCancelled = false;
        _showingIssue = false;
        return LoadFilePreviewAsync(index);
    }

    internal Task NavigateFilePreviewAsync(int direction)
    {
        if (direction is not (-1 or 1) || !CanGetPackage || _filePreviewInFlight ||
            _filePreviewView is null || !ReferenceEquals(_plan, _filePreviewPlan)) return Task.CompletedTask;
        int index = _filePreviewIndex + direction;
        return index < 0 || index >= _filePreviewFiles.Length ? Task.CompletedTask : LoadFilePreviewAsync(index, direction);
    }

    private bool IsCurrentFilePreview(long request, GngUpdatePlan plan) =>
        !_closed && !_preparationCancelled && request == _filePreviewRequest &&
        ReferenceEquals(_plan, plan) && ReferenceEquals(_filePreviewPlan, plan);

    private async Task LoadFilePreviewAsync(int index, int focusDirection = 0)
    {
        if (_filePreviewPlan is not { } plan || _filePreviewInFlight) return;
        long request = ++_filePreviewRequest;
        _filePreviewReadRequest = request;
        _filePreviewInFlight = _readingFilePreview = true;
        _filePreviewView?.SetNavigationBusy(true);
        bool invalidatedReview = false;
        SetBusy(true, "Checking the selected file and preparing its expected changes…", "Preparing file comparison");
        try
        {
            var (preview, diff) = await FilePreviewLoader(plan, _filePreviewFiles[index].RelativePath);
            if (!IsCurrentFilePreview(request, plan)) return;
            // A fresh view prevents previous text, binary metadata or change positions leaking into the next file.
            var view = new GngFileDiffControl(preview, diff);
            view.SetFileNavigation(index, _filePreviewFiles.Length);
            view.SetNavigationBusy(true);
            view.CloseRequested += (_, _) =>
            {
                if (ReferenceEquals(_filePreviewView, view)) CancelFilePreviewNavigation();
            };
            view.FileNavigationRequested += async direction =>
            {
                if (ReferenceEquals(_filePreviewView, view)) await NavigateFilePreviewAsync(direction);
            };
            _filePreviewIndex = index;
            _filePreviewView = view;
            StatusText.Text = _filePreviewReturnStatus;
            SetStage(_filePreviewReturnStage);
            ShowNestedView(view);
            if (focusDirection != 0)
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    if (!IsCurrentFilePreview(request, plan) || !ReferenceEquals(_filePreviewView, view) || _filePreviewInFlight) return;
                    var button = view.FindName(focusDirection > 0 ? "NextFileButton" : "PreviousFileButton") as Button;
                    if (button?.IsEnabled != true) button = view.FindName("CloseButton") as Button;
                    button?.Focus();
                }));
        }
        catch (Exception)
        {
            if (!IsCurrentFilePreview(request, plan)) return;
            // Do not leave an old file covering the error or permit installation from a stale review.
            CancelFilePreviewNavigation();
            _plan = null;
            invalidatedReview = true;
            InvalidateReviewAssessment();
            ShowIssue("File comparison unavailable", "The file or package may have changed, or could not be read. Refresh changes to prepare a new review from the saved package.");
        }
        finally
        {
            if (_filePreviewReadRequest == request)
            {
                _filePreviewInFlight = _readingFilePreview = false;
                // A new package/review owns its own busy state; a late read must not clear it.
                if (!_closed && (ReferenceEquals(_plan, plan) || invalidatedReview)) SetBusy(false);
                _filePreviewView?.SetNavigationBusy(false);
            }
        }
    }

    internal void CancelFilePreviewNavigation()
    {
        _filePreviewRequest++;
        var view = _filePreviewView;
        var plan = _filePreviewPlan;
        _filePreviewView = null;
        _filePreviewPlan = null;
        _filePreviewFiles = [];
        if (view is not null && ReferenceEquals(NestedContent.Content, view)) CloseNestedView();
        // The grid is kept in place: search, sort, selected row and scroll offsets are not rebuilt.
        if (!_closed && !_showingIssue && plan is not null && ReferenceEquals(_plan, plan))
        {
            StatusText.Text = _filePreviewReturnStatus;
            SetStage(_filePreviewReturnStage);
        }
    }
}
