// Exact production XAML and presentation-only details selection; no production services.
using System.Windows;
namespace VatscaUpdateChecker;
public partial class App : Application { }
public partial class MainWindow : Window
{
    public MainWindow() { InitializeComponent(); UiFixture.Initialize(this); InitializeApplicationDetails(); }
    internal void SelectSyntheticDetails(Models.CheckResult row) => SelectApplicationDetails(row);
    internal void RefreshSyntheticDetailsLayout() => UpdateApplicationDetailsLayout();
    internal void ResetSyntheticDetails() { _expandedDetailsOpen = false; InitializeApplicationDetails(); }
    internal void SetSyntheticRestart(bool required)
    {
        UiFixture.RestartRequired = required;
        RestartNotice.Visibility = required ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Window_Loaded(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Window_Loaded");
    private void Header_MouseLeftButtonDown(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Header_MouseLeftButtonDown");
    private void ThemeToggle_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "ThemeToggle_Click");
    private void DensityToggle_Click(object sender, RoutedEventArgs e)
    {
        UiFixture.Handle(this, sender, e, "DensityToggle_Click");
        UpdateApplicationDetailsLayout();
    }
    private void SetupWizard_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "SetupWizard_Click");
    private void AppConfig_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "AppConfig_Click");
    private void Settings_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Settings_Click");
    private void Minimize_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Minimize_Click");
    private void Maximize_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Maximize_Click");
    private void Close_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Close_Click");
    private void Check_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Check_Click");
    private void Maintenance_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Maintenance_Click");
    private void DiscoverInstallations_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "DiscoverInstallations_Click");
    private void Launch_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Launch_Click");
    private void ProfileDropdown_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "ProfileDropdown_Click");
    private void Fonts_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Fonts_Click");
    private void EuroScopeManage_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "EuroScopeManage_Click");
    private void Download_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Download_Click");
    private void SoftwareUpdate_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "SoftwareUpdate_Click");
    private void SoftwareCancel_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "SoftwareCancel_Click");
    private void SoftwareDownloads_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "SoftwareDownloads_Click");
    private void SoftwareInstall_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "SoftwareInstall_Click");
    private void SelfUpdate_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "SelfUpdate_Click");
}
public partial class SettingsWindow : Window
{
    public SettingsWindow() { InitializeComponent(); UiFixture.Initialize(this); }
    private void Path_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => UiFixture.PathChanged(this, sender);
    private void Save_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Save_Click");
    private void Cancel_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Cancel_Click");
    private void FindInstallations_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "FindInstallations_Click");
    private void InsertDefault_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "InsertDefault_Click");
    private void Clear_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Clear_Click");
    private void BrowseExe_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "BrowseExe_Click");
    private void BrowseFolder_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "BrowseFolder_Click");
}
public partial class AppConfigWindow : Window
{
    public AppConfigWindow() { InitializeComponent(); UiFixture.Initialize(this); }
    private void Preview_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Preview_Click");
    private void Save_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Save_Click");
    private void Cancel_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Cancel_Click");
    private void TogglePassword_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "TogglePassword_Click");
    private void ToggleHoppie_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "ToggleHoppie_Click");
}

public partial class MaintenanceWindow : Window
{
    public MaintenanceWindow() { InitializeComponent(); UiFixture.Initialize(this); }
    private void Apply_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Apply_Click");
    private void ChooseBackup_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "ChooseBackup_Click");
    private void Clear_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Clear_Click");
    private void Close_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Close_Click");
    private void ConfirmationChanged(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "ConfirmationChanged");
    private void Review_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Review_Click");
    private void SelectApps_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "SelectApps_Click");
    private void SelectData_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "SelectData_Click");
    private void SelectionChanged(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "SelectionChanged");
    private void Refresh_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Refresh_Click");
}

public partial class InstallationDiscoveryWindow : Window
{
    public InstallationDiscoveryWindow() { InitializeComponent(); UiFixture.Initialize(this); }
    private void Cancel_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Cancel_Click");
    private void Use_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Use_Click");
}

public partial class EuroScopeInstallWindow : Window
{
    public EuroScopeInstallWindow() { InitializeComponent(); UiFixture.Initialize(this); }
    private void Adopt_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Adopt_Click");
    private void Apply_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Apply_Click");
    private void CancelOperation_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "CancelOperation_Click");
    private void ChooseBackup_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "ChooseBackup_Click");
    private void Close_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Close_Click");
    private void ConfirmationChanged(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "ConfirmationChanged");
    private void Prerequisite_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Prerequisite_Click");
    private void ReleaseNotes_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "ReleaseNotes_Click");
    private void Removal_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Removal_Click");
    private void Review_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Review_Click");
}

public partial class FreshSoftwareInstallWindow : Window
{
    public FreshSoftwareInstallWindow() { InitializeComponent(); UiFixture.Initialize(this); }
    private void Adopt_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Adopt_Click");
    private void Apply_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Apply_Click");
    private void BetaChanged(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "BetaChanged");
    private void CancelOperation_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "CancelOperation_Click");
    private void ChooseBackup_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "ChooseBackup_Click");
    private void Close_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Close_Click");
    private void ConfirmationChanged(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "ConfirmationChanged");
    private void Prerequisite_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Prerequisite_Click");
    private void Removal_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Removal_Click");
    private void Review_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Review_Click");
}
