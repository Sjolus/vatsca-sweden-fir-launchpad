using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class MainWindow : Window
{
    private static readonly string AppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VatscaUpdateChecker");

    private readonly ObservableCollection<CheckResult> _results;
    private readonly DispatcherTimer _processTimer;
    private AppSettings _settings;
    private bool _isDarkMode;

    public MainWindow()
    {
        InitializeComponent();

        _settings   = SettingsService.Load();
        _isDarkMode = _settings.IsDarkMode;

        if (_isDarkMode)
        {
            App.SetTheme(true);
            ThemeToggleButton.Content = "☀";
        }

        _results = new ObservableCollection<CheckResult>
        {
            new() { AppName = "EuroScope" },
            new() { AppName = "EuroScope (GNG Pack)", IsFolder = true },
            new() { AppName = "TrackAudio" },
            new() { AppName = "VACS" },
            new() { AppName = "vATIS" },
            new() { AppName = "VATIRIS", IsWebApp = true, LaunchPath = "https://vatiris.se", Status = CheckStatus.WebApp, InstalledVersion = "N/A", LatestVersion = "N/A" },
            new() { AppName = "Sweden FIR Launchpad" },
        };

        ApplyLaunchPaths();
        RefreshEuroscopeProfiles();
        AppList.ItemsSource = _results;

        _processTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _processTimer.Tick += (_, _) => UpdateRunningStates();
        _processTimer.Start();
    }

    private void RefreshEuroscopeProfiles()
    {
        var es       = _results[0];
        var previous = es.SelectedProfile?.FilePath;

        es.Profiles.Clear();
        es.Profiles.Add(new(DisplayName: "— No profile —", FilePath: null));

        if (!string.IsNullOrWhiteSpace(_settings.EuroscopeDataPath) &&
            Directory.Exists(_settings.EuroscopeDataPath))
        {
            foreach (var prf in Directory.GetFiles(_settings.EuroscopeDataPath, "ES*.prf").OrderBy(f => f))
                es.Profiles.Add(new(Path.GetFileNameWithoutExtension(prf), prf));
        }

        // Restore previous selection: prefer last saved profile path, then previous selection, else first
        var savedPath = _settings.LastEuroscopeProfile;
        es.SelectedProfile = es.Profiles.FirstOrDefault(p => p.FilePath == savedPath)
                             ?? es.Profiles.FirstOrDefault(p => p.FilePath == previous)
                             ?? es.Profiles[0];

    }

    private void ApplyLaunchPaths()
    {
        _results[0].LaunchPath = _settings.EuroscopeExePath;
        _results[1].LaunchPath = _settings.EuroscopeDataPath;
        _results[2].LaunchPath = _settings.TrackAudioExePath;
        _results[3].LaunchPath = _settings.VacsExePath;
        _results[4].LaunchPath = _settings.VatisExePath;
    }

    private void UpdateRunningStates()
    {
        foreach (var result in _results)
        {
            if (result.IsWebApp)
            {
                result.IsRunning = IsWebAppRunning(result.AppName);
                continue;
            }
            if (result.IsFolder || string.IsNullOrEmpty(result.LaunchPath)) continue;
            var exeName = Path.GetFileNameWithoutExtension(result.LaunchPath);
            result.IsRunning = Process.GetProcessesByName(exeName).Length > 0;
        }
    }

    // Returns true if the pid file for this web app exists and the tracked process is still alive.
    // If the tracked PID has exited (e.g. Edge relaunched itself during first-run profile setup),
    // falls back to scanning for the actual browser process spawned around the same time.
    private bool IsWebAppRunning(string appName)
    {
        var pidFile = Path.Combine(AppDataDir, $"{appName}.pid");
        if (!File.Exists(pidFile)) return false;

        var lines = File.ReadAllLines(pidFile);
        if (lines.Length == 0) return false;
        if (!int.TryParse(lines[0].Trim(), out var pid)) return false;
        var launchTime = lines.Length > 1 && DateTime.TryParse(lines[1].Trim(), out var lt) ? lt : DateTime.MinValue;

        // Fast path: tracked PID is still alive.
        try
        {
            var proc = Process.GetProcessById(pid);
            if (!proc.HasExited && proc.ProcessName == "msedge") return true;
        }
        catch { }

        // Tracked PID is gone — Edge may have relaunched itself (e.g. first-run profile setup).
        // Find the new browser process: an msedge that started after our launch whose parent is
        // not itself msedge (renderers/GPU processes are children of the browser, not vice versa).
        var browserId = ProcessHelper.FindEdgeBrowserProcess(launchTime);
        if (browserId > 0)
        {
            File.WriteAllLines(pidFile, new[] { browserId.ToString(), launchTime.ToString("O") });
            return true;
        }

        try { File.Delete(pidFile); } catch { }
        return false;
    }

    private void KillWebApp(CheckResult result)
    {
        var pidFile = Path.Combine(AppDataDir, $"{result.AppName}.pid");
        var lines   = File.Exists(pidFile) ? File.ReadAllLines(pidFile) : Array.Empty<string>();
        if (lines.Length > 0 && int.TryParse(lines[0].Trim(), out var pid))
        {
            try
            {
                var proc = Process.GetProcessById(pid);
                Logger.Log("KILL", $"{result.AppName}: killing pid {pid}");
                proc.Kill(entireProcessTree: true);
            }
            catch { /* already exited */ }
            try { File.Delete(pidFile); } catch { }
        }
        result.IsRunning = false;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateRunningStates();
        UpdateAppConfigButton();
        if (_settings.CheckOnStartup)
            await RunChecks();
    }

    private void UpdateAppConfigButton()
    {
        bool needsAttention = !ProfileService.IsConfigured(_settings) ||
            !ProfileService.IsInSync(_settings, _settings.EuroscopeDataPath);

        AppConfigButton.Background = needsAttention
            ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ff9800"))
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1a475f"));
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        _isDarkMode = !_isDarkMode;
        App.SetTheme(_isDarkMode);
        ThemeToggleButton.Content = _isDarkMode ? "☀" : "☽";

        // Force row backgrounds to re-evaluate via the converter
        AppList.ItemsSource = null;
        AppList.ItemsSource = _results;

        _settings.IsDarkMode = _isDarkMode;
        SettingsService.Save(_settings);
    }

    private async void Check_Click(object sender, RoutedEventArgs e) => await RunChecks();

    private async Task RunChecks()
    {
        CheckButton.IsEnabled = false;
        CheckButton.Content   = "Checking...";
        _settings = SettingsService.Load();

        foreach (var r in _results)
            if (!r.IsWebApp) r.Status = CheckStatus.Checking;

        await Task.WhenAll(
            UpdateChecker.CheckEuroscope(_results[0], _settings.EuroscopeExePath),
            UpdateChecker.CheckGng(_results[1], _settings.EuroscopeDataPath),
            UpdateChecker.CheckGitHub(_results[2], _settings.TrackAudioExePath, "pierr3/TrackAudio", skipPreRelease: true),
            UpdateChecker.CheckGitHub(_results[3], _settings.VacsExePath,       "vacs-project/vacs"),
            UpdateChecker.CheckGitHub(_results[4], _settings.VatisExePath,      "vatis-project/vatis"),
            UpdateChecker.CheckGitHub(_results[6], Environment.ProcessPath!,    "Sjolus/vatsca-sweden-fir-launchpad")
        );

        LastCheckedText.Text    = $"Last checked: {DateTime.Now:HH:mm:ss}";
        CheckButton.IsEnabled   = true;
        CheckButton.Content     = "↻  Check for Updates";
    }

    private void AppConfig_Click(object sender, RoutedEventArgs e)
    {
        var win = new AppConfigWindow(_settings, _settings.EuroscopeDataPath) { Owner = this };
        if (win.ShowDialog() == true)
        {
            _settings            = win.Settings;
            _settings.IsDarkMode = _isDarkMode;
            SettingsService.Save(_settings);
            UpdateAppConfigButton();
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(_settings) { Owner = this };
        if (win.ShowDialog() == true)
        {
            _settings            = win.Settings;
            _settings.IsDarkMode = _isDarkMode;
            SettingsService.Save(_settings);
            ApplyLaunchPaths();
            RefreshEuroscopeProfiles();
            UpdateRunningStates();
            UpdateAppConfigButton();
        }
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CheckResult result }) return;

        try
        {
            if (result.IsFolder)
            {
                Process.Start("explorer.exe", result.LaunchPath);
                Logger.Log("LAUNCH", $"{result.AppName}: opened folder {result.LaunchPath}");
            }
            else if (result.IsWebApp)
            {
                if (result.IsRunning)
                {
                    KillWebApp(result);
                }
                else
                {
                    const string edgePath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
                    var profileDir = Path.Combine(AppDataDir, "VATIRISProfile");
                    var proc = Process.Start(new ProcessStartInfo
                    {
                        FileName        = edgePath,
                        Arguments       = $"--app={result.LaunchPath} --user-data-dir=\"{profileDir}\"",
                        UseShellExecute = false,
                    });
                    if (proc != null)
                    {
                        File.WriteAllLines(Path.Combine(AppDataDir, $"{result.AppName}.pid"),
                            new[] { proc.Id.ToString(), proc.StartTime.ToString("O") });
                        result.IsRunning = true;
                    }
                    Logger.Log("LAUNCH", $"{result.AppName}: launched as web app (pid={proc?.Id}, url={result.LaunchPath})");
                }
            }
            else if (result.IsRunning)
            {
                var exeName = Path.GetFileNameWithoutExtension(result.LaunchPath);
                var procs = Process.GetProcessesByName(exeName);
                Logger.Log("KILL", $"{result.AppName}: killing {procs.Length} process(es) (name={exeName})");
                foreach (var proc in procs)
                {
                    try { proc.Kill(entireProcessTree: true); }
                    catch { /* process may have already exited */ }
                }
                result.IsRunning = false;
            }
            else if (result.SelectedProfile?.FilePath is string prfPath)
            {
                // EuroScope with a specific profile — launch directly with .prf argument
                Process.Start(new ProcessStartInfo
                {
                    FileName        = result.LaunchPath,
                    Arguments       = $"\"{prfPath}\"",
                    UseShellExecute = false,
                });
                Logger.Log("LAUNCH", $"{result.AppName}: launched with profile \"{result.SelectedProfile.DisplayName}\" ({prfPath})");
            }
            else
            {
                // Launch via explorer.exe so the process starts outside our job object context,
                // exactly as a double-click would. Required for Electron apps (TrackAudio) whose
                // Chromium renderer/GPU child processes break under inherited job restrictions.
                Process.Start(new ProcessStartInfo
                {
                    FileName        = "explorer.exe",
                    Arguments       = $"\"{result.LaunchPath}\"",
                    UseShellExecute = false,
                });
                Logger.Log("LAUNCH", $"{result.AppName}: launched via explorer.exe ({result.LaunchPath})");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not launch {result.AppName}:\n\n{ex.Message}",
                "Launch failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ProfileDropdown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CheckResult result }) return;

        var menu = new System.Windows.Controls.ContextMenu();
        bool separatorAdded = false;
        foreach (var profile in result.Profiles)
        {
            if (!separatorAdded && profile.FilePath != null)
            {
                menu.Items.Add(new System.Windows.Controls.Separator());
                separatorAdded = true;
            }

            bool isSelected = result.SelectedProfile == profile;
            var item = new System.Windows.Controls.MenuItem
            {
                Header = (isSelected ? "✓  " : "    ") + profile.DisplayName
            };
            var captured = profile;
            item.Click += (_, _) =>
            {
                result.SelectedProfile = captured;
                _settings.LastEuroscopeProfile = captured.FilePath ?? string.Empty;
                SettingsService.Save(_settings);
            };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = (System.Windows.UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url } && !string.IsNullOrEmpty(url))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
