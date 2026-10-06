using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
            }
            finally { window.Close(); }
        }
    }

    private static void Save(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }
}
