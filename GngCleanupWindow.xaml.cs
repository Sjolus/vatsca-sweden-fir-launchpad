using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class GngCleanupWindow : Window
{
    private readonly string? _selectedProfile;
    private readonly string[] _protectedPaths;
    private readonly string? _storageNotice;
    private readonly ObservableCollection<CleanupRow> _rows = new();
    private GngCleanupPlan? _plan;
    private bool _isBusy;

    public bool AttemptedChanges { get; private set; }

    public GngCleanupWindow(string root, string? selectedProfile, IEnumerable<string> protectedPaths,
        string? oldZip = null, string? newZip = null)
    {
        InitializeComponent();
        _selectedProfile = selectedProfile;
        _protectedPaths = protectedPaths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        RootBox.Text = root;
        BackupRootBox.Text = BackupRoot;
        _storageNotice = GetStorageNotice(root, BackupRoot);
        CandidatesGrid.ItemsSource = _rows;
        OldZipBox.Text = oldZip ?? string.Empty;
        NewZipBox.Text = newZip ?? string.Empty;
        if (string.IsNullOrWhiteSpace(root))
            StatusText.Text = "Choose your EuroScope data folder in App settings before cleaning up or restoring GNG files.";
        else if (_storageNotice is not null)
            StatusText.Text = _storageNotice;
        else if (!string.IsNullOrWhiteSpace(newZip))
            StatusText.Text = string.IsNullOrWhiteSpace(oldZip)
                ? "The new complete package reference is ready. Choose the old complete ZIP to compare, or close this window and keep the old files."
                : "The complete packages from your managed installations are ready to compare. Review the files before selecting anything to move.";
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
        ArchiveButton.IsEnabled = SelectButton.IsEnabled = ClearButton.IsEnabled = false;
        SummaryText.Text = "Nothing selected. Compare both complete ZIPs to make a fresh preview.";
    }

    private void BrowseZip_Click(object sender, RoutedEventArgs e)
    {
        bool old = (sender as FrameworkElement)?.Tag as string == "Old";
        var dialog = new OpenFileDialog
        {
            Title = old ? "Select the old complete GNG package" : "Select the new complete GNG package",
            Filter = "GNG ZIP packages (*.zip)|*.zip", CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
            (old ? OldZipBox : NewZipBox).Text = dialog.FileName;
    }

    private async void Compare_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(RootBox.Text))
        {
            StatusText.Text = "Choose your EuroScope data folder in App settings before comparing GNG packages.";
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
            foreach (var candidate in _plan.Candidates) _rows.Add(new CleanupRow(candidate, UpdateSelection));
            var guidance = _plan.Warnings.Count > 0
                ? string.Join(Environment.NewLine, _plan.Warnings)
                : "Nothing is selected yet. Review the reasons beside each file, then select the files you want to move. They will be checked again before anything moves.";
            StatusText.Text = _storageNotice is null ? guidance : _storageNotice + Environment.NewLine + guidance;
            StatusText.ScrollToHome();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { SetBusy(false); }
    }

    private void UpdateSelection()
    {
        int eligible = _rows.Count(r => r.IsEligible);
        int selected = _rows.Count(r => r.IsSelected);
        ArchiveButton.IsEnabled = !_isBusy && selected > 0;
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
        if (_plan is null) return;
        var selected = _rows.Where(r => r.IsSelected && r.IsEligible).Select(r => r.RelativePath).ToArray();
        if (selected.Length == 0) return;
        if (MessageBox.Show(this,
            $"Move {selected.Length} selected files out of:\n{_plan.Root}\n\nThey will be kept in a dated folder under:\n{BackupRoot}\n\nThe files remain in the backup and still use disk space. Use Restore a backup to put them back. Close EuroScope first. Files changed since this review will be kept.\n\nMove the selected files?",
            "Move files to backup", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        var plan = _plan;
        SetBusy(true);
        StatusText.Text = "Rechecking files and moving the selected files to backup…";
        try
        {
            AttemptedChanges = true;
            var result = await Task.Run(() => GngCleanupService.ArchiveSelected(plan, selected));
            InvalidatePlan();
            ShowResult(result, restoring: false);
        }
        catch (Exception ex) { InvalidatePlan(); StatusText.Text = ex.Message; }
        finally { SetBusy(false); }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(RootBox.Text))
        {
            StatusText.Text = "Choose the original EuroScope data folder in App settings before restoring a backup.";
            return;
        }
        var dialog = new OpenFolderDialog { Title = "Select a dated Launchpad cleanup backup" };
        if (Directory.Exists(BackupRoot)) dialog.InitialDirectory = BackupRoot;
        if (dialog.ShowDialog(this) != true) return;
        if (MessageBox.Show(this,
            $"Restore files from:\n{dialog.FolderName}\n\nDestination:\n{RootBox.Text}\n\nOnly backups made for this data folder can be restored. Existing files will not be overwritten. Close EuroScope first.\n\nRestore this backup?",
            "Restore GNG backup", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var folder = dialog.FolderName;
        var root = RootBox.Text;
        SetBusy(true);
        StatusText.Text = "Restoring backup files…";
        try
        {
            AttemptedChanges = true;
            var result = await Task.Run(() => GngCleanupService.Restore(folder, root, _protectedPaths));
            InvalidatePlan();
            ShowResult(result, restoring: true);
        }
        catch (Exception ex) { InvalidatePlan(); StatusText.Text = ex.Message; }
        finally { SetBusy(false); }
    }

    private void ShowResult(GngCleanupResult result, bool restoring)
    {
        var verb = restoring ? "Restored" : "Moved";
        StatusText.Text = $"{verb} {result.Files.Count} files. {result.Skipped.Count} skipped.";
        if (!string.IsNullOrWhiteSpace(result.BackupFolder)) StatusText.Text += $"\nBackup: {result.BackupFolder}";
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
    }

    private void Window_Closing(object? sender, CancelEventArgs e) => e.Cancel = _isBusy;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

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
