using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

internal static partial class LayoutMatrix
{
    private static readonly (string Name, GngOperationState Operation)[] GngDownloadCases =
    [
        ("update", new("42% · 21.0 of 50.0 MB · 1.3 MB/s · About 22 seconds left for this file. You will review the update before installing.",
            "Download 1/2", Busy: true, CanCancel: true, Percent: 42, TransferSummary: "1.3 MB/s · 21/50 MB")),
        ("reference", new("42% · 84.0 of 200.0 MB · 3.2 MB/s · About 36 seconds left for this file. Reference copy for future cleanup comparisons. It will not be installed.",
            "Reference 2/2", Busy: true, CanCancel: true, Percent: 42, TransferSummary: "3.2 MB/s · 84/200 MB")),
        ("unknown-size", new("21.0 MB downloaded · 1.3 MB/s. The server has not supplied the file size.",
            "Download 1/2", Busy: true, CanCancel: true, TransferSummary: "1.3 MB/s · 21 MB")),
        ("waiting", new("42% · 21.0 of 50.0 MB · 0 B/s · Waiting for data. You can stop this download.",
            "Download 1/2", Busy: true, CanCancel: true, Percent: 42, TransferSummary: "0 B/s · 21/50 MB"))
    ];

    private static int CheckGngDownloadLayouts()
    {
        int count = 0;
        try
        {
            foreach (double scale in new[] { 1d, 1.5d })
            foreach (double width in new[] { 760d, 1000d })
            foreach (string density in new[] { "Compact", "Comfortable" })
            foreach (var scenario in GngDownloadCases)
            {
                UiFixture.Blank = UiFixture.Minimum = false;
                UiFixture.Scale = 1;
                UiFixture.SetTextScale(scale);
                var window = new MainWindow { Width = width, Height = 590, Tag = density };
                try
                {
                    var list = Required<ItemsControl>(window, "AppList");
                    var row = list.Items.Cast<CheckResult>().Single(item => item.HasFontsCheck);
                    row.GngOperation = scenario.Operation;
                    LayoutValidation.CheckWindowContent(window);
                    DrainBindings();
                    LayoutValidation.CheckWindowContent(window);
                    CheckRowReadability(window);
                    CheckRowDiagnostics(window, row);
                    var stats = LayoutValidation.Descendants(list).OfType<TextBlock>().Single(text =>
                        ReferenceEquals(text.DataContext, row) && BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path?.Path == "RowExplanation");
                    var bar = LayoutValidation.Descendants(list).OfType<ProgressBar>().Single(progress =>
                        ReferenceEquals(progress.DataContext, row) && BindingOperations.GetBinding(progress, UIElement.VisibilityProperty)?.Path?.Path == "RowProgressVisible");
                    Require(IsPresented(stats) && stats.Text == scenario.Operation.TransferSummary,
                        $"GNG transfer metrics must remain visible in {density}: {scenario.Name}.");
                    Require(Equals(stats.ToolTip, row.DetailText), "Truncated transfer metrics must retain full details in the tooltip.");
                    Require(bar.IsIndeterminate == (scenario.Operation.Percent is null), "Unknown transfer size must remain indeterminate.");
                    var parent = (FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(row);
                    var statsBounds = Bounds(stats, parent);
                    var barBounds = Bounds(bar, parent);
                    var statusBounds = Bounds(RowStatus(window, row), parent);
                    Require(statsBounds.Top >= statusBounds.Bottom - .5 && statsBounds.Bottom <= barBounds.Top + .5,
                        $"GNG metrics overlap status/progress at {width}px {scale:P0} {density}: status={statusBounds}; metrics={statsBounds}; bar={barBounds}.");
                    if (scenario.Operation.Percent is not null)
                        Require(RowStatus(window, row).Text.Contains("42%", StringComparison.Ordinal), "Known download percentage must be in the selectable status.");
                    else Require(!RowStatus(window, row).Text.Contains('%'), "Unknown sizes must not invent a percentage.");
                    row.GngOperation = new("Checking the package.", "Checking package", Busy: true);
                    DrainBindings();
                    Require(!row.HasGngTransfer && !stats.Text.Contains("MB/s", StringComparison.Ordinal), "Finished transfer must not leave a stale speed.");
                    Require(IsPresented(stats) == (density == "Comfortable"), "Compact metrics must disappear when the transfer finishes.");
                    Require(!window.IsVisible, "GNG transfer checks must not open native windows.");
                    count++;
                }
                finally { window.Close(); }
            }
        }
        finally { UiFixture.SetTextScale(1); UiFixture.Minimum = false; }
        return count;
    }

    internal static IReadOnlyList<(string Name, GngOperationState Operation)> GngDownloadSnapshotCases => GngDownloadCases;
}

internal static partial class LayoutSnapshots
{
    private static void CaptureGngDownloads(string output)
    {
        try
        {
            foreach (var dark in new[] { false, true })
            foreach (var density in new[] { "Compact", "Comfortable" })
            foreach (var scale in new[] { 1d, 1.5d })
            foreach (var width in new[] { 760d, 1000d })
            foreach (var scenario in LayoutMatrix.GngDownloadSnapshotCases)
            {
                if (UiFixture.Dark != dark) UiFixture.ToggleTheme();
                UiFixture.SetTextScale(scale);
                UiFixture.Blank = UiFixture.Minimum = false;
                UiFixture.Scale = 1;
                var window = new MainWindow { Width = width, Height = 590, Tag = density };
                try
                {
                    var row = ((ItemsControl)window.FindName("AppList")).Items.Cast<CheckResult>().Single(item => item.HasFontsCheck);
                    row.GngOperation = scenario.Operation;
                    ((TextBlock)window.FindName("LastCheckedText")).Text = scenario.Operation.Message;
                    Save(window, Path.Combine(output, $"{(dark ? "dark" : "light")}-gng-download-{scenario.Name}-{density.ToLowerInvariant()}-{width:0}-{scale * 100:0}.png"));
                }
                finally { window.Close(); }
            }
        }
        finally { UiFixture.SetTextScale(1); UiFixture.Minimum = false; }
    }
}
