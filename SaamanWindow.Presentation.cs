using System.Windows;

namespace VatscaUpdateChecker;

public partial class SaamanWindow
{
    // Shared with the inert UI fixture. Process detection and all mutations stay with the owner.
    private void PresentActionAvailability(bool busy, bool restart, bool profileEnabled, bool hasRecovery, string? blocked)
    {
        bool available = !busy && !restart;
        ProcessBlockText.Text = restart ? "Restart Windows before changing SAAMAN." : blocked ?? "";
        ProcessBlockText.Visibility = string.IsNullOrEmpty(ProcessBlockText.Text) ? Visibility.Collapsed : Visibility.Visible;
        InstallButton.IsEnabled = available && blocked is null;
        ProfileEnabledCheck.IsEnabled = available && blocked is null;
        AdoptButton.IsEnabled = available && (!profileEnabled || blocked is null);
        ChooseProfileButton.IsEnabled = available && !profileEnabled;
        ChooseProfileButton.ToolTip = profileEnabled ? "Disable SAAMAN before choosing another profile." : null;
        ConfirmChange.IsEnabled = available && hasRecovery && blocked is null;
        RestoreButton.IsEnabled = ConfirmChange.IsEnabled && ConfirmChange.IsChecked == true;
    }
}
