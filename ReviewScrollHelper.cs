using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace VatscaUpdateChecker;

/// <summary>Shows a generated review after focus and nested TextBox scrolling have settled.</summary>
internal static class ReviewScrollHelper
{
    internal static void Show(ScrollViewer scroll, FrameworkElement reviewPanel, TextBox reviewText,
        Func<bool>? stillCurrent = null)
    {
        scroll.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (stillCurrent?.Invoke() == false) return;
            scroll.UpdateLayout();
            reviewText.CaretIndex = 0;
            reviewText.Focus();
            reviewText.ScrollToHome();

            // Focus can queue a RequestBringIntoView from the TextBox's own scroll viewer.
            // Apply the outer offset afterwards, in its content's coordinate space.
            scroll.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (stillCurrent?.Invoke() == false || scroll.Content is not UIElement content) return;
                scroll.UpdateLayout();
                var top = reviewPanel.TranslatePoint(new Point(), content).Y;
                if (double.IsFinite(top))
                    scroll.ScrollToVerticalOffset(Math.Clamp(top, 0, scroll.ScrollableHeight));
            }));
        }));
    }
}
