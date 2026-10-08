using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

internal static class LayoutSnapshots
{
    // Render only the already measured synthetic content; never create or show an HWND.
    internal static void Capture(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        foreach (var dark in new[] { false, true })
        foreach (var width in new[] { 760d, 880d })
        foreach (var compact in new[] { false, true })
        {
            if (UiFixture.Dark != dark) UiFixture.ToggleTheme();
            UiFixture.Blank = UiFixture.Minimum = false;
            UiFixture.Scale = 1;
            var window = new MainWindow { Width = width, Height = 550, Tag = compact ? "Compact" : "Comfortable" };
            try
            {
                window.RefreshSyntheticDetailsLayout();
                LayoutValidation.CheckWindowContent(window);
                Save(window, Path.Combine(outputDirectory, $"{(dark ? "dark" : "light")}-{(compact ? "compact" : "expanded")}-{width}.png"));
                if (compact && width == 880)
                {
                    var list = (ItemsControl)window.FindName("AppList");
                    var row = list.Items.Cast<CheckResult>().Single(item => item.SoftwareApp == SoftwareApp.TrackAudio);
                    row.SoftwareUpdate = new(SoftwareApp.TrackAudio, SoftwareUpdatePhase.Unavailable,
                        "Synthetic example: the executable version and Windows registration disagree. Repair using the official installer before updating through Launchpad.");
                    row.StatusMessage = row.SoftwareUpdate.Message;
                    window.SelectSyntheticDetails(row);
                    LayoutValidation.CheckWindowContent(window);
                    Save(window, Path.Combine(outputDirectory, $"{(dark ? "dark" : "light")}-compact-review-880.png"));
                }
                if (compact)
                {
                    var list = (ItemsControl)window.FindName("AppList");
                    window.SelectSyntheticDetails(list.Items.Cast<CheckResult>().Single(item => item.HasFontsCheck));
                    LayoutValidation.CheckWindowContent(window);
                    Save(window, Path.Combine(outputDirectory, $"{(dark ? "dark" : "light")}-compact-gng-{width}.png"));
                }
            }
            finally { window.Close(); }
        }

        foreach (var dark in new[] { false, true })
        foreach (var minimum in new[] { false, true })
        {
            if (UiFixture.Dark != dark) UiFixture.ToggleTheme();
            UiFixture.Blank = false;
            UiFixture.Minimum = minimum;
            UiFixture.Scale = 1;
            var window = new GngCleanupWindow();
            try
            {
                LayoutValidation.CheckWindowContent(window);
                Save(window, Path.Combine(outputDirectory,
                    $"{(dark ? "dark" : "light")}-gng-cleanup-{(minimum ? "minimum" : "default")}.png"));
            }
            finally { window.Close(); }
        }

        foreach (var dark in new[] { false, true })
        foreach (var minimum in new[] { false, true })
        foreach (var state in Enum.GetValues<GngUpdateFixtureState>())
        {
            if (UiFixture.Dark != dark) UiFixture.ToggleTheme();
            UiFixture.Blank = false;
            UiFixture.Minimum = minimum;
            UiFixture.Scale = 1;
            var window = new GngUpdateWindow();
            try
            {
                window.SetSyntheticState(state);
                LayoutValidation.CheckWindowContent(window);
                Save(window, Path.Combine(outputDirectory,
                    $"{(dark ? "dark" : "light")}-gng-update-{state.ToString().ToLowerInvariant()}-{(minimum ? "minimum" : "default")}.png"));
                if (state == GngUpdateFixtureState.Review)
                {
                    ((CheckBox)window.FindName("ShowUnchangedFiles")).IsChecked = true;
                    Save(window, Path.Combine(outputDirectory,
                        $"{(dark ? "dark" : "light")}-gng-update-reviewall-{(minimum ? "minimum" : "default")}.png"));
                    window.SetSyntheticReviewRows(GngUpdateWindow.UnchangedRows());
                    Save(window, Path.Combine(outputDirectory,
                        $"{(dark ? "dark" : "light")}-gng-update-reviewzero-{(minimum ? "minimum" : "default")}.png"));
                    window.SetSyntheticReviewRows(GngUpdateWindow.ExampleRows());
                    window.SetSyntheticReviewProgress();
                    Save(window, Path.Combine(outputDirectory,
                        $"{(dark ? "dark" : "light")}-gng-update-reviewbusy-{(minimum ? "minimum" : "default")}.png"));
                    window.SetSyntheticState(GngUpdateFixtureState.Review);
                    var files = (DataGrid)window.FindName("FilesGrid");
                    files.ScrollIntoView(files.Items.Cast<GngUpdateFile>().Single(file => file.Action == "Merge list layout"));
                    Save(window, Path.Combine(outputDirectory,
                        $"{(dark ? "dark" : "light")}-gng-update-review-listlayout-{(minimum ? "minimum" : "default")}.png"));
                    ((CheckBox)window.FindName("PreserveListLayout")).IsChecked = false;
                    Save(window, Path.Combine(outputDirectory,
                        $"{(dark ? "dark" : "light")}-gng-update-review-listlayout-off-{(minimum ? "minimum" : "default")}.png"));
                }
            }
            finally { window.Close(); }
        }

        foreach (var dark in new[] { false, true })
        foreach (var minimum in new[] { false, true })
        foreach (var state in Enum.GetValues<GngFileDiffFixtureState>())
        {
            if (UiFixture.Dark != dark) UiFixture.ToggleTheme();
            var window = GngFileDiffFixture.Create(state, minimum);
            try
            {
                Save(window, Path.Combine(outputDirectory,
                    $"{(dark ? "dark" : "light")}-gng-file-preview-{state.ToString().ToLowerInvariant()}-{(minimum ? "minimum" : "default")}.png"));
                if (GngFileDiffFixture.IsListLayout(state))
                {
                    ((TabItem)window.FindName("BeforeAfterTab")).IsSelected = true;
                    Save(window, Path.Combine(outputDirectory,
                        $"{(dark ? "dark" : "light")}-gng-file-preview-{state.ToString().ToLowerInvariant()}-beforeafter-{(minimum ? "minimum" : "default")}.png"));
                }
            }
            finally { window.Close(); }
        }
    }

    private static void Save(Window window, string path)
    {
        // DataGrid star columns finish sizing through a deferred dispatcher callback.
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        LayoutValidation.CheckWindowContent(window);
        var content = (FrameworkElement)window.Content;
        var width = (int)Math.Ceiling(content.ActualWidth + content.Margin.Left + content.Margin.Right);
        var height = (int)Math.Ceiling(content.ActualHeight + content.Margin.Top + content.Margin.Bottom);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        bitmap.Render(background);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }
}
