using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class SaamanWindow : Window
{
    private readonly SaamanInstallService _installer = new();
    private readonly SaamanReleaseService _releases = new();
    private SaamanInstallPlan? _plan;
    private SaamanRecovery? _recovery;
    private CancellationTokenSource? _cancellation;
    private bool _busy;
    public AppSettings Settings { get; }
    public bool SettingsChanged { get; private set; }
    public bool RequiresRestart { get; private set; }

    public SaamanWindow(AppSettings settings)
    {
        Settings = settings.Copy();
        InitializeComponent();
        RefreshPaths();
        Closing += OnClosing;
    }
    private void RefreshPaths()
    {
        try { DllPathText.Text = SaamanInstallService.GetSuggestedPath(Settings); }
        catch (Exception ex) { DllPathText.Text = ex.Message; }
        ProfilePathText.Text = string.IsNullOrWhiteSpace(Settings.SaamanProfilePath)
            ? Path.Combine(Settings.EuroscopeDataPath, "ESAA APP ACC.prf") : Settings.SaamanProfilePath;
    }
    private void InvalidateReview()
    {
        _plan = null; _recovery = null;
        ConfirmChange.IsChecked = false; ConfirmChange.IsEnabled = false;
        ApplyButton.IsEnabled = false; RestoreButton.IsEnabled = false;
    }
    private async void Review_Click(object sender, RoutedEventArgs e)
    {
        InvalidateReview();
        SetBusy(true);
        try
        {
            var release = await _releases.GetLatestAsync(_cancellation!.Token)
                ?? throw new InvalidOperationException("No published stable SAAMAN release is available.");
            _plan = _installer.Preview(Settings, release);
            bool runtime = WebViewRuntimeService.GetInstalledVersion() is not null;
            ReviewText.Text = SaamanReviewPresentation.Format(_plan, runtime);
            ApplyButton.Content = _plan.Previous is null ? "Install reviewed release" : "Install reviewed update";
            ConfirmChange.IsEnabled = true;
            StatusText.Text = "Review the destination and close every EuroScope instance before installing.";
            ContentScroll.ScrollToBottom();
        }
        catch (OperationCanceledException) { StatusText.Text = "Review cancelled. No plugin was installed."; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { SetBusy(false); }
    }
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || RequiresRestart || _plan is null || ConfirmChange.IsChecked != true) return;
        var plan = _plan;
        var operation = plan.Previous is null ? ActivityOperation.Install : ActivityOperation.Update;
        SetBusy(true);
        try
        {
            var result = await _installer.InstallAsync(plan, Settings, new Progress<SaamanProgress>(progress =>
            {
                if (!_busy) return;
                StatusText.Text = progress.Message;
                Activity.IsIndeterminate = progress.Percent is null;
                Activity.Value = progress.Percent ?? 0;
                if (progress.Message.StartsWith("Applying", StringComparison.Ordinal)) CancelOperationButton.IsEnabled = false;
            }), _cancellation!.Token);
            ActivityHistoryService.Record(ActivityApplication.Saaman, operation, ActivityOutcome.Completed,
                result.Version, Path.GetDirectoryName(result.ReceiptPath), dataFolder: Settings.EuroscopeDataPath,
                previousVersion: plan.Previous?.Version);
            Settings.SaamanDllPath = result.DllPath;
            SettingsChanged = true;
            RefreshPaths();
            StatusText.Text = $"SAAMAN {result.Version} installed. Recovery receipt: {result.ReceiptPath}\n" +
                "Open Controller profile to opt into profile integration.";
            InvalidateReview();
        }
        catch (OperationCanceledException)
        {
            ActivityHistoryService.Record(ActivityApplication.Saaman, operation, ActivityOutcome.Cancelled,
                plan.Release.Version, _installer.LastRecoveryFolder, dataFolder: Settings.EuroscopeDataPath,
                previousVersion: plan.Previous?.Version);
            StatusText.Text = "Preparation cancelled. No installed DLL was replaced.";
        }
        catch (Exception ex)
        {
            ActivityHistoryService.Record(ActivityApplication.Saaman, operation, ActivityOutcome.Stopped,
                plan.Release.Version, _installer.LastRecoveryFolder, dataFolder: Settings.EuroscopeDataPath,
                previousVersion: plan.Previous?.Version);
            StatusText.Text = ex.Message + "\nIf replacement had started, use Review latest recovery.";
            InvalidateReview();
        }
        finally { SetBusy(false); }
    }
    private void Adopt_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Choose the existing SAAMAN plugin DLL", Filter = "SAAMAN plugin DLL (*.dll)|*.dll", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            RequireProfileDisabledForPathChange(picker.FileName, Settings.SaamanDllPath);
            var identity = _installer.Adopt(picker.FileName, Settings);
            if (MessageBox.Show(this, $"Use SAAMAN {identity.Version} from:\n{identity.Path}\n\n" +
                "This saves the selected path. Profile integration is a separate Controller profile choice.",
                "Use existing SAAMAN", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            Settings.SaamanDllPath = identity.Path;
            SettingsChanged = true;
            InvalidateReview(); RefreshPaths();
            StatusText.Text = $"Selected SAAMAN {identity.Version}. No files were changed.";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void ChooseProfile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "Choose SAAMAN's EuroScope profile", Filter = "EuroScope profiles (*.prf)|*.prf", CheckFileExists = true };
        if (Directory.Exists(Settings.EuroscopeDataPath)) picker.InitialDirectory = Settings.EuroscopeDataPath;
        if (picker.ShowDialog(this) != true) return;
        try
        {
            string selected = SaamanFileSafety.Canonical(picker.FileName);
            string root = SaamanFileSafety.Canonical(Settings.EuroscopeDataPath);
            if (!selected.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ||
                !Path.GetExtension(selected).Equals(".prf", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Choose a .prf file inside the configured EuroScope data folder.");
            string previous = string.IsNullOrWhiteSpace(Settings.SaamanProfilePath)
                ? Path.Combine(root, "ESAA APP ACC.prf") : Settings.SaamanProfilePath;
            RequireProfileDisabledForPathChange(selected, previous);
            Settings.SaamanProfilePath = selected;
            SettingsChanged = true;
            InvalidateReview(); RefreshPaths();
            StatusText.Text = "Profile selected. Enable SAAMAN separately in Controller profile.";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void RequireProfileDisabledForPathChange(string selected, string previous)
    {
        if (Settings.SaamanProfileManaged && Settings.PatchSaaman &&
            !selected.Equals(previous, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Disable SAAMAN in Controller profile and apply that change before selecting a different DLL or profile.");
    }
    private void Recovery_Click(object sender, RoutedEventArgs e)
    {
        InvalidateReview();
        try
        {
            _recovery = _installer.FindRecovery(SaamanInstallService.GetSuggestedPath(Settings));
            if (_recovery is null) { StatusText.Text = "No previous DLL is available to restore for this path. Fresh installations keep their DLL and receipt."; return; }
            ReviewText.Text = $"Restore SAAMAN change for:\n{_recovery.TargetPath}\n\n" +
                $"Restore the verified DLL saved before version {_recovery.Version} (previous version {_recovery.PreviousVersion})." +
                "\n\nChanged or newer DLLs are retained and recovery stops. Close every EuroScope instance first.\n" +
                "SAAMAN personal state and EuroScope profiles are preserved.\n\nReceipt: " + _recovery.ReceiptPath;
            ConfirmChange.IsEnabled = true;
            StatusText.Text = "Review this recovery and confirm before restoring.";
            ContentScroll.ScrollToBottom();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || RequiresRestart || _recovery is null || ConfirmChange.IsChecked != true) return;
        var recovery = _recovery;
        SetBusy(true);
        CancelOperationButton.Visibility = Visibility.Collapsed;
        try
        {
            await Task.Run(() => _installer.Restore(recovery, Settings));
            ActivityHistoryService.Record(ActivityApplication.Saaman, ActivityOperation.Restore, ActivityOutcome.Completed,
                recovery.PreviousVersion, Path.GetDirectoryName(recovery.ReceiptPath), dataFolder: Settings.EuroscopeDataPath,
                previousVersion: recovery.Version);
            StatusText.Text = "The reviewed SAAMAN change was restored. Profiles and SAAMAN preferences were preserved.";
            InvalidateReview();
        }
        catch (Exception ex)
        {
            ActivityHistoryService.Record(ActivityApplication.Saaman, ActivityOperation.Restore, ActivityOutcome.Stopped,
                recovery.PreviousVersion, Path.GetDirectoryName(recovery.ReceiptPath), dataFolder: Settings.EuroscopeDataPath,
                previousVersion: recovery.Version);
            StatusText.Text = ex.Message;
        }
        finally { SetBusy(false); }
    }
    private async void Runtime_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? version = WebViewRuntimeService.GetInstalledVersion();
            if (version is not null) { StatusText.Text = "Microsoft Edge WebView2 Runtime is available: " + version; return; }
            if (MessageBox.Show(this, "SAAMAN requires Microsoft Edge WebView2 Runtime. Download and run the verified Microsoft setup now?",
                "Install WebView2 Runtime", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            if (!MaintenanceLock.TryAcquire(out var lease)) throw new InvalidOperationException("Another maintenance operation is active.");
            using (lease)
            {
                SetBusy(true);
                CancelOperationButton.Visibility = Visibility.Collapsed;
                await WebViewRuntimeService.InstallAsync(new Progress<string>(message => StatusText.Text = message),
                    onRestartRequired: () => RequiresRestart = true);
                StatusText.Text = RequiresRestart ? "Restart Windows before continuing installation." : "WebView2 Runtime is available.";
            }
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { SetBusy(false); }
    }
    private void SetBusy(bool busy)
    {
        _busy = busy;
        OptionsPanel.IsEnabled = !busy && !RequiresRestart;
        ReviewButton.IsEnabled = !busy && !RequiresRestart;
        RecoveryButton.IsEnabled = !busy && !RequiresRestart;
        CloseButton.IsEnabled = !busy;
        ConfirmChange.IsEnabled = !busy && !RequiresRestart && (_plan is not null || _recovery is not null);
        Activity.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Activity.IsIndeterminate = true;
        CancelOperationButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelOperationButton.IsEnabled = true;
        if (busy) _cancellation = new CancellationTokenSource();
        else { _cancellation?.Dispose(); _cancellation = null; }
        UpdateConfirmation();
    }
    private void UpdateConfirmation()
    {
        ApplyButton.IsEnabled = !_busy && !RequiresRestart && ConfirmChange.IsChecked == true && _plan is not null;
        RestoreButton.IsEnabled = !_busy && !RequiresRestart && ConfirmChange.IsChecked == true && _recovery is not null;
    }
    private void ConfirmationChanged(object sender, RoutedEventArgs e) { if (ApplyButton is not null) UpdateConfirmation(); }
    private void ReleaseNotes_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(SaamanReleaseService.ReleasesUrl) { UseShellExecute = true });
    private void CancelOperation_Click(object sender, RoutedEventArgs e) => _cancellation?.Cancel();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_busy) { e.Cancel = true; StatusText.Text = "Wait for the current SAAMAN operation to finish."; }
    }
}
