using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

internal static partial class LayoutMatrix
{
    private static void CheckReportingHistory(ReportingHistoryWindow window)
    {
        Require(window.Tag is ReportingHistoryState, "Report history needs an independent synthetic expected state.");
        var expected = (ReportingHistoryState)window.Tag;
        var content = (FrameworkElement)window.Content;
        var choice = Required<ComboBox>(window, "StreamChoice");
        var panel = Required<StackPanel>(window, "DeliveryPanel");
        var summary = Required<TextBox>(window, "ReadableReportText");
        var json = Required<TextBox>(window, "JsonText");
        var details = Required<Expander>(window, "JsonDetails");
        var copy = Required<Button>(window, "CopyJsonButton");
        var scroll = Required<ScrollViewer>(window, "ContentScroll");
        var status = Required<TextBlock>(window, "DeliveryStatus");
        var pending = Required<TextBlock>(window, "PendingStatus");
        var empty = Required<TextBlock>(window, "EmptyStatus");

        Require(choice.Items.Count == 4 && choice.SelectedIndex is >= 0 and < 4,
            "Report history must provide all four independent report types.");
        Require(!details.IsExpanded, "Exact JSON should start collapsed.");
        Require(summary.IsReadOnly && json.IsReadOnly && summary.IsReadOnlyCaretVisible && json.IsReadOnlyCaretVisible &&
            summary.Focusable && json.Focusable && summary.IsTabStop && json.IsTabStop,
            "Both sent-data views must be read-only and keyboard selectable.");
        Require(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(summary)) &&
            !string.IsNullOrWhiteSpace(AutomationProperties.GetName(json)), "Sent-data views need accessible names.");
        Require(summary.TextWrapping == TextWrapping.Wrap && json.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto &&
            json.VerticalScrollBarVisibility == ScrollBarVisibility.Auto, "Long report content must wrap or scroll without being lost.");
        Require(scroll.ViewportHeight >= 60, "Report history has no usable content viewport at this text/window size.");
        CheckControlBounds(choice, content);
        CheckFooter(window);

        var stream = (ReportingStream)choice.SelectedIndex;
        Require(choice.SelectedItem?.ToString() == ReportingHistoryPresentation.StreamName(stream),
            "The selected report type must display a friendly label even when the theme uses ToString.");
        Require(details.Foreground is SolidColorBrush foreground &&
            window.FindResource("PrimaryText") is SolidColorBrush primary && foreground.Color == primary.Color,
            "The exact-data expander label must use readable primary text in both themes.");
        var delivery = expected.StorageAvailable ? expected.LastSent.FirstOrDefault(item => item.Stream == stream) : null;
        Require(IsPresented(panel) == (delivery is not null), "Sent details must match the selected stream's confirmed copy.");
        if (delivery is null)
        {
            Require(summary.Text.Length == 0 && json.Text.Length == 0 && !copy.IsEnabled && IsPresented(empty),
                "An absent/unavailable copy must clear stale details and disable copying.");
            if (!expected.StorageAvailable)
                Require(status.Text.Contains("unavailable", StringComparison.OrdinalIgnoreCase) && pending.Text.Length == 0,
                    "Unreadable storage must not present stale confirmation or pending counts.");
            else
                Require(status.Text.Contains("No sent copy recorded yet", StringComparison.Ordinal) &&
                    empty.Text.Contains("starts recording copies with this version", StringComparison.Ordinal),
                    "An empty upgraded installation must not claim it never sent reports.");
            return;
        }

        Require(!IsPresented(empty) && summary.Text.Length > 0 && copy.IsEnabled,
            "A confirmed copy needs a readable summary and an available exact-copy action.");
        Require(json.Text == ReportingHistoryPresentation.FormatJson(delivery), "The displayed JSON must belong to the selected saved delivery.");
        Require(status.Text.Contains(delivery.ConfirmedAt.ToLocalTime().ToString("g"), StringComparison.Ordinal) &&
            status.Text.Contains("local time", StringComparison.Ordinal), "Confirmation time must be shown as labelled local time.");
        if (stream == ReportingStream.ManualReport)
            Require(pending.Text.Contains("only when you review and choose Send report", StringComparison.Ordinal),
                "Manual reporting must remain separate from background-reporting choices.");
        else
        {
            int count = stream == ReportingStream.Usage ? expected.UsagePending : stream == ReportingStream.Inventory ? expected.InventoryPending : expected.ErrorPending;
            Require(pending.Text.Contains($"Awaiting confirmation: {count} ", StringComparison.Ordinal),
                "Pending counts must describe unconfirmed delivery, not promise that nothing was transmitted.");
        }

        try
        {
            details.IsExpanded = true;
            DrainBindings();
            LayoutValidation.CheckWindowContent(window);
            Require(Bounds(details, panel).Top < Bounds(summary, panel).Top,
                "Exact JSON access must precede long readable event summaries.");
            Require(IsPresented(json) && json.ActualHeight >= 180 && scroll.ViewportHeight >= 60,
                "Expanded JSON must remain readable while leaving the outer report scroll usable.");
            CheckFooter(window);
            copy.BringIntoView();
            DrainBindings();
            LayoutValidation.CheckWindowContent(window);
            CheckControlBounds(copy, content);
            CheckButtonCaption(copy);
            CheckFooter(window);
        }
        finally
        {
            details.IsExpanded = false;
            scroll.ScrollToTop();
            LayoutValidation.CheckWindowContent(window);
        }
    }
}
