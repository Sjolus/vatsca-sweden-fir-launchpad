using System.Windows;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    private void PresentApplicationRows(IEnumerable<CheckResult> rows)
    {
        AppList.ItemsSource = ApplicationRows.Visible(rows);
    }

    // Shared by the owner and inert layout fixtures; no settings, processes or storage access.
    private void PresentWorkspace(string page)
    {
        AppsWorkspace.Visibility = page == "Apps" ? Visibility.Visible : Visibility.Collapsed;
        SetupWorkspace.Visibility = page != "Apps" ? Visibility.Visible : Visibility.Collapsed;
        AppBulkActions.Visibility = page == "Apps" ? Visibility.Visible : Visibility.Collapsed;
        CheckButton.Visibility = page == "Apps" ? Visibility.Visible : Visibility.Collapsed;
        AppsTab.IsChecked = page == "Apps";
        SetupTab.IsChecked = page != "Apps";
        MaintenanceButton.Visibility = page != "Apps" && SetupWorkspace.SelectedSection == "Activity" ? Visibility.Visible : Visibility.Collapsed;
        ActivityNotice.Visibility = page == "Apps" && !string.IsNullOrWhiteSpace(ActivityNotice.Text) ? Visibility.Visible : Visibility.Collapsed;
    }
}
