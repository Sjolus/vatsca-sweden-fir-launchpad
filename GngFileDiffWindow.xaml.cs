using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Globalization;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class GngFileDiffWindow : Window
{
    private readonly List<Paragraph> _changeGroups = [];
    private int _currentChange = -1;
    public GngFileDiffWindow(GngFilePreview preview, GngTextDiffResult? diff)
    {
        ArgumentNullException.ThrowIfNull(preview);
        InitializeComponent();
        FilePathText.Text = preview.RelativePath;
        ActionText.Text = preview.Action;
        SummaryText.Text = preview.Summary;
        MetadataDetails.IsExpanded = !preview.HasText;
        PreviewTabs.Visibility = preview.HasText ? Visibility.Visible : Visibility.Collapsed;
        if (preview.HasText)
        {
            CurrentText.Text = preview.BeforeText;
            AfterText.Text = preview.AfterText;
            ChangesTab.Visibility = diff is null ? Visibility.Collapsed : Visibility.Visible;
            if (diff is null) BeforeAfterTab.IsSelected = true;
            else
            {
                SetDiff(diff.Text);
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

    private void SetDiff(string sanitizedDiff)
    {
        var document = new FlowDocument { PagePadding = new Thickness(0) };
        var lines = sanitizedDiff.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0) lines = lines[..^1];
        double textWidth = 0;
        foreach (var line in lines)
        {
            // Keep logical diff lines intact without forcing a horizontal scrollbar for short files.
            var measured = new FormattedText(line[..Math.Min(line.Length, 4000)], CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(DiffText.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                DiffText.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            textWidth = Math.Max(textWidth, measured.WidthIncludingTrailingWhitespace);
            var paragraph = new Paragraph(new Run(line)) { Margin = new Thickness(0), Padding = new Thickness(2, 0, 2, 0) };
            if (line.StartsWith('+'))
            {
                paragraph.SetResourceReference(TextElement.ForegroundProperty, "DiffAddedFg");
                paragraph.SetResourceReference(TextElement.BackgroundProperty, "DiffAddedBg");
            }
            else if (line.StartsWith('-'))
            {
                paragraph.SetResourceReference(TextElement.ForegroundProperty, "DiffRemovedFg");
                paragraph.SetResourceReference(TextElement.BackgroundProperty, "DiffRemovedBg");
            }
            else if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                paragraph.SetResourceReference(TextElement.ForegroundProperty, "ApplicationUpdateText");
                paragraph.FontWeight = FontWeights.SemiBold;
                _changeGroups.Add(paragraph);
            }
            document.Blocks.Add(paragraph);
        }
        // Extremely long lines wrap at this bound; their full sanitized text remains selectable.
        document.MinPageWidth = Math.Min(textWidth + 24, 16_000);
        DiffText.Document = document;
        NextChangeButton.IsEnabled = _changeGroups.Count != 0;
        ChangePositionText.Text = _changeGroups.Count == 0 ? "" : $"{_changeGroups.Count} change {(_changeGroups.Count == 1 ? "group" : "groups")}";
    }

    private void NextChange_Click(object sender, RoutedEventArgs e)
    {
        if (_changeGroups.Count == 0) return;
        _currentChange = (_currentChange + 1) % _changeGroups.Count;
        var paragraph = _changeGroups[_currentChange];
        DiffText.Selection.Select(paragraph.ContentStart, paragraph.ContentStart);
        paragraph.BringIntoView();
        ChangePositionText.Text = $"Change {_currentChange + 1} of {_changeGroups.Count}";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
