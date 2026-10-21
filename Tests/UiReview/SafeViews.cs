// Exact production XAML and inert action handlers; no production services.
using System.Windows;
namespace VatscaUpdateChecker;
public partial class App : Application { }
public partial class MainWindow : Window
{
    private void Help_Click(object sender, RoutedEventArgs e) { }
    private void ReportProblem_Click(object sender, RoutedEventArgs e) { }
    private void Activity_Click(object sender, RoutedEventArgs e) => ShowSyntheticWorkspace("Activity");
    private void RunningActions_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "RunningActions_Click");
    private void ShowRunning_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "ShowRunning_Click");
    private void CloseApplication_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "CloseApplication_Click");
    private void StartAll_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "StartAll_Click");
    private void CloseAll_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "CloseAll_Click");
    private void Usage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Models.CheckResult row }) row.IsNotUsed = !row.IsNotUsed;
    }
    public MainWindow() { InitializeComponent(); UiFixture.Initialize(this); InitializeSyntheticWorkspaces(); }
    internal IReadOnlyList<Models.CheckResult> SyntheticRows { get; private set; } = [];
    internal void SetSyntheticApplicationRows(IEnumerable<Models.CheckResult> rows)
    {
        SyntheticRows = rows.ToArray();
        PresentApplicationRows(SyntheticRows);
    }
    internal void RefreshSyntheticApplicationRows() => PresentApplicationRows(SyntheticRows);
    internal void FocusSyntheticRow(Models.CheckResult row)
    {
        if (AppList.ItemContainerGenerator.ContainerFromItem(row) is FrameworkElement container) container.BringIntoView();
    }
    private void RowAction_Click(object sender, RoutedEventArgs e) { }
    internal Models.CheckResult? RequestedStatusCopy { get; private set; }
    private void CopyApplicationStatus_CanExecute(object sender, System.Windows.Input.CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = sender is FrameworkElement { DataContext: Models.CheckResult };
        e.Handled = true;
    }
    private void CopyApplicationStatus_Executed(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
    {
        RequestedStatusCopy = (sender as FrameworkElement)?.DataContext as Models.CheckResult;
        e.Handled = true;
    }
    internal void SetSyntheticRestart(bool required)
    {
        UiFixture.RestartRequired = required;
        RestartNotice.Visibility = required ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Window_Loaded(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Window_Loaded");
    private void Header_MouseLeftButtonDown(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Header_MouseLeftButtonDown");
    private void ThemeToggle_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "ThemeToggle_Click");
    private void SetupWizard_Click(object sender, RoutedEventArgs e) => ShowSyntheticWorkspace("Setup");
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
    private void GngCleanup_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "GngCleanup_Click");
    private void GngUpdate_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "GngUpdate_Click");
    private void SaamanManage_Click(object sender, RoutedEventArgs e) { }
    private void SaamanGuide_Click(object sender, RoutedEventArgs e) { }
    private void EuroScopeManage_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "EuroScopeManage_Click");
    private void Download_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Download_Click");
    private void SoftwareUpdate_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "SoftwareUpdate_Click");
    private void SoftwareCancel_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "SoftwareCancel_Click");
    private void SoftwareDownloads_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "SoftwareDownloads_Click");
    private void SoftwareInstall_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "SoftwareInstall_Click");
    private void SoftwareLocation_Click(object sender, RoutedEventArgs e) => ShowSyntheticWorkspace("Setup");
    private void SelfUpdate_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "SelfUpdate_Click");
}
public partial class SettingsWindow : Window
{
    private void PrivacySupport_Click(object sender, RoutedEventArgs e) { }
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
    private void Cancel_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Cancel_Click");
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
    private void Choose_Click(object sender, RoutedEventArgs e) => UiFixture.Handle(this, sender, e, "Choose_Click");
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
    public bool IsVatEfsFixture { get; }
    public FreshSoftwareInstallWindow(bool vatEfs = false) { IsVatEfsFixture = vatEfs; InitializeComponent(); UiFixture.Initialize(this); }
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
