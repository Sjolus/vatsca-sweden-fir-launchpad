using System.Windows;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker
{
    // Only synthetic models are used by the linked production preview coordinator.
    public partial class GngUpdateControl
    {
        private GngUpdatePlan? _plan;
        private bool _closed;
        private bool _readingFilePreview;
        private bool _preparationCancelled;
        private bool _showingIssue;
        private bool _syntheticPreviewBusy;
        private bool CanGetPackage => !_syntheticPreviewBusy && !_readingFilePreview && !IsBusy && !_closed;

        private void SetBusy(bool busy, string? message = null, string? stage = null)
        {
            _syntheticPreviewBusy = busy;
            if (message is not null) StatusText.Text = message;
            if (stage is not null) SetStage(stage);
            FilesGrid.IsEnabled = FileSearchBox.IsEnabled = FileFilterBox.IsEnabled = !busy;
            InstallButton.IsEnabled = !busy && _plan is not null && _euroScopeBlockingReason is null;
            RefreshReviewButton.Visibility = !busy && _plan is null ? Visibility.Visible : Visibility.Collapsed;
            CloseButton.IsEnabled = !busy;
            UpdateReviewReadiness(InstallButton.IsEnabled);
            StateChanged?.Invoke();
        }
        private void SetStage(string stage) => DownloadStageText.Text = stage;
        private void ShowIssue(string title, string detail)
        {
            _showingIssue = true;
            SetStage(title);
            StatusText.Text = detail;
        }
        private void ShowNestedView(UIElement content)
        {
            NestedContent.Content = content;
            NestedContent.Visibility = Visibility.Visible;
            OperationBody.Visibility = Visibility.Collapsed;
        }
        private void CloseNestedView()
        {
            NestedContent.Content = null;
            NestedContent.Visibility = Visibility.Collapsed;
            OperationBody.Visibility = Visibility.Visible;
        }
        internal void PrepareSyntheticFileNavigation(GngUpdatePlan plan)
        {
            SetSyntheticState(GngUpdateFixtureState.Review);
            CancelFilePreviewNavigation();
            _plan = plan;
            _preparationCancelled = _showingIssue = _closed = false;
            SetBusy(false);
            SetReviewFiles(plan.Files);
            SetReviewAssessment(GngReviewNotes.Assess(plan));
            ShowReviewDetails(true);
            StatusText.Text = "Synthetic package ready.";
            SetStage("Ready to install");
        }
        internal void ReplaceSyntheticNavigationPlan(GngUpdatePlan plan, bool busy)
        {
            _plan = plan;
            SetBusy(busy, "Synthetic newer review owns this status.", "New review");
        }
        internal bool SyntheticNavigationHasPlan => _plan is not null;
        internal bool SyntheticNavigationBusy => _syntheticPreviewBusy;
        internal bool SyntheticNavigationHasError => _showingIssue;
        internal void CancelSyntheticNavigation()
        {
            CancelFilePreviewNavigation();
            _preparationCancelled = true;
            StatusText.Text = "Synthetic preview cancelled.";
        }
    }
}

namespace VatscaUpdateChecker.Services
{
    // The UI harness never links the filesystem GNG updater. This provider cannot read files.
    public static class GngUpdateService
    {
        public static GngFilePreview PreviewFile(GngUpdatePlan plan, string path)
        {
            var file = plan.Files.Single(file => file.RelativePath == path);
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || file.Action == "Keep personal file")
                return new(path, file.Action, "Synthetic summary only. No personal or binary contents were read.", null, null);
            string Text(bool after) => string.Join("\n", Enumerable.Range(1, 120).Select(line =>
                line == 5 ? "LastSession\tpassword\t[hidden]" :
                $"Setting {line:D3} · {path} · " + (line is 20 or 60 or 100 ? after ? "new package value" : "previous value" : "unchanged synthetic value"))) + "\n";
            return new(path, file.Action, "Synthetic already-sanitized file comparison. Three separated change groups; no external file was read.", Text(false), Text(true));
        }
    }
}
