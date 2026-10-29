using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

internal static class GngFileNavigationTests
{
    internal static int Run()
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        int count = 0;
        try
        {
            Check("filtered sorted 150-file navigation is lazy and preserves the list", (view, plan) =>
            {
                var grid = Element<DataGrid>(view, "FilesGrid");
                Element<TextBox>(view, "FileSearchBox").Text = "File0";
                Element<ComboBox>(view, "FileFilterBox").SelectedIndex = 0;
                var filtered = grid.ItemsSource;
                grid.Items.SortDescriptions.Add(new SortDescription(nameof(GngUpdateFile.RelativePath), ListSortDirection.Descending));
                var displayed = grid.Items.OfType<GngUpdateFile>().ToArray();
                Require(displayed.Length == 100, "Search did not filter the 150-file synthetic list.");
                grid.SelectedItem = displayed[2];
                grid.UpdateLayout(); Drain();
                var listScroll = LayoutValidation.Descendants(grid).OfType<ScrollViewer>().First();
                listScroll.ScrollToVerticalOffset(12); Drain();
                double offset = listScroll.VerticalOffset;
                Require(offset > 0, "The 150-file fixture did not exercise a scrolled list.");
                int reads = 0;
                view.FilePreviewLoader = (currentPlan, path) => { reads++; return Task.FromResult(Preview(currentPlan, path)); };
                Await(view.ShowFilePreviewAsync(displayed[2]));
                Require(reads == 1 && CurrentPath(view) == displayed[2].RelativePath, "Initial navigation eagerly read other files.");
                Await(view.NavigateFilePreviewAsync(1));
                Require(reads == 2 && CurrentPath(view) == displayed[3].RelativePath, "Next ignored displayed filtering/sorting.");
                Await(view.NavigateFilePreviewAsync(-1));
                Require(CurrentPath(view) == displayed[2].RelativePath, "Previous did not return to displayed predecessor.");
                Back(view);
                grid.UpdateLayout(); Drain();
                Require(ReferenceEquals(grid.ItemsSource, filtered) && ReferenceEquals(grid.SelectedItem, displayed[2]) &&
                    grid.Items.SortDescriptions.Count == 1 && grid.Items.OfType<GngUpdateFile>().SequenceEqual(displayed) &&
                    Element<TextBox>(view, "FileSearchBox").Text == "File0" && Element<ComboBox>(view, "FileFilterBox").SelectedIndex == 0 &&
                    Math.Abs(listScroll.VerticalOffset - offset) < 0.1, "Back rebuilt list/filter/sort/selection or lost scroll position.");
                Await(view.ShowFilePreviewAsync(displayed[0]));
                int beforeBoundary = reads; Await(view.NavigateFilePreviewAsync(-1));
                Require(reads == beforeBoundary, "Previous crossed the first displayed file.");
                Back(view); Await(view.ShowFilePreviewAsync(displayed[^1]));
                beforeBoundary = reads; Await(view.NavigateFilePreviewAsync(1));
                Require(reads == beforeBoundary, "Next crossed the final displayed file.");
            });
            Check("Back suppresses delayed Next success and keeps one read in flight", (view, plan) =>
            {
                Await(view.ShowFilePreviewAsync(plan.Files[0]));
                var pending = Pending(); int reads = 0;
                view.FilePreviewLoader = (_, _) => { reads++; return pending.Task; };
                var next = view.NavigateFilePreviewAsync(1);
                Require(!next.IsCompleted && view.SyntheticNavigationBusy, "Delayed Next did not acquire busy state.");
                Await(view.NavigateFilePreviewAsync(1));
                Require(reads == 1, "Rapid Next started a parallel read.");
                Back(view);
                Require(CurrentView(view) is null && view.SyntheticNavigationBusy, "Back did not close the child or released an unfinished read.");
                Await(view.ShowFilePreviewAsync(plan.Files[2]));
                Require(reads == 1, "Back allowed another read before the old one finished.");
                pending.SetResult(Preview(plan, plan.Files[1].RelativePath)); Await(next);
                Require(CurrentView(view) is null && !view.SyntheticNavigationBusy && view.SyntheticNavigationHasPlan && !view.SyntheticNavigationHasError,
                    "Late success reopened a closed preview or invalidated its review.");
            });
            Check("cancellation suppresses a delayed preview failure", (view, plan) =>
            {
                Await(view.ShowFilePreviewAsync(plan.Files[0]));
                var pending = Pending(); view.FilePreviewLoader = (_, _) => pending.Task;
                var next = view.NavigateFilePreviewAsync(1);
                view.CancelSyntheticNavigation();
                pending.SetException(new InvalidOperationException("synthetic failure after cancellation")); Await(next);
                Require(CurrentView(view) is null && !view.SyntheticNavigationHasError && view.SyntheticNavigationHasPlan &&
                    view.Status == "Synthetic preview cancelled." && !view.SyntheticNavigationBusy, "Late failure replaced the cancellation state.");
            });
            Check("stale Next removes the old diff and disables installation", (view, plan) =>
            {
                Await(view.ShowFilePreviewAsync(plan.Files[0]));
                view.FilePreviewLoader = (_, _) => Task.FromException<(GngFilePreview Preview, GngTextDiffResult? Diff)>(new InvalidOperationException("synthetic stale package"));
                Await(view.NavigateFilePreviewAsync(1));
                Require(CurrentView(view) is null && Element<Grid>(view, "OperationBody").Visibility == Visibility.Visible,
                    "The preceding file hid the stale-review error.");
                Require(!view.SyntheticNavigationHasPlan && view.SyntheticNavigationHasError && !Element<Button>(view, "InstallButton").IsEnabled &&
                    view.Status.Contains("Refresh changes", StringComparison.Ordinal), "A stale preview retained approval or lost its recovery action.");
            });
            foreach (bool fail in new[] { false, true })
                Check("new plan ignores late " + (fail ? "failure" : "success") + " and retains its busy state", (view, plan) =>
                {
                    var pending = Pending(); view.FilePreviewLoader = (_, _) => pending.Task;
                    var old = view.ShowFilePreviewAsync(plan.Files[0]);
                    view.ReplaceSyntheticNavigationPlan(plan with { PackageName = "synthetic-new-plan.zip" }, busy: true);
                    if (fail) pending.SetException(new InvalidOperationException("synthetic obsolete failure"));
                    else pending.SetResult(Preview(plan, plan.Files[0].RelativePath));
                    Await(old);
                    Require(CurrentView(view) is null && view.SyntheticNavigationHasPlan && !view.SyntheticNavigationHasError &&
                        view.SyntheticNavigationBusy && view.Status == "Synthetic newer review owns this status.", "Old preview replaced the new review state.");
                });
            Check("text binary personal and text transitions use independent views", (view, plan) =>
            {
                var files = new GngUpdateFile[]
                {
                    new("A.prf", "Merge profile", "synthetic text"),
                    new("B.dll", "Promote plugin", "synthetic binary"),
                    new("C.txt", "Keep personal file", "synthetic personal"),
                    new("D.txt", "Replace package default", "synthetic text")
                };
                plan = plan with { Files = files };
                view.PrepareSyntheticFileNavigation(plan);
                Element<ComboBox>(view, "FileFilterBox").SelectedIndex = 3;
                Await(view.ShowFilePreviewAsync(files[0]));
                var first = CurrentView(view)!;
                Require(Element<TextBox>(first, "CurrentText").Text.Contains("A.prf", StringComparison.Ordinal), "Text preview absent.");
                Await(view.NavigateFilePreviewAsync(1));
                var binary = CurrentView(view)!;
                Require(!ReferenceEquals(first, binary) && Element<TabItem>(binary, "ChangesTab").Visibility == Visibility.Collapsed && Element<TabItem>(binary, "InfoTab").IsSelected &&
                    Element<TextBox>(binary, "CurrentText").Text.Length == 0, "Binary preview retained preceding text.");
                Await(view.NavigateFilePreviewAsync(1));
                var personal = CurrentView(view)!;
                Require(Element<TabItem>(personal, "ChangesTab").Visibility == Visibility.Collapsed && Element<TabItem>(personal, "InfoTab").IsSelected &&
                    Element<TextBox>(personal, "AfterText").Text.Length == 0, "Personal file exposed preceding text.");
                Await(view.NavigateFilePreviewAsync(1));
                var last = CurrentView(view)!;
                Require(Element<TabControl>(last, "PreviewTabs").Visibility == Visibility.Visible && Element<RowDefinition>(last, "HeaderRow").Height.IsAuto &&
                    Element<TextBox>(last, "CurrentText").Text.Contains("D.txt", StringComparison.Ordinal) &&
                    !Element<TextBox>(last, "CurrentText").Text.Contains("A.prf", StringComparison.Ordinal), "Text navigation retained summary-only layout or earlier contents.");
            });
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        return count;

        void Check(string name, Action<GngUpdateControl, GngUpdatePlan> action)
        {
            var window = new GngUpdateWindow();
            try
            {
                var plan = new GngUpdatePlan("synthetic.zip", "2610/01 rev.3", window.Update.DataFolder, "synthetic.zip",
                    Enumerable.Range(0, 150).Select(index => new GngUpdateFile($"ESAA/Settings/File{index:D3}.txt", "Replace package default", "Synthetic file only.")).ToArray(), [])
                    { ReviewContext = new GngReviewContext { HasExistingFiles = true, HasVerifiedBaseline = true } };
                window.Update.PrepareSyntheticFileNavigation(plan);
                LayoutValidation.CheckWindowContent(window);
                action(window.Update, plan);
                count++;
            }
            catch (Exception error) { throw new InvalidOperationException("GNG file navigation: " + name + ": " + error.Message, error); }
            finally { window.Close(); }
        }
    }

