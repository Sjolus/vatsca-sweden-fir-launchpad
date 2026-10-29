using System.Windows;
using System.Windows.Automation;

namespace VatscaUpdateChecker;

public partial class GngUpdateControl
{
    private string? _euroScopeBlockingReason;

    private void SetEuroScopeBlockingReason(string? reason)
    {
        _euroScopeBlockingReason = reason;
        EuroScopeNotice.Text = reason ?? string.Empty;
        EuroScopeNotice.Visibility = reason is null || ReviewPanel?.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateEuroScopeControls(bool canInstall, bool canRestore)
    {
        bool closed = _euroScopeBlockingReason is null;
        UpdateReviewReadiness(canInstall);
        InstallButton.IsEnabled = canInstall && closed;
        RestoreButton.IsEnabled = canRestore && closed;
        ConfirmRestoreButton.IsEnabled = canRestore && closed;
        AutomationProperties.SetHelpText(InstallButton, _euroScopeBlockingReason ?? "Install after reviewing and confirming the proposed changes.");
        AutomationProperties.SetHelpText(RestoreButton, _euroScopeBlockingReason ?? "Review and restore a GNG installation backup.");
    }
}
