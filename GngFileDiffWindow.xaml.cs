using System.Windows;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class GngFileDiffWindow : Window
{
    public GngFileDiffControl Preview { get; }

    public GngFileDiffWindow(GngFilePreview preview, GngTextDiffResult? diff)
    {
        InitializeComponent();
        Preview = new(preview, diff);
        Content = Preview;
        NameScope.SetNameScope(this, NameScope.GetNameScope(Preview));
        Preview.CloseRequested += (_, _) => Close();
        if (!preview.HasText) { Width = 780; Height = 360; MinHeight = 300; }
    }
}
