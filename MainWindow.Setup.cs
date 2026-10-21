using System.Windows;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    private void SetupWizard_Click(object sender, RoutedEventArgs e) => ShowWorkspace("Setup");

    private void ApplyDensity() => Tag = _settings.CompactLayout ? "Compact" : "Comfortable";
}