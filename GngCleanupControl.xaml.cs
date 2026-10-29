using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class GngCleanupControl : UserControl
{
    private readonly string? _selectedProfile;
    private readonly string[] _protectedPaths;
    private readonly string? _storageNotice;
    private readonly ObservableCollection<CleanupRow> _rows = new();
    private GngCleanupPlan? _plan;
    private bool _isBusy;
    private string? _suggestedRecoveryFolder;
    private bool _autoCompared;

    public bool AttemptedChanges { get; private set; }
    public string Status => StatusText.Text;
    public bool IsBusy => _isBusy;
    public bool CanDismiss => !_isBusy;
    public event EventHandler? CloseRequested;
    public event Action? StateChanged;

    public GngCleanupControl(string root, string? selectedProfile, IEnumerable<string> protectedPaths,
        string? oldZip = null, string? newZip = null, string? recoveryFolder = null)
    {
        InitializeComponent();
        _selectedProfile = selectedProfile;
        _suggestedRecoveryFolder = recoveryFolder;
        _protectedPaths = protectedPaths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        RootBox.Text = root;
        BackupRootBox.Text = BackupRoot;
        _storageNotice = GetStorageNotice(root, BackupRoot);
        CandidatesGrid.ItemsSource = _rows;
        OldZipBox.Text = oldZip ?? string.Empty;
        NewZipBox.Text = newZip ?? string.Empty;
        SourceDetails.IsExpanded = recoveryFolder is null && (string.IsNullOrWhiteSpace(oldZip) || string.IsNullOrWhiteSpace(newZip));
        RestoreButton.Content = recoveryFolder is null ? "Restore a backup…" : "Restore this backup…";
        RestoreButton.ToolTip = recoveryFolder;
        Loaded += async (_, _) => await StartAsync();
        if (string.IsNullOrWhiteSpace(root))
            StatusText.Text = "Choose your EuroScope data folder in Setup before cleaning up or restoring files.";
        else if (_storageNotice is not null)
            StatusText.Text = _storageNotice;
        else if (recoveryFolder is not null)
            StatusText.Text = "Restore this backup to recover its archived files. Existing files will be kept.\n" + recoveryFolder;
        else if (!string.IsNullOrWhiteSpace(newZip))
            StatusText.Text = string.IsNullOrWhiteSpace(oldZip)
                ? "The new complete package reference is ready. Choose the old complete ZIP to compare, or go back and keep the old files."
                : "Checking the saved packages. Nothing will move until you select files and confirm.";
    }

    public async Task StartAsync()
    {
        if (_autoCompared) return;
        _autoCompared = true;
        if (_suggestedRecoveryFolder is null && !string.IsNullOrWhiteSpace(RootBox.Text) &&
            !string.IsNullOrWhiteSpace(OldZipBox.Text) && !string.IsNullOrWhiteSpace(NewZipBox.Text))
            await CompareAsync();
    }

    private void Downloads_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://files.aero-nav.com/ESAA") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        { StatusText.Text = "Could not open AeroNav. Visit https://files.aero-nav.com/ESAA in your browser and sign in to download complete packages."; }
    }

    private void Inputs_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (ArchiveButton is null) return;
        InvalidatePlan();
    }

    private void InvalidatePlan()
    {
        _plan = null;
        _rows.Clear();
        EmptyText.Visibility = Visibility.Collapsed;
        ArchiveButton.IsEnabled = SelectButton.IsEnabled = ClearButton.IsEnabled = false;
        SummaryText.Text = "Choose both Full Package ZIPs, then compare.";
    }

    private void BrowseZip_Click(object sender, RoutedEventArgs e)
    {
        bool old = (sender as FrameworkElement)?.Tag as string == "Old";
        var dialog = new OpenFileDialog
        {
            Title = old ? "Select the old complete GNG package" : "Select the new complete GNG package",
            Filter = "GNG ZIP packages (*.zip)|*.zip", CheckFileExists = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            (old ? OldZipBox : NewZipBox).Text = dialog.FileName;
    }

    private async void Compare_Click(object sender, RoutedEventArgs e) => await CompareAsync();

    private async Task CompareAsync()
    {
        if (_isBusy) return;
        if (string.IsNullOrWhiteSpace(RootBox.Text))
        {
            StatusText.Text = "Choose your EuroScope data folder in Setup before comparing packages.";
            return;
        }
        if (string.IsNullOrWhiteSpace(OldZipBox.Text) || string.IsNullOrWhiteSpace(NewZipBox.Text))
        {
            StatusText.Text = "Choose both complete ZIPs first: the old package you used before, and the newer package already installed in your EuroScope data folder.";
            return;
        }
        var root = RootBox.Text;
        var oldZip = OldZipBox.Text;
        var newZip = NewZipBox.Text;
        InvalidatePlan();
        SetBusy(true);
        StatusText.Text = "Comparing package contents and checking installed file references…";
        try
        {
            _plan = await Task.Run(() => GngCleanupService.BuildPlan(root, oldZip, newZip, _selectedProfile, _protectedPaths));
            ReportingEvents.Feature(ReportingFeature.GngCleanupReview, ReportingApplication.Gng);
            foreach (var candidate in _plan.Candidates) _rows.Add(new CleanupRow(candidate, UpdateSelection));
            EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SourceDetails.IsExpanded = false;
            StatusText.Text = _rows.Count == 0
                ? "No old files need cleanup."
                : "Nothing is selected. Files moved to a backup still use disk space.";
            if (_storageNotice is not null) StatusText.Text = _storageNotice;
            StatusText.ScrollToHome();
        }
        catch (Exception ex) { SourceDetails.IsExpanded = true; StatusText.Text = "Comparison could not finish: " + ex.Message; }
        finally { SetBusy(false); }
    }

    private void UpdateSelection()
    {
        int eligible = _rows.Count(r => r.IsEligible);
        int selected = _rows.Count(r => r.IsSelected);
        ArchiveButton.IsEnabled = !_isBusy && selected > 0;
        ArchiveButton.Content = selected == 0 ? "Move selected to backup…" : $"Move {selected} to backup…";
        SelectButton.IsEnabled = !_isBusy && eligible > 0;
        ClearButton.IsEnabled = !_isBusy && selected > 0;
        if (_plan is not null)
            SummaryText.Text = $"{_rows.Count} old package files found · {eligible} can move · {_rows.Count - eligible} must stay · {selected} selected";
    }

    private void SelectEligible_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.IsSelected = row.IsEligible;
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.IsSelected = false;
    }

    private async void Archive_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is null || _isBusy) return;
        var selected = _rows.Where(r => r.IsSelected && r.IsEligible).Select(r => r.RelativePath).ToArray();
        if (selected.Length == 0) return;
        if (MessageBox.Show(Window.GetWindow(this),
            $"Move {selected.Length} selected files out of:\n{_plan.Root}\n\nThey will be kept in a dated folder under:\n{BackupRoot}\n\nThe files remain in the backup and still use disk space. Use Restore a backup to put them back. Close EuroScope first. Files changed since this review will be kept.\n\nMove the selected files?",
            "Move files to backup", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        var plan = _plan;
        SetBusy(true);
        StatusText.Text = "Rechecking files and moving the selected files to backup…";
        ReportingEvents.Start(ReportingApplication.Gng, ReportingOperation.Cleanup);
        try
        {
            AttemptedChanges = true;
            var result = await Task.Run(() => GngCleanupService.ArchiveSelected(plan, selected));
            ActivityHistoryService.Record(ActivityApplication.Gng, ActivityOperation.Cleanup,
                result.Skipped.Count == 0 ? ActivityOutcome.Completed : ActivityOutcome.NeedsAttention,
                recoveryFolder: result.BackupFolder, dataFolder: plan.Root);
            InvalidatePlan();
            ShowResult(result, restoring: false);
        }
        catch (Exception ex)
        {
            ReportingEvents.RecordFailure(ex, ReportingApplication.Gng, ReportingOperation.Cleanup);
            ActivityHistoryService.Record(ActivityApplication.Gng, ActivityOperation.Cleanup, ActivityOutcome.Stopped,
                dataFolder: plan.Root);
            InvalidatePlan(); StatusText.Text = ex.Message;
        }
        finally { SetBusy(false); }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(RootBox.Text))
        {
            StatusText.Text = "Choose the original EuroScope data folder in Setup before restoring a backup.";
            return;
        }
        if (_isBusy) return;
        var folder = _suggestedRecoveryFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            var dialog = new OpenFolderDialog { Title = "Select a dated Launchpad cleanup backup" };
            if (Directory.Exists(BackupRoot)) dialog.InitialDirectory = BackupRoot;
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            folder = dialog.FolderName;
        }
        if (MessageBox.Show(Window.GetWindow(this),
            $"Restore files from:\n{folder}\n\nDestination:\n{RootBox.Text}\n\nExisting files will be kept. EuroScope must be closed.\n\nRestore this backup?",
            "Restore GNG backup", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var root = RootBox.Text;
        SetBusy(true);
        StatusText.Text = "Restoring backup files…";
        ReportingEvents.Start(ReportingApplication.Gng, ReportingOperation.RestoreCleanup);
        try
        {
            AttemptedChanges = true;
            var result = await Task.Run(() => GngCleanupService.Restore(folder, root, _protectedPaths));
            ActivityHistoryService.Record(ActivityApplication.Gng, ActivityOperation.RestoreCleanup,
                result.Skipped.Count == 0 ? ActivityOutcome.Completed : ActivityOutcome.NeedsAttention,
                recoveryFolder: result.BackupFolder, dataFolder: root);
            InvalidatePlan();
            ShowResult(result, restoring: true);
        }
        catch (Exception ex)
        {
            ReportingEvents.RecordFailure(ex, ReportingApplication.Gng, ReportingOperation.RestoreCleanup);
            ActivityHistoryService.Record(ActivityApplication.Gng, ActivityOperation.RestoreCleanup, ActivityOutcome.Stopped,
                recoveryFolder: folder, dataFolder: root);
            InvalidatePlan(); StatusText.Text = ex.Message;
        }
        finally { SetBusy(false); }
    }

    private void ShowResult(GngCleanupResult result, bool restoring)
    {
        var verb = restoring ? "Restored" : "Moved";
        StatusText.Text = $"{verb} {result.Files.Count} files. {result.Skipped.Count} skipped.";
        if (!string.IsNullOrWhiteSpace(result.BackupFolder))
        {
            StatusText.Text += $"\nBackup: {result.BackupFolder}";
            _suggestedRecoveryFolder = result.BackupFolder;
            RestoreButton.Content = "Restore this backup…";
            RestoreButton.ToolTip = result.BackupFolder;
        }
        if (result.Files.Count > 0)
            StatusText.Text += restoring
                ? "\nRestored files have moved out of this backup and back into your EuroScope data folder."
                : "\nFiles remain in the backup. Compare again after archiving old profiles to review sector or plugin files they previously kept in use.";
        if (result.Skipped.Count > 0) StatusText.Text += "\nSome files were left unchanged. Review these details before trying again:\n" + string.Join(Environment.NewLine, result.Skipped);
        StatusText.ScrollToHome();
    }

    private static string BackupRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VatscaUpdateChecker", "CleanupBackups");

    // This is early guidance only. The service validates actual paths and volume identity again.
    private static string? GetStorageNotice(string root, string backup)
    {
        try
        {
            if (!Path.IsPathFullyQualified(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            var dataDrive = Path.GetPathRoot(root);
            var backupDrive = Path.GetPathRoot(backup);
            return !string.Equals(dataDrive, backupDrive, StringComparison.OrdinalIgnoreCase)
                ? $"Cleanup needs data and backups on the same local drive. These paths appear to use different drives ({dataDrive} and {backupDrive}); moving files may not be supported. Comparing still makes no changes."
                : null;
        }
        catch (ArgumentException) { return null; }
    }

    private void SetBusy(bool value)
    {
        _isBusy = value;
        InputsPanel.IsEnabled = CompareButton.IsEnabled = CandidatesGrid.IsEnabled = RestoreButton.IsEnabled = CloseButton.IsEnabled = !value;
        Activity.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection();
        StateChanged?.Invoke();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (CanDismiss) CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private sealed class CleanupRow(GngCleanupCandidate candidate, Action selectionChanged) : INotifyPropertyChanged
    {
        private bool _isSelected;
        public string RelativePath => candidate.RelativePath;
        public bool IsEligible => candidate.IsEligible;
        public string Details => IsEligible ? candidate.Reason : $"Preserved: {candidate.BlockReason}";
        public string SizeText => candidate.Length >= 1024 * 1024 ? $"{candidate.Length / (1024d * 1024d):0.0} MB" : $"{candidate.Length / 1024d:0.0} KB";
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                bool next = value && IsEligible;
                if (_isSelected == next) return;
                _isSelected = next;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                selectionChanged();
            }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
