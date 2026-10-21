using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VatscaUpdateChecker;

public partial class ControllerProfileControl
{
    private void ProfileInfo_Open(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ToolTip: ToolTip tip } target) return;
        tip.PlacementTarget = target;
        tip.IsOpen = true;
    }

    private void ProfileInfo_Close(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ToolTip: ToolTip tip }) tip.IsOpen = false;
    }

    private void ProfileInfo_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || sender is not FrameworkElement { ToolTip: ToolTip { IsOpen: true } tip }) return;
        tip.IsOpen = false;
        e.Handled = true;
    }
}
