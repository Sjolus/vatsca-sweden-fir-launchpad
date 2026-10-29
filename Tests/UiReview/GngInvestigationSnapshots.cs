using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

internal static class GngInvestigationFixture
{
    internal static GngFileDiffWindow CompactDiff()
    {
        var plan = GngUpdateWindow.CreateInvestigationPlan();
        var preview = GngUpdateService.PreviewFile(plan, plan.Files[0].RelativePath);
        var window = new GngFileDiffWindow(preview, GngTextDiff.Create(preview.BeforeText!, preview.AfterText!))
        { Width = 616, Height = 440 };
        window.Preview.SetFileNavigation(0, plan.Files.Count(file => file.WritesFile));
        return window;
    }

    internal static void JumpToChange(Window window)
    {
        LayoutValidation.CheckWindowContent(window);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        LayoutValidation.CheckWindowContent(window);
        ((Button)window.FindName("NextChangeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        LayoutValidation.CheckWindowContent(window);
    }
}

internal static partial class LayoutMatrix
{
    private static int CheckCompactGngChangeNavigation()
    {
        UiFixture.SetTextScale(1.5);
        var window = GngInvestigationFixture.CompactDiff();
        try
        {
            GngInvestigationFixture.JumpToChange(window);
            var text = Required<RichTextBox>(window, "DiffText");
            var line = text.Selection.Start.Paragraph!;
            Require(new TextRange(line.ContentStart, line.ContentEnd).Text.StartsWith('-'),
                "Compact Next change must select the first changed line.");
            var bounds = line.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            Require(!bounds.IsEmpty && bounds.Top >= 0 && bounds.Bottom <= text.ActualHeight,
                $"Next change must show its changed line at 150% text, not only select it: {bounds}; viewport {text.ActualHeight}.");
            return 1;
        }
        finally { window.Close(); UiFixture.SetTextScale(1); }
    }
}

internal static partial class LayoutSnapshots
{
    private static void CaptureEmbeddedGngInvestigation(string output)
    {
        if (!UiFixture.Dark) UiFixture.ToggleTheme();
        UiFixture.Blank = UiFixture.Minimum = false;
        UiFixture.Scale = 1;
        UiFixture.SetTextScale(1);
        var main = new MainWindow { Tag = "Compact" };
        try
        {
            var flow = new GngUpdateControl();
            flow.PrepareSyntheticFileNavigation(GngUpdateWindow.CreateInvestigationPlan());
            main.ShowSyntheticWorkspace("Apps");
            ((FrameworkElement)main.FindName("AppsWorkspace")).Visibility = Visibility.Collapsed;
            var host = (ContentControl)main.FindName("GngFlowHost");
            host.Content = flow;
            host.Visibility = Visibility.Visible;
            foreach (var name in new[] { "AppBulkActions", "CheckButton", "MaintenanceButton" })
                ((FrameworkElement)main.FindName(name)).Visibility = Visibility.Collapsed;
            Save(main, System.IO.Path.Combine(output, "dark-investigate-main-default-100.png"));
        }
        finally { main.Close(); }

        UiFixture.SetTextScale(1.5);
        var diff = GngInvestigationFixture.CompactDiff();
        try
        {
            GngInvestigationFixture.JumpToChange(diff);
            Save(diff, System.IO.Path.Combine(output, "dark-investigate-diff-next-change-small-150.png"));
        }
        finally { diff.Close(); UiFixture.SetTextScale(1); }
    }
}
