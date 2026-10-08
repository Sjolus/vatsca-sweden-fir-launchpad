using System.Windows;
using System.Windows.Controls;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class GngFileDiffWindow : Window
{
    public GngFileDiffWindow(GngFilePreview preview, GngTextDiffResult? diff)
    {
        ArgumentNullException.ThrowIfNull(preview);
        InitializeComponent();
        FilePathText.Text = preview.RelativePath;
        ActionText.Text = preview.Action;
        SummaryText.Text = preview.Summary;
        PreviewTabs.Visibility = preview.HasText ? Visibility.Visible : Visibility.Collapsed;
        if (preview.HasText)
        {
            CurrentText.Text = preview.BeforeText;
            AfterText.Text = preview.AfterText;
            ChangesTab.Visibility = diff is null ? Visibility.Collapsed : Visibility.Visible;
            if (diff is null) BeforeAfterTab.IsSelected = true;
            else
            {
                DiffText.Text = diff.Text;
                DiffSummaryText.Text = diff.Summary;
            }
        }
        else
        {
            // Summary-only previews do not need the space reserved for two text panes.
            Width = 780;
            Height = 360;
            MinHeight = 300;
            HeaderRow.Height = new GridLength(1, GridUnitType.Star);
            BodyRow.Height = GridLength.Auto;
            SummaryText.MaxHeight = double.PositiveInfinity;
            SummaryText.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
