using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class MaintenanceWindow : Window
{
    private sealed class Choice(AtcRemovalTarget target)
    {
        public AtcRemovalTarget Target { get; } = target;
        public bool RemoveApplication { get; set; }
        public bool RemoveData { get; set; }
        public string Details => string.Join("\n", new[] { Target.Reason }.Concat(Target.Warnings).Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    private readonly AtcMaintenanceService _service;
    private readonly AppSettings _settings;
    private readonly ObservableCollection<Choice> _choices = new();
    private readonly List<string> _completedExports = new();
    private AtcMaintenancePlan? _plan;
    private IDisposable? _lease;
    private bool _busy;
    private bool _finished;
    private bool _handoff;
    private bool _discoveryReady;
    private bool _applying;
    public bool MadeChanges { get; private set; }
    public bool RestartRequired { get; private set; }

    public MaintenanceWindow(AppSettings settings, bool uninstallLaunchpad = false)
    {
        InitializeComponent();
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        _settings = settings;
        _service = new(settings);
        RemoveLaunchpad.IsChecked = uninstallLaunchpad;
        RemoveLaunchpad.IsEnabled = LaunchpadUninstallService.CanUninstall(out var reason);
        RemoveLaunchpad.ToolTip = reason;
        Loaded += async (_, _) => await DiscoverAsync();
    }

    private async Task DiscoverAsync(string? priorOutcome = null)
    {
        _discoveryReady = false;
        _choices.Clear();
        TargetsList.ItemsSource = _choices;
        SetBusy(true, "Identifying configured applications and supported removal routes…");
        try
        {
            foreach (var target in await Task.Run(_service.Discover)) _choices.Add(new(target));
            _discoveryReady = true;
            StatusText.Text = OutcomePrefix(priorOutcome) + "Current applications loaded. Choose apps and optional settings, then review. Unsupported copies are explained in their row.";
        }
        catch (Exception ex)
        {
            StatusText.Text = OutcomePrefix(priorOutcome) + "Could not inspect applications: " + ex.Message +
                " Use Refresh applications to try again. Removal stays disabled until discovery succeeds.";
        }
        finally { SetBusy(false); }
    }

    private static string OutcomePrefix(string? outcome) => string.IsNullOrWhiteSpace(outcome) ? "" : outcome + "\n\n";

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _finished) return;
        ClearDestructiveSelections();
        await DiscoverAsync("All removal selections were cleared." +
            (_completedExports.Count == 0 ? "" : " Completed recovery exports remain listed below."));
    }

    private void SelectApps_Click(object sender, RoutedEventArgs e)
    {
        foreach (var choice in _choices) choice.RemoveApplication = choice.Target.CanRemoveApplication;
        TargetsList.Items.Refresh();
        InvalidateReview();
    }

    private void SelectData_Click(object sender, RoutedEventArgs e)
    {
        foreach (var choice in _choices.Where(c => c.RemoveApplication)) choice.RemoveData = choice.Target.CanRemoveData;
        TargetsList.Items.Refresh();
        InvalidateReview();
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => ClearDestructiveSelections();

    private void ClearDestructiveSelections()
    {
        foreach (var choice in _choices) { choice.RemoveApplication = false; choice.RemoveData = false; }
        foreach (var check in new[] { RemoveLaunchpad, RemoveSettings, RemoveSessions, RemoveCredentials, RemoveDownloads, RemoveBackups }) check.IsChecked = false;
        TargetsList.Items.Refresh();
        InvalidateReview();
    }

    private void SelectionChanged(object sender, RoutedEventArgs e) => InvalidateReview();
    private void ConfirmationChanged(object sender, RoutedEventArgs e) => ApplyButton.IsEnabled = !_busy && !_finished && _plan != null && ConfirmRemoval.IsChecked == true;

    private void InvalidateReview()
    {
        _plan = null;
        _lease?.Dispose();
        _lease = null;
        ConfirmRemoval.IsChecked = false;
        ConfirmRemoval.IsEnabled = false;
        ApplyButton.IsEnabled = false;
        ReviewText.Text = "Selection changed. Review again before removing anything.";
    }

    private void ChooseBackup_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose a private recovery export folder outside the selected data" };
        if (picker.ShowDialog(this) != true) return;
        BackupPath.Text = picker.FolderName;
        ExportBackup.IsChecked = true;
        InvalidateReview();
    }

    private async void Review_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _finished || !_discoveryReady) return;
        InvalidateReview();
        if (!MaintenanceLock.TryAcquire(out _lease))
        {
            StatusText.Text = "Another Launchpad operation is running. Wait for it to finish and review again.";
            return;
        }
        SetBusy(true, "Checking selected paths and building a complete review…");
        try
        {
            RequireOtherLaunchpadsClosed();
            if (RemoveLaunchpad.IsChecked == true) LaunchpadUninstallService.ValidateConfiguredPaths(_settings,
                ExportBackup.IsChecked == true ? BackupPath.Text : null);
            var selected = _choices.Select(c => new AtcRemovalSelection(c.Target, c.RemoveApplication, c.RemoveData)).ToArray();
            var local = new LaunchpadDataSelection(RemoveSettings.IsChecked == true, RemoveSessions.IsChecked == true,
                RemoveCredentials.IsChecked == true, RemoveDownloads.IsChecked == true, RemoveBackups.IsChecked == true);
            if (!selected.Any(s => s.RemoveApplication || s.RemoveData) && !local.Settings && !local.BrowserSessions && !local.Credentials && !local.Downloads && !local.Backups && RemoveLaunchpad.IsChecked != true)
                throw new InvalidOperationException("Select at least one application or data category.");
            string? backup = null;
            var needsFileExport = selected.Any(s => s.RemoveApplication || s.RemoveData) || local.Settings || local.BrowserSessions || local.Downloads || local.Backups;
            if (ExportBackup.IsChecked == true && needsFileExport)
            {
                if (string.IsNullOrWhiteSpace(BackupPath.Text)) throw new InvalidOperationException("Choose a recovery export folder, or turn off the recovery copy.");
                backup = BackupPath.Text;
            }
            _plan = await Task.Run(() => _service.Preview(selected, local, backup));
            if (RemoveLaunchpad.IsChecked == true)
            {
                // Presentation only: even this empty selection keeps the service plan
                // and its normal validation/apply path below.
                ReviewText.Text = !needsFileExport && !local.Credentials
                    ? "Uninstall Sweden FIR Launchpad only\n\n" +
                      "Launchpad will close, and its installation folder and shortcuts will be removed.\n\n" +
                      "Kept: your configured ATC applications and their settings; Launchpad preferences, saved credentials, browser sessions, client downloads and recovery backups.\n\n" +
                      "No separate settings or data cleanup is selected. No recovery export will be created for this selection."
                    : "Uninstall Sweden FIR Launchpad\n\n" +
                      "Launchpad will be uninstalled last, after all selected work below succeeds.\n\n" + _plan.Review;
            }
            else ReviewText.Text = _plan.Review + "\nLaunchpad will remain installed.";
            ConfirmRemoval.IsEnabled = true;
            StatusText.Text = "Review the paths, then confirm. No files have been changed.";
        }
        catch (Exception ex)
        {
            _lease?.Dispose(); _lease = null;
            StatusText.Text = ex.Message;
        }
        finally { SetBusy(false); }
        if (_plan != null) ShowReviewedPlan();
    }

    private void ShowReviewedPlan() => ReviewScrollHelper.Show(ContentScroll, ReviewPanel, ReviewText,
        () => !_busy && _plan != null && IsVisible);

    private void RememberRecoveryExport()
    {
        var folder = _service.LastBackupFolder;
        if (folder == null) return;
        if (!_completedExports.Contains(folder, StringComparer.OrdinalIgnoreCase)) _completedExports.Add(folder);
        RecoveryText.Text = "Recovery copies saved. Open RESTORE.txt in each folder for manual recovery; files are not restored automatically.\n" +
            string.Join("\n", _completedExports);
        RecoveryText.Visibility = Visibility.Visible;
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _finished || _plan == null || ConfirmRemoval.IsChecked != true || _lease == null) return;
        var plan = _plan;
        string? failureOutcome = null;
        _applying = true;
        SetBusy(true, "Verifying the reviewed files and preparing recovery…");
        try
        {
            RequireOtherLaunchpadsClosed();
            if (RemoveLaunchpad.IsChecked == true) LaunchpadUninstallService.ValidateConfiguredPaths(_settings, plan.BackupDestination);
            var progress = new Progress<string>(message => { if (_applying) StatusText.Text = message; });
            // Once removal starts the window must stay open. No uninstaller is killed for cancellation.
            MadeChanges = true;
            var backup = await Task.Run(() => _service.ApplyAsync(plan, progress, CancellationToken.None));
            RememberRecoveryExport();
            StatusText.Text = "Selected removal completed." + (backup == null ? " No recovery export was requested." : " Recovery export: " + backup);
            if (RemoveLaunchpad.IsChecked == true)
            {
                StatusText.Text = "Selected ATC removal finished. Handing Launchpad removal to its uninstaller…";
                LaunchpadUninstallService.Uninstall(_settings, backup);
                _handoff = true;
                Application.Current.Shutdown();
            }
            _finished = true;
        }
        catch (Exception ex)
        {
            RememberRecoveryExport();
            RestartRequired |= _service.RestartRequired;
            failureOutcome = "Stopped: " + ex.Message + " Completed removals are not undone. All removal selections have been cleared." +
                (RestartRequired ? " Restart Windows before further changes. Remaining removal and data cleanup were not performed; Launchpad was not uninstalled."
                    : " Review the refreshed application state before retrying.") +
                (_service.LastBackupFolder == null ? " No completed recovery export was created by this operation." : " Recovery export location (check before restoring): " + _service.LastBackupFolder);
            StatusText.Text = failureOutcome;
        }
        finally
        {
            RestartRequired |= _service.RestartRequired;
            _finished |= RestartRequired;
            _applying = false;
            _plan = null;
            _lease?.Dispose(); _lease = null;
            ConfirmRemoval.IsChecked = false;
            ConfirmRemoval.IsEnabled = false;
            if (!_handoff && failureOutcome != null)
            {
                ClearDestructiveSelections();
                if (RestartRequired) SetBusy(false);
                else await DiscoverAsync(failureOutcome);
            }
            else if (!_handoff) SetBusy(false);
            if (_finished) SelectionPanel.IsEnabled = false;
        }
    }

    private static void RequireOtherLaunchpadsClosed()
    {
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Environment.ProcessPath));
        try
        {
            if (processes.Any(process => process.Id != Environment.ProcessId))
                throw new InvalidOperationException("Close other Launchpad windows before removal. Their running updates and unsaved settings must finish first.");
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        SelectionPanel.IsEnabled = !busy && !_finished && _discoveryReady;
        ReviewButton.IsEnabled = !busy && !_finished && _discoveryReady;
        RefreshButton.IsEnabled = !busy && !_finished;
        ConfirmRemoval.IsEnabled = !busy && !_finished && _plan != null;
        ApplyButton.IsEnabled = !busy && !_finished && _plan != null && ConfirmRemoval.IsChecked == true;
        CloseButton.IsEnabled = !busy;
        Activity.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (message != null) StatusText.Text = message;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_busy && !_handoff) { e.Cancel = true; StatusText.Text = "Wait for the current operation to finish. You can minimize this window."; }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _lease?.Dispose();
        base.OnClosed(e);
    }
}
