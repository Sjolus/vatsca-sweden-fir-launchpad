using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace VatscaUpdateChecker;

// Only production XAML is linked. These handlers never compare packages or move files.
public partial class GngCleanupWindow : Window
{
    public GngCleanupWindow() { InitializeComponent(); UiFixture.Initialize(this); }

    internal void RefreshSyntheticScenario()
    {
        RootBox.Text = UiFixture.Blank ? "" : @"C:\Synthetic\EuroScope data";
        BackupRootBox.Text = @"C:\Synthetic\AppData\Local\VatscaUpdateChecker\CleanupBackups";
        OldZipBox.Text = UiFixture.Blank ? "" : @"C:\Synthetic\Downloads\ESAA-Full-Package_20260903120000-260901-0001.zip";
        NewZipBox.Text = UiFixture.Blank ? "" : @"C:\Synthetic\Downloads\ESAA-Full-Package_20261001120000-261001-0003.zip";
        CandidatesGrid.ItemsSource = UiFixture.Blank ? Array.Empty<CleanupFixtureRow>() : ExampleRows();
        SummaryText.Text = UiFixture.Blank ? "Nothing selected. Choose both complete ZIPs, then compare." : "3 old package files found · 1 can move · 2 must stay · 0 selected";
        StatusText.Text = "Synthetic preview only. Modified and referenced files are kept. Moving files to a backup does not free disk space.";
        ArchiveButton.IsEnabled = ClearButton.IsEnabled = false;
        SelectButton.IsEnabled = !UiFixture.Blank;
    }

    internal static CleanupFixtureRow[] ExampleRows() =>
    [
        new("ESAA-Sweden_20260903120000-260901-0001.sct", true, "Unchanged file from the old package, absent from the new complete package."),
        new("ESAA TOPSKY.prf", false, "Kept: this file is still referenced by an installed profile."),
        new("ESAA/Plugins/Retired plugin with a long folder name/RetiredPlugin.dll", false, "Kept: the installed plugin differs from the original package. Modified plugins need manual review.")
    ];

    private void Inputs_Changed(object sender, TextChangedEventArgs e) { }
    private void Downloads_Click(object sender, RoutedEventArgs e) => StatusText.Text = "Synthetic download-page action only. No browser opens.";
    private void BrowseZip_Click(object sender, RoutedEventArgs e) => StatusText.Text = "Synthetic chooser only. No file dialog opens.";
    private void Compare_Click(object sender, RoutedEventArgs e) { CandidatesGrid.ItemsSource = ExampleRows(); SelectButton.IsEnabled = true; }
    private void SelectEligible_Click(object sender, RoutedEventArgs e)
    {
        if (CandidatesGrid.ItemsSource is not CleanupFixtureRow[] rows) return;
        foreach (var row in rows) row.IsSelected = row.IsEligible;
        CandidatesGrid.Items.Refresh();
        ArchiveButton.IsEnabled = ClearButton.IsEnabled = rows.Any(row => row.IsSelected);
    }
    private void ClearSelection_Click(object sender, RoutedEventArgs e) => RefreshSyntheticScenario();
    private void Archive_Click(object sender, RoutedEventArgs e) => StatusText.Text = "Synthetic result: moved 1 file, skipped 1. Backup: C:\\Synthetic\\CleanupBackups\\dated-backup. Nothing was changed.";
    private void Restore_Click(object sender, RoutedEventArgs e) => StatusText.Text = "Synthetic restore: an existing destination file was kept. No files were changed.";
    private void Window_Closing(object? sender, CancelEventArgs e) { }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    public sealed class CleanupFixtureRow(string relativePath, bool isEligible, string details)
    {
        public string RelativePath => relativePath;
        public bool IsEligible => isEligible;
        public string Details => details;
        public string SizeText => "120.0 KB";
        public bool IsSelected { get; set; }
    }
}

internal static partial class LayoutMatrix
{
    private static void CheckCleanupLayout(GngCleanupWindow window)
    {
        var root = Required<TextBox>(window, "RootBox");
        Require(root.IsReadOnly && root.Focusable, "Cleanup must show a selectable, fixed configured destination.");
        var backup = Required<TextBox>(window, "BackupRootBox");
        Require(backup.IsReadOnly && backup.Focusable && !string.IsNullOrWhiteSpace(backup.Text),
            "Cleanup must show the selectable backup location before comparison or selection.");
        var downloads = Required<Hyperlink>(window, "DownloadsLink");
        Require(downloads.Focusable && !string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(downloads)),
            "The explicit package-download link must be keyboard accessible and identify its browser action.");
        var grid = Required<DataGrid>(window, "CandidatesGrid");
        Require(grid.ActualHeight >= 90 && grid.ActualWidth > 450,
            "The cleanup preview must leave room to read and scroll file rows at the minimum size.");
        Require(!grid.CanUserAddRows && !grid.CanUserDeleteRows, "The cleanup review must not permit creating/deleting grid entries.");
        Require(!Required<Button>(window, "ArchiveButton").IsEnabled, "Cleanup must begin without an enabled move action.");
        Require(grid.Items.OfType<GngCleanupWindow.CleanupFixtureRow>().All(row => !row.IsSelected),
            "The cleanup preview must start with every file unchecked.");
        var status = Required<TextBox>(window, "StatusText");
        Require(status.IsReadOnly && status.IsReadOnlyCaretVisible && status.Focusable &&
            status.VerticalScrollBarVisibility == ScrollBarVisibility.Auto, "Cleanup results and recovery paths must be keyboard-readable and scrollable.");

        status.Text = string.Join(Environment.NewLine, Enumerable.Repeat("Synthetic partial result. Kept modified files; backup remains in C:\\Synthetic\\Recovery\\Long backup directory name.", 12));
        Required<ProgressBar>(window, "Activity").Visibility = Visibility.Visible;
        LayoutValidation.CheckWindowContent(window);
        Require(grid.ActualHeight >= 90, "Long cleanup results must leave the preview available.");
        CheckFooter(window);
    }
}
