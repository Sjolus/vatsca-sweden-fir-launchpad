using System.Windows;
using System.Windows.Documents;

namespace VatscaUpdateChecker;

public partial class ControllerProfileControl
{
    public void ShowResultLines(IReadOnlyList<(string Text, bool NeedsAttention)> lines)
    {
        ResultText.Inlines.Clear();
        for (int index = 0; index < lines.Count; index++)
        {
            if (index > 0) ResultText.Inlines.Add(new LineBreak());
            var line = lines[index];
            var run = new Run((index == 0 ? "" : line.NeedsAttention ? "! " : "• ") + line.Text);
            run.SetResourceReference(TextElement.ForegroundProperty, line.NeedsAttention ? "WarningFg" : "SecondaryText");
            ResultText.Inlines.Add(run);
        }
        ResultScroll.ScrollToTop();
    }

    // Layout only: narrow windows and larger text stack fields without changing their values.
    private void IdentityFields_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        int columns = IdentityFields.ActualWidth >= 560 * Math.Max(1, FontSize / 12) ? 2 : 1;
        if (IdentityFields.Columns != columns) IdentityFields.Columns = columns;
    }
}
