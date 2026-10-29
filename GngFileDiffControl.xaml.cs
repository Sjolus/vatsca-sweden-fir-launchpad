using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Globalization;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class GngFileDiffControl : UserControl
{
    public event EventHandler? CloseRequested;
    public event Action<int>? FileNavigationRequested;
    private int _fileIndex;
    private int _fileCount;
    private bool _navigationBusy;

    private readonly List<Paragraph> _changeGroups = [];
    private int _currentChange = -1;
    public GngFileDiffControl(GngFilePreview preview, GngTextDiffResult? diff)
    {
        ArgumentNullException.ThrowIfNull(preview);
        InitializeComponent();
        FilePathText.Text = preview.RelativePath;
        FilePathText.ToolTip = preview.RelativePath;
        ActionText.Text = preview.Action;
        ActionText.ToolTip = preview.Action;
        SummaryText.Text = preview.Summary;
        ChangesTab.Visibility = BeforeAfterTab.Visibility = preview.HasText ? Visibility.Visible : Visibility.Collapsed;
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
                DiffSummaryText.ToolTip = diff.Summary;
            }
        }
        else
        {
            InfoTab.IsSelected = true;
        }
    }

    public void SetFileNavigation(int index, int total)
    {
        _fileIndex = index;
        _fileCount = total;
        FilePositionText.Text = total > 0 ? $"File {index + 1} of {total}" : string.Empty;
        FileNavigation.Visibility = total > 1 ? Visibility.Visible : Visibility.Collapsed;
        UpdateFileNavigation();
    }

    public void SetNavigationBusy(bool busy)
    {
        _navigationBusy = busy;
        NavigationStatus.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        PreviewTabs.IsEnabled = !busy;
        UpdateFileNavigation();
    }

    private void UpdateFileNavigation()
    {
        PreviousFileButton.IsEnabled = !_navigationBusy && _fileIndex > 0;
        NextFileButton.IsEnabled = !_navigationBusy && _fileIndex + 1 < _fileCount;
    }

    private void PreviousFile_Click(object sender, RoutedEventArgs e) { if (PreviousFileButton.IsEnabled) FileNavigationRequested?.Invoke(-1); }
    private void NextFile_Click(object sender, RoutedEventArgs e) { if (NextFileButton.IsEnabled) FileNavigationRequested?.Invoke(1); }

    private void SetDiff(string sanitizedDiff)
    {
        var document = new FlowDocument { PagePadding = new Thickness(0) };
        var lines = sanitizedDiff.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0) lines = lines[..^1];
        double textWidth = 0;
        bool needsChangeAnchor = false;
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
                needsChangeAnchor = true;
            }
            // Jump to the changed lines, so their context cannot fill a short viewport.
            if (needsChangeAnchor && (line.StartsWith('+') || line.StartsWith('-')))
            {
                _changeGroups.Add(paragraph);
                needsChangeAnchor = false;
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

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
