using System.Linq;
using System.Windows;
using System.Windows.Controls;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    private CheckResult? _selectedApplication;
    private bool _expandedDetailsOpen;

    // Presentation only: the inert UI fixture also compiles this selection logic.
    private void InitializeApplicationDetails()
    {
        _selectedApplication = AppList.Items.OfType<CheckResult>().FirstOrDefault();
        ApplicationDetails.DataContext = _selectedApplication;
        UpdateApplicationDetailsLayout();
    }

    private void SelectApplicationDetails(CheckResult row)
    {
        if (!AppList.Items.Contains(row)) return;
        _selectedApplication = row;
        _expandedDetailsOpen = true;
        ApplicationDetails.DataContext = row;
        DetailMessage.ScrollToHome();
        UpdateApplicationDetailsLayout();
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: CheckResult row } button) return;
        SelectApplicationDetails(row);
        // Put keyboard readers beside the explanation and its actions, not eight rows away.
        if (button.IsKeyboardFocused) DetailMessage.Focus();
    }

    private void DetailsClose_Click(object sender, RoutedEventArgs e)
    {
        _expandedDetailsOpen = false;
        UpdateApplicationDetailsLayout();
        // Theme/setup refreshes recreate row controls, so never retain an old button instance.
        if (_selectedApplication is { } row &&
            AppList.ItemContainerGenerator.ContainerFromItem(row) is ContentPresenter container)
        {
            container.ApplyTemplate();
            if (container.ContentTemplate?.FindName("RowDetailsButton", container) is Button current)
            {
                current.Focus();
                return;
            }
        }
        DensityToggleButton.Focus();
    }

    private void UpdateApplicationDetailsLayout()
    {
        bool compact = Equals(Tag, "Compact");
        bool visible = _selectedApplication != null && (compact || _expandedDetailsOpen);
        ApplicationDetails.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        DetailsCloseButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        foreach (var row in AppList.Items.OfType<CheckResult>())
            row.IsSelected = visible && ReferenceEquals(row, _selectedApplication);
    }
}
