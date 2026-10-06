using System.IO;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class SettingsWindow : Window
{
    public AppSettings Settings { get; private set; }
    private bool _discovering, _ready, _saving, _closed;
    private int _validationGeneration;
    private readonly AppSettings _original;
    private readonly ConfiguredPathValidationService _pathValidator = new();
    private readonly DispatcherTimer _validationTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };

    private static readonly Dictionary<string, string> StandardPaths = new()
    {
        ["EuroScope"]    = @"C:\Program Files (x86)\EuroScope\EuroScope.exe",
        ["EuroscopeData"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EuroScope"),
        ["TrackAudio"]   = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\trackaudio\trackaudio.exe"),
        ["VACS"]         = @"C:\Program Files\vacs\vacs-client.exe",
        ["vATIS"]        = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"org.vatsim.vatis\current\vATIS.exe"),
        ["VatEFS"]       = @"C:\Program Files\VatEFS",
    };

    public SettingsWindow(AppSettings current, bool discoverOnLoad = false, bool showApplicationPaths = false)
    {
        _original = current.Copy();
        InitializeComponent();
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);

        // Work on a copy so Cancel truly discards changes
        Settings = current.Copy();

        CheckOnStartup.IsChecked  = Settings.CheckOnStartup;
        EuroscopeExePath.Text = Settings.EuroscopeExePath;
        EuroscopePath.Text    = Settings.EuroscopeDataPath;
        TrackAudioPath.Text   = Settings.TrackAudioExePath;
        VacsPath.Text         = Settings.VacsExePath;
        VatisPath.Text        = Settings.VatisExePath;
        VatEfsPath.Text       = Settings.VatEfsPath;
        ApplicationPaths.IsExpanded = showApplicationPaths;
        _validationTimer.Tick += async (_, _) => { _validationTimer.Stop(); await ValidateFieldsAsync(); };
        Closed += (_, _) => { _closed = true; _validationGeneration++; _validationTimer.Stop(); };
        _ready = true;
        Loaded += async (_, _) => await ValidateFieldsAsync();
        if (discoverOnLoad) Loaded += (_, _) => FindInstallations_Click(this, new RoutedEventArgs());
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)?.Tag?.ToString();
        var (title, target) = tag switch
        {
            "VatEFS" => ("Choose the folder containing VatEFS.dll", VatEfsPath),
            _ => ("Choose the EuroScope folder containing the Swedish GNG files", EuroscopePath),
        };

        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (Directory.Exists(target.Text))
            dialog.InitialDirectory = target.Text;

        if (dialog.ShowDialog() == true)
            target.Text = dialog.FolderName;
    }

    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var appName = (sender as FrameworkElement)?.Tag?.ToString() ?? "Application";
        var dialog  = new OpenFileDialog
        {
            Title            = $"Choose the installed {appName} program (not its installer)",
            Filter           = "Program files (*.exe)|*.exe",
            CheckFileExists  = true,
        };

        // Pre-navigate to the currently set directory if any
        var current = appName switch
        {
            "EuroScope"  => EuroscopeExePath.Text,
            "TrackAudio" => TrackAudioPath.Text,
            "VACS"       => VacsPath.Text,
            "vATIS"      => VatisPath.Text,
            _            => string.Empty,
        };
        try
        {
            var directory = Path.GetDirectoryName(current);
            if (Directory.Exists(directory)) dialog.InitialDirectory = directory;
        }
        catch (ArgumentException) { }

        if (dialog.ShowDialog() != true) return;

        switch (appName)
        {
            case "EuroScope":  EuroscopeExePath.Text = dialog.FileName; break;
            case "TrackAudio": TrackAudioPath.Text   = dialog.FileName; break;
            case "VACS":       VacsPath.Text         = dialog.FileName; break;
            case "vATIS":      VatisPath.Text        = dialog.FileName; break;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_discovering || _saving) return;
        _saving = true;
        _validationTimer.Stop();
        SetBusyState();
        FrameworkElement? focusAfterCheck = null;
        try
        {
            var draft = ReadFields();
            var results = await ValidateFieldsAsync(draft);
            if (results == null || _closed) return;
            var changedIssues = results.Where(r => ConfiguredPathValidationService.NeedsAttention(r, _original)).ToArray();
            var invalid = changedIssues.FirstOrDefault(r => r.Status == PathValidationStatus.Invalid);
            if (invalid != null)
            {
                ValidationSummary.Text = "A new selection needs fixing. Choose the correct file or folder, or clear it to skip that application.";
                ApplicationPaths.IsExpanded = true;
                focusAfterCheck = BoxFor(invalid.Kind);
                return;
            }
            if (changedIssues.Any(r => r.Status == PathValidationStatus.Warning) && KeepUnverified.IsChecked != true)
            {
                ValidationSummary.Text = "Some new locations could not be recognised. Check the explanations, then choose another location or confirm keeping them.";
                ApplicationPaths.IsExpanded = true;
                focusAfterCheck = KeepUnverified;
                return;
            }
            Settings = draft;
            DialogResult = true;
        }
        finally
        {
            _saving = false;
            if (!_closed)
            {
                SetBusyState();
                focusAfterCheck?.BringIntoView();
                focusAfterCheck?.Focus();
            }
        }
    }

    private AppSettings ReadFields()
    {
        var draft = Settings.Copy();
        draft.CheckOnStartup = CheckOnStartup.IsChecked == true;
        draft.EuroscopeExePath = EuroscopeExePath.Text.Trim();
        draft.EuroscopeDataPath = EuroscopePath.Text.Trim();
        draft.TrackAudioExePath = TrackAudioPath.Text.Trim();
        draft.VacsExePath = VacsPath.Text.Trim();
        draft.VatisExePath = VatisPath.Text.Trim();
        draft.VatEfsPath = VatEfsPath.Text.Trim();
        return draft;
    }

    private void Path_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _closed) return;
        _validationGeneration++;
        KeepUnverified.IsChecked = false;
        KeepUnverified.Visibility = Visibility.Collapsed;
        ValidationSummary.Text = "";
        foreach (var kind in Enum.GetValues<ConfiguredPathKind>())
            if (ReferenceEquals(sender, BoxFor(kind))) StatusFor(kind).Text = "Checking this location…";
        _validationTimer.Stop();
        _validationTimer.Start();
    }

    private async Task<IReadOnlyList<ConfiguredPathValidation>?> ValidateFieldsAsync(AppSettings? draft = null)
    {
        if (_closed) return null;
        draft ??= ReadFields();
        int generation = ++_validationGeneration;
        try
        {
            var results = await Task.Run(() => _pathValidator.Validate(draft));
            if (_closed || generation != _validationGeneration) return null;
            foreach (var result in results)
            {
                var status = StatusFor(result.Kind);
                status.Text = result.Message;
                status.SetResourceReference(TextBlock.ForegroundProperty,
                    result.Status is PathValidationStatus.Invalid or PathValidationStatus.Warning ? "WarningFg" : "SecondaryText");
            }
            bool warnings = results.Any(r => r.Status == PathValidationStatus.Warning &&
                ConfiguredPathValidationService.NeedsAttention(r, _original));
            KeepUnverified.Visibility = warnings ? Visibility.Visible : Visibility.Collapsed;
            if (!warnings) KeepUnverified.IsChecked = false;
            return results;
        }
        catch
        {
            if (!_closed && generation == _validationGeneration)
                ValidationSummary.Text = "The locations could not be checked. Try again before saving.";
            return null;
        }
    }

    private TextBox BoxFor(ConfiguredPathKind kind) => kind switch
    {
        ConfiguredPathKind.EuroScopeExecutable => EuroscopeExePath,
        ConfiguredPathKind.EuroScopeDataFolder => EuroscopePath,
        ConfiguredPathKind.TrackAudioExecutable => TrackAudioPath,
        ConfiguredPathKind.VacsExecutable => VacsPath,
        ConfiguredPathKind.VatisExecutable => VatisPath,
        ConfiguredPathKind.VatEfsFolder => VatEfsPath,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private TextBlock StatusFor(ConfiguredPathKind kind) => kind switch
    {
        ConfiguredPathKind.EuroScopeExecutable => EuroscopeExeStatus,
        ConfiguredPathKind.EuroScopeDataFolder => EuroscopeDataStatus,
        ConfiguredPathKind.TrackAudioExecutable => TrackAudioStatus,
        ConfiguredPathKind.VacsExecutable => VacsStatus,
        ConfiguredPathKind.VatisExecutable => VatisStatus,
        ConfiguredPathKind.VatEfsFolder => VatEfsStatus,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private void SetBusyState()
    {
        FindInstallationsButton.IsEnabled = SaveButton.IsEnabled = !_saving && !_discovering;
        ApplicationPaths.IsEnabled = CheckOnStartup.IsEnabled = !_saving;
    }

    private void InsertDefault_Click(object sender, RoutedEventArgs e)
    {
        var key = (sender as FrameworkElement)?.Tag?.ToString() ?? "";
        if (!StandardPaths.TryGetValue(key, out var path)) return;
        TextBoxFor(key).Text = path;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        var key = (sender as FrameworkElement)?.Tag?.ToString() ?? "";
        TextBoxFor(key).Text = string.Empty;
    }

    private System.Windows.Controls.TextBox TextBoxFor(string key) => key switch
    {
        "EuroScope"    => EuroscopeExePath,
        "EuroscopeData" => EuroscopePath,
        "TrackAudio"   => TrackAudioPath,
        "VACS"         => VacsPath,
        "vATIS"        => VatisPath,
        "VatEFS"       => VatEfsPath,
        _              => throw new ArgumentOutOfRangeException(nameof(key), key, null),
    };

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private async void FindInstallations_Click(object sender, RoutedEventArgs e)
    {
        if (_discovering || _saving) return;
        _discovering = true;
        SetBusyState();
        DiscoveryStatus.Text = "Looking for installed EuroScope, TrackAudio, VACS and vATIS programs, plus Swedish GNG and VatEFS folders…";
        try
        {
            var candidates = await Task.Run(() => new InstallationDiscoveryService().Discover());
            if (!IsVisible) return;
            var review = new InstallationDiscoveryWindow(candidates) { Owner = this };
            if (review.ShowDialog() != true) { DiscoveryStatus.Text = "No paths changed."; return; }
            int imported = 0, retained = 0;
            foreach (var candidate in review.SelectedCandidates)
            {
                var (box, value) = candidate.Id switch
                {
                    AtcRemovalApp.EuroScope => (EuroscopeExePath, candidate.ExecutablePath),
                    AtcRemovalApp.Gng => (EuroscopePath, candidate.DataPath ?? ""),
                    AtcRemovalApp.Vacs => (VacsPath, candidate.ExecutablePath),
                    AtcRemovalApp.Vatis => (VatisPath, candidate.ExecutablePath),
                    AtcRemovalApp.TrackAudio => (TrackAudioPath, candidate.ExecutablePath),
                    AtcRemovalApp.VatEfs => (VatEfsPath, candidate.DataPath ?? ""),
                    _ => (null, "")
                };
                if (box == null || string.IsNullOrWhiteSpace(value)) continue;
                if (!review.ReplaceExisting && !string.IsNullOrWhiteSpace(box.Text)) { retained++; continue; }
                box.Text = value;
                imported++;
            }
            DiscoveryStatus.Text = $"Locations added: {imported}. Existing choices kept: {retained}. Check the results below, then Save to use these locations in Launchpad.";
            ApplicationPaths.IsExpanded = true;
            ApplicationPaths.BringIntoView();
        }
        catch { if (!_closed) DiscoveryStatus.Text = "The search could not finish. Try again, or choose the program files and folders below."; }
        finally { _discovering = false; if (!_closed) SetBusyState(); }
    }
}
