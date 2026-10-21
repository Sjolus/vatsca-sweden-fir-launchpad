using System.Windows;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

// Legacy wizard host. The main workspace embeds this same editor directly.
public partial class AppConfigWindow : Window
{
    public AppSettings Settings => ProfileEditor.Settings;
    public string SaveResultMessage => ProfileEditor.SaveResultMessage;
    public bool ApplyNeedsAttention => ProfileEditor.ApplyNeedsAttention;

    public AppConfigWindow(AppSettings current, string euroscopeDataPath)
    {
        InitializeComponent();
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        ProfileEditor.Initialize(current, euroscopeDataPath,
            CredentialManagerService.LoadForEditing(CredentialManagerService.TargetVatsim) ?? "",
            CredentialManagerService.LoadForEditing(CredentialManagerService.TargetHoppie) ?? "");
        ProfileEditor.AcquireAction = () => MaintenanceLock.TryAcquire(out var lease) ? lease : null;
        ProfileEditor.Applied += (_, _, _) => DialogResult = true;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (ProfileEditor.IsBusy) e.Cancel = true;
        base.OnClosing(e);
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
