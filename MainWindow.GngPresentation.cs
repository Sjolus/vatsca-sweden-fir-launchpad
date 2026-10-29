using System.Windows;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    private void UpdateGngPresentation()
    {
        bool samePage = _workspace == _gngFlowWorkspace &&
            (_workspace != "Setup" || SetupWorkspace.SelectedSection == _gngFlowSection);
        bool show = samePage && (_gngUpdateFlow?.NeedsAttentionView == true || _gngCleanupFlow is not null);
        FrameworkElement? content = (FrameworkElement?)_gngUpdateFlow ?? _gngCleanupFlow;
        if (_gngFlowWorkspace == "Setup" && content is not null)
        {
            GngFlowHost.Content = null;
            GngFlowHost.Visibility = Visibility.Collapsed;
            SetupWorkspace.SetGngOperationContent(content);
            SetupWorkspace.ShowGngOperation(show);
            PresentWorkspace(_workspace);
        }
        else
        {
            SetupWorkspace.SetGngOperationContent(null);
            GngFlowHost.Content = content;
            GngFlowHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show)
            {
                AppsWorkspace.Visibility = SetupWorkspace.Visibility = Visibility.Collapsed;
                AppBulkActions.Visibility = CheckButton.Visibility = Visibility.Collapsed;
                MaintenanceButton.Visibility = Visibility.Collapsed;
            }
            else PresentWorkspace(_workspace);
        }
    }

    private void ReturnToGngFlow()
    {
        if (!HasGngFlow) return;
        _gngFlowWorkspace = _workspace;
        if (_workspace == "Setup") { _gngFlowSection = "Gng"; SetupWorkspace.ShowSection("Gng"); }
        UpdateGngPresentation();
    }

}
