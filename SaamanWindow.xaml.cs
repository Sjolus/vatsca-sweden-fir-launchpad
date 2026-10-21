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
    private readonly Action<AppSettings>? _saveChoices;
    private SaamanRecovery? _recovery;
    private CancellationTokenSource? _cancellation;
    private bool _busy;
    private readonly System.Windows.Threading.DispatcherTimer _processTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    public AppSettings Settings { get; }
    public bool SettingsChanged { get; private set; }
    public bool RequiresRestart { get; private set; }
    public SaamanRelease? LatestReviewedRelease { get; private set; }

    public SaamanWindow(AppSettings settings, Action<AppSettings>? saveChoices = null)
    {
        Settings = settings.Copy();
        _saveChoices = saveChoices;
        InitializeComponent();
        RefreshPaths();
        Closing += OnClosing;
        _processTimer.Tick += (_, _) => { if (!_busy) UpdateConfirmation(); };
        Loaded += (_, _) => { UpdateConfirmation(); _processTimer.Start(); };
        Closed += (_, _) => _processTimer.Stop();
    }
    private void RefreshPaths()
    {
        try { DllPathText.Text = SaamanInstallService.GetSuggestedPath(Settings); }
        catch (Exception ex) { DllPathText.Text = ex.Message; }
        ProfilePathText.Text = string.IsNullOrWhiteSpace(Settings.SaamanProfilePath)
            ? Path.Combine(Settings.EuroscopeDataPath, "ESAA APP ACC.prf") : Settings.SaamanProfilePath;
        RefreshProfileChoice();
    }
    private void InvalidateReview()
    {
        _recovery = null;
        ConfirmChange.IsChecked = false; ConfirmChange.IsEnabled = false;
        RestoreButton.IsEnabled = false;
        ConfirmChange.Visibility = RestoreButton.Visibility =
            ReviewPanel.Visibility = CancelReviewButton.Visibility = Visibility.Collapsed;
    }
    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || RequiresRestart) return;
        InvalidateReview();
        SaamanInstallPlan? plan = null;
        bool installationStarted = false;
        var stage = ReportingStage.Prepare;
        var operation = File.Exists(DllPathText.Text) ? ActivityOperation.Update : ActivityOperation.Install;
        var reportedOperation = operation == ActivityOperation.Update ? ReportingOperation.Update : ReportingOperation.Install;
        ReportingEvents.Start(ReportingApplication.Saaman, reportedOperation);
        SetBusy(true);
        try
        {
            EuroScopeProcessGuard.RequireClosed();
            StatusText.Text = "Checking the latest stable SAAMAN release…";
            stage = ReportingStage.Check;
            var release = await _releases.GetLatestAsync(_cancellation!.Token)
                ?? throw new InvalidOperationException("No published stable SAAMAN release is available.");
            LatestReviewedRelease = release;
            stage = ReportingStage.Prepare;
            plan = _installer.Preview(Settings, release);
            _cancellation.Token.ThrowIfCancellationRequested();
            if (plan.Previous is { } current && current.Version.Equals(release.Version, StringComparison.Ordinal) && current.Size == release.Dll.Size &&
                current.Sha256.Equals(release.Dll.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Settings.SaamanDllPath = plan.TargetPath;
                SettingsChanged = true;
                RefreshPaths();
                StatusText.Text = $"SAAMAN {current.Version} is already up to date.";
                bool choicesSaved = SaveChangedChoices();
                ReportingEvents.Complete(ReportingApplication.Saaman, reportedOperation,
                    choicesSaved ? ReportingOutcome.Completed : ReportingOutcome.Failed,
                    choicesSaved ? ReportingReason.AlreadyConfigured : ReportingReason.SaveFailed);
                return;
            }
            installationStarted = true;
            stage = ReportingStage.Download;
            var result = await _installer.InstallAsync(plan, Settings, new Progress<SaamanProgress>(progress =>
            {
                if (!_busy) return;
                StatusText.Text = progress.Message;
                Activity.IsIndeterminate = progress.Percent is null;
                Activity.Value = progress.Percent ?? 0;
                if (progress.Message.StartsWith("Applying", StringComparison.Ordinal))
                {
                    stage = ReportingStage.Apply;
                    CancelOperationButton.IsEnabled = false;
                }
            }), _cancellation!.Token);
            ActivityHistoryService.Record(ActivityApplication.Saaman, operation, ActivityOutcome.Completed,
                result.Version, Path.GetDirectoryName(result.ReceiptPath), dataFolder: Settings.EuroscopeDataPath,
                previousVersion: plan.Previous?.Version);
            Settings.SaamanDllPath = result.DllPath;
            SettingsChanged = true;
            RefreshPaths();
            StatusText.Text = $"SAAMAN {result.Version} installed. " + (Settings.PatchSaaman
                ? "It remains enabled in your profile." : "Enable SAAMAN above to use it in your profile.");
            InvalidateReview();
            bool saved = SaveChangedChoices();
            ReportingEvents.Complete(ReportingApplication.Saaman, reportedOperation,
                saved ? ReportingOutcome.Completed : ReportingOutcome.Partial, saved ? null : ReportingReason.SaveFailed,
                details: new ReportingDetails { PackageVersion = ReportingEvents.PublicVersion(result.Version),
                    PreviousVersion = ReportingEvents.PublicVersion(plan.Previous?.Version), RecoveryAvailable = true });
        }
        catch (OperationCanceledException)
        {
            ReportingEvents.Complete(ReportingApplication.Saaman, reportedOperation, ReportingOutcome.Cancelled);
            if (plan is not null) ActivityHistoryService.Record(ActivityApplication.Saaman, operation, ActivityOutcome.Cancelled,
                plan.Release.Version, installationStarted ? _installer.LastRecoveryFolder : null, dataFolder: Settings.EuroscopeDataPath,
                previousVersion: plan.Previous?.Version);
            StatusText.Text = "Preparation cancelled. No installed DLL was replaced.";
        }
        catch (Exception ex)
        {
            ReportingEvents.Stop(ex, ReportingApplication.Saaman, reportedOperation,
                ex is InvalidDataException && installationStarted ? ReportingStage.Verify : stage);
            if (plan is not null) ActivityHistoryService.Record(ActivityApplication.Saaman, operation, ActivityOutcome.Stopped,
                plan.Release.Version, installationStarted ? _installer.LastRecoveryFolder : null, dataFolder: Settings.EuroscopeDataPath,
                previousVersion: plan.Previous?.Version);
            StatusText.Text = ex.Message + (!installationStarted || _installer.LastRecoveryFolder is null ? "" : "\nUse Restore previous DLL to recover an interrupted replacement.");
            InvalidateReview();
        }
        finally { SetBusy(false); }
    }
    private void Adopt_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || RequiresRestart) return;
        var picker = new OpenFileDialog { Title = "Choose the existing SAAMAN plugin DLL", Filter = "SAAMAN plugin DLL (*.dll)|*.dll", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        ApplyLocalChange(() =>
        {
            var identity = _installer.Adopt(picker.FileName, Settings);
            SaamanProfileChange? profile = null;
            if (Settings.PatchSaaman)
            {
                var policy = SaamanProfileService.Capture(Settings, Settings.EuroscopeDataPath)
                    ?? throw new InvalidOperationException("The enabled SAAMAN profile could not be identified.");
                profile = SaamanProfileService.BuildRelocationPlan(policy, identity.Path, Settings.EuroscopeDataPath);
            }
            return (profile, identity);
        }, Settings.PatchSaaman ? ReportingOperation.ConfigurePluginProfile : ReportingOperation.Adopt);
    }
    private void ChooseProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || RequiresRestart) return;
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
            RequireProfileDisabledForTargetChange(selected, previous);
            Settings.SaamanProfilePath = selected;
            SettingsChanged = true;
            InvalidateReview(); RefreshPaths();
            StatusText.Text = "Profile selected. Enable SAAMAN below to use it here.";
            SaveChangedChoices();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void RequireProfileDisabledForTargetChange(string selected, string previous)
    {
        if (Settings.PatchSaaman &&
            !selected.Equals(previous, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Disable SAAMAN here before switching profiles. This restores the original MAESTRO entries in the current profile.");
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
            ShowRecoveryReview();
            StatusText.Text = "Review this recovery and confirm before restoring.";
            ContentScroll.ScrollToBottom();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || RequiresRestart || _recovery is null || ConfirmChange.IsChecked != true) return;
        var recovery = _recovery;
        ReportingEvents.Start(ReportingApplication.Saaman, ReportingOperation.Restore);
        SetBusy(true);
        CancelOperationButton.Visibility = Visibility.Collapsed;
        try
        {
            await Task.Run(() => _installer.Restore(recovery, Settings));
            ReportingEvents.Complete(ReportingApplication.Saaman, ReportingOperation.Restore, ReportingOutcome.Completed,
                details: new ReportingDetails { PackageVersion = ReportingEvents.PublicVersion(recovery.PreviousVersion),
                    PreviousVersion = ReportingEvents.PublicVersion(recovery.Version), RecoveryAvailable = true });
            ActivityHistoryService.Record(ActivityApplication.Saaman, ActivityOperation.Restore, ActivityOutcome.Completed,
                recovery.PreviousVersion, Path.GetDirectoryName(recovery.ReceiptPath), dataFolder: Settings.EuroscopeDataPath,
                previousVersion: recovery.Version);
            StatusText.Text = "The reviewed SAAMAN change was restored. Profiles and SAAMAN preferences were preserved.";
            InvalidateReview();
        }
        catch (Exception ex)
        {
            ReportingEvents.Stop(ex, ReportingApplication.Saaman, ReportingOperation.Restore);
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
        RecoveryButton.IsEnabled = !busy && !RequiresRestart;
        CancelReviewButton.IsEnabled = !busy;
        CloseButton.IsEnabled = !busy;
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
        if (ProcessBlockText is null) return;
        string? blocked = !_busy ? EuroScopeProcessGuard.GetBlockingReason() : null;
        PresentActionAvailability(_busy, RequiresRestart, Settings.PatchSaaman, _recovery is not null, blocked);
    }
    private void ConfirmationChanged(object sender, RoutedEventArgs e) { if (InstallButton is not null) UpdateConfirmation(); }
    private void ReleaseNotes_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(SaamanReleaseService.ReleasesUrl) { UseShellExecute = true });
    private void CancelOperation_Click(object sender, RoutedEventArgs e) => _cancellation?.Cancel();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_busy) { e.Cancel = true; StatusText.Text = "Wait for the current SAAMAN operation to finish."; }
    }
}
