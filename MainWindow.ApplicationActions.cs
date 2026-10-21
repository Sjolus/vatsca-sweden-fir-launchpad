using System.Windows;
using System.Windows.Controls;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    private void FocusApplicationRow(CheckResult selected)
    {
        foreach (var row in AppList.Items.OfType<CheckResult>()) row.IsSelected = ReferenceEquals(row, selected);
        if (AppList.ItemContainerGenerator.ContainerFromItem(selected) is FrameworkElement container)
            container.BringIntoView();
        if (!string.IsNullOrWhiteSpace(selected.StatusMessage))
            LastCheckedText.Text = selected.DisplayName + ": " + selected.StatusMessage.ReplaceLineEndings(" ");
    }

    private void WorkspaceTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton tab) return;
        if (Equals(tab.Tag, "Apps")) AppsWorkspace_Click(sender, e);
        else SetupWizard_Click(sender, e);
        PresentWorkspace(_workspace);
    }

    private void RowAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CheckResult row } || !row.RowActionEnabled) return;
        switch (row.RowAction)
        {
            case CheckRowAction.Setup: OpenApplicationSetup(row); break;
            case CheckRowAction.SoftwareCancel: SoftwareCancel_Click(sender, e); break;
            case CheckRowAction.SoftwareUpdate:
            case CheckRowAction.SoftwareCheck: SoftwareUpdate_Click(sender, e); break;
            case CheckRowAction.Downloads: SoftwareDownloads_Click(sender, e); break;
            case CheckRowAction.SelfUpdate: SelfUpdate_Click(sender, e); break;
            case CheckRowAction.GngUpdate: GngUpdate_Click(sender, e); break;
            case CheckRowAction.Fonts: Fonts_Click(sender, e); break;
            case CheckRowAction.SaamanManage: SaamanManage_Click(sender, e); break;
            case CheckRowAction.EuroScopeManage: EuroScopeManage_Click(sender, e); break;
        }
    }

    private void OpenApplicationSetup(CheckResult row)
    {
        if (!row.CanOpenSetup || !CanRunSoftwareAction() || !CanRunSelfUpdateAction()) return;
        SetupWorkspace.ShowSection(row.HasFontsCheck ? "Gng" : "Applications");
        ShowWorkspace("Setup");
        SetupWorkspace.ShowNotice(row.HasSaamanManagement ? "Switch on SAAMAN below to use it in the APP/ACC profile. Switch it off to restore MAESTRO."
            : row.HasFontsCheck ? "Choose your EuroScope data folder or get the Swedish GNG package."
            : "Find " + row.DisplayName + " below to change its location or manage its installation.");
    }

    private void CopyApplicationStatus_CanExecute(object sender, System.Windows.Input.CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = sender is FrameworkElement { DataContext: CheckResult };
        e.Handled = true;
    }

    private void CopyApplicationStatus_Executed(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CheckResult row }) return;
        try { Clipboard.SetText(row.DetailHeading + "\n\n" + row.DetailText); }
        catch (System.Runtime.InteropServices.ExternalException) { LastCheckedText.Text = "The clipboard is busy. Try copying the status again."; }
        e.Handled = true;
    }
}
