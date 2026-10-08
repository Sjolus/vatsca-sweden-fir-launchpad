using System.Windows;
using System.Windows.Automation;

namespace VatscaUpdateChecker;

public partial class GngUpdateWindow
{
    private string? _euroScopeBlockingReason;

    private void SetEuroScopeBlockingReason(string? reason)
    {
        _euroScopeBlockingReason = reason;
        EuroScopeNotice.Text = reason ?? string.Empty;
        EuroScopeNotice.Visibility = reason is null ? Visibility.Collapsed : Visibility.Visible;
        // Closing EuroScope must never automatically approve a previously blocked review.
        if (reason is not null) ConfirmInstall.IsChecked = false;
    }

    private void UpdateEuroScopeControls(bool canInstall, bool canRestore)
    {
        bool closed = _euroScopeBlockingReason is null;
        ConfirmInstall.IsEnabled = canInstall && closed;
        InstallButton.IsEnabled = canInstall && closed && ConfirmInstall.IsChecked == true;
        RestoreButton.IsEnabled = canRestore && closed;
        AutomationProperties.SetHelpText(InstallButton, _euroScopeBlockingReason ?? "Install after reviewing and confirming the proposed changes.");
        AutomationProperties.SetHelpText(RestoreButton, _euroScopeBlockingReason ?? "Review and restore a GNG installation backup.");
    }
}