    private static TaskCompletionSource<(GngFilePreview Preview, GngTextDiffResult? Diff)> Pending() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static (GngFilePreview Preview, GngTextDiffResult? Diff) Preview(GngUpdatePlan plan, string path)
    {
        var preview = GngUpdateService.PreviewFile(plan, path);
        return (preview, preview.HasText ? GngTextDiff.Create(preview.BeforeText!, preview.AfterText!) : null);
    }
    private static GngFileDiffControl? CurrentView(GngUpdateControl view) => Element<ContentControl>(view, "NestedContent").Content as GngFileDiffControl;
    private static string CurrentPath(GngUpdateControl view) => Element<TextBox>(CurrentView(view)!, "FilePathText").Text;
    private static void Back(GngUpdateControl view) => Element<Button>(CurrentView(view)!, "CloseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static T Element<T>(FrameworkElement view, string name) where T : class => view.FindName(name) as T ?? throw new InvalidOperationException("Missing fixture element " + name);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Await(Task task)
    {
        if (!task.IsCompleted)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var frame = new DispatcherFrame();
            var timeout = new DispatcherTimer(DispatcherPriority.Send, dispatcher) { Interval = TimeSpan.FromSeconds(10) };
            timeout.Tick += (_, _) => frame.Continue = false;
            _ = task.ContinueWith(_ => dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
            timeout.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timeout.Stop(); }
        }
        Require(task.IsCompleted, "Synthetic preview did not finish within the test limit.");
        task.GetAwaiter().GetResult();
    }
}
