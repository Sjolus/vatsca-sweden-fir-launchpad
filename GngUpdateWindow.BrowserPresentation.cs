using System.Windows;

namespace VatscaUpdateChecker;

public partial class GngUpdateWindow
{
    private void SetBrowserInteractionBlocked(bool blocked)
    {
        if (BrowserSurface is null || BrowserDownloadOverlay is null || BrowserToolbar is null) return;

        // Native WebView2 cannot be covered reliably by a WPF overlay. Hide its
        // surface without detaching the control, so the active download stays alive.
        BrowserSurface.IsEnabled = !blocked;
        BrowserSurface.IsHitTestVisible = !blocked;
        BrowserSurface.Visibility = blocked ? Visibility.Hidden : Visibility.Visible;
        BrowserDownloadOverlay.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
        BrowserToolbar.Opacity = blocked ? 0.55 : 1;
    }
}
