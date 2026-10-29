using System.Windows;

namespace VatscaUpdateChecker;

public partial class GngUpdateControl
{
    // WebView2 needs a presented host while its native controller is being created.
    // Ordinary background preparation and downloads can still return to the app list.
    public bool NeedsAttentionView => _startingBrowser || NestedContent?.Content is not null || _readingFilePreview || _showingIssue || _preparationCancelled || _recoveryRequired || _showingCompletion ||
        (!_busy && !_queuedPreview && _download is null && (ReviewPanel?.Visibility == Visibility.Visible || BrowserToolbar?.Visibility == Visibility.Visible || StartPanel?.Visibility == Visibility.Visible));

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
