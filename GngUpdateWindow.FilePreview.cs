using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class GngUpdateWindow
{
    private async void ViewFileChanges_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: GngUpdateFile file })
            await ShowFilePreviewAsync(file);
    }

    private async void FilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(FilesGrid, source) is not DataGridRow { Item: GngUpdateFile file }) return;
        e.Handled = true;
        await ShowFilePreviewAsync(file);
    }

    private async Task ShowFilePreviewAsync(GngUpdateFile file)
    {
        if (!CanGetPackage || _plan is not { } plan) return;
        var priorStatus = StatusText.Text;
        var priorStage = DownloadStageText.Text;
        GngFilePreview? preview = null;
        GngTextDiffResult? diff = null;
        SetBusy(true, "Checking the selected file and preparing its expected changes…", "Preparing file comparison");
        try
        {
            (preview, diff) = await Task.Run(() =>
            {
                var result = GngUpdateService.PreviewFile(plan, file.RelativePath);
                return (result, result.HasText ? GngTextDiff.Create(result.BeforeText!, result.AfterText!) : null);
            });
            StatusText.Text = priorStatus;
            SetStage(priorStage);
        }
        catch (Exception)
        {
            // A stale or unreadable preview must not leave an old approval enabled.
            ConfirmInstall.IsChecked = false;
            _plan = null;
            ShowIssue("File comparison unavailable", "The file or package may have changed, or could not be read. Choose another package to prepare a fresh review before installing.");
        }
        finally { SetBusy(false); }
        if (preview is not null && !_closed)
            new GngFileDiffWindow(preview, diff) { Owner = this }.ShowDialog();
    }
}
