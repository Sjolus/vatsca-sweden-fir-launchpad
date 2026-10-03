using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
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
    private readonly LaunchpadUpdateService _launchpadUpdates;
    private readonly CancellationTokenSource _windowLifetime = new();
    private const string LaunchpadDownloads = "https://github.com/Sjolus/vatsca-sweden-fir-launchpad/releases/latest";
    private AppSettings _settings;
    private bool _isDarkMode;
    private bool _isChecking;
    private bool _selfUpdateActionBusy;
    private bool _windowClosed;
    private int _openDialogCount;

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
            new() { AppName = "EuroScope (GNG Pack)", IsFolder = true, HasFontsCheck = true },
            new() { AppName = "TrackAudio" },
            new() { AppName = "VACS" },
            new() { AppName = "vATIS" },
            new() { AppName = "VatEFS", IsWebApp = true, IsLocalUrl = true, LaunchPath = "http://localhost:17770" },
            new() { AppName = "VATIRIS", IsWebApp = true, LaunchPath = "https://vatiris.se", Status = CheckStatus.WebApp, InstalledVersion = "N/A", LatestVersion = "N/A" },
            new() { AppName = "Sweden FIR Launchpad", HasSelfUpdate = true },
        };

        _launchpadUpdates = new LaunchpadUpdateService();
        _launchpadUpdates.StateChanged += LaunchpadUpdate_StateChanged;
        ApplySelfUpdateState(_launchpadUpdates.State);

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
        // Snapshot the OS TCP listener table once per tick. Used to gate Launch buttons for rows
        // whose LaunchPath points at a local server (e.g. VatEFS on localhost:17770) without
        // throwing first-chance exceptions every tick like an HTTP / TCP-connect probe would.
        IPEndPoint[]? listeners = null;
        if (_results.Any(r => r.IsLocalUrl))
        {
            try { listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(); }
            catch { listeners = Array.Empty<IPEndPoint>(); }
        }

        foreach (var result in _results)
        {
            if (result.IsLocalUrl && !string.IsNullOrEmpty(result.LaunchPath))
                UpdateLocalUrlReachability(result, listeners!);

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

    private static void UpdateLocalUrlReachability(CheckResult result, IPEndPoint[] listeners)
    {
        bool reachable = false;
        if (Uri.TryCreate(result.LaunchPath, UriKind.Absolute, out var uri))
        {
            // Match by port and accept any loopback or wildcard binding — covers servers bound to
            // 127.0.0.1, ::1, 0.0.0.0, or [::] (all reachable from a localhost client).
            foreach (var ep in listeners)
            {
                if (ep.Port != uri.Port) continue;
                if (IPAddress.IsLoopback(ep.Address) ||
                    ep.Address.Equals(IPAddress.Any) ||
                    ep.Address.Equals(IPAddress.IPv6Any))
                {
                    reachable = true;
                    break;
                }
            }
        }
        if (result.IsLocalUrlReachable != reachable)
            result.IsLocalUrlReachable = reachable;
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
        if (_isChecking || _windowClosed) return;
        _isChecking = true;
        UpdateSelfUpdateActionEnabled();
        CheckButton.IsEnabled = false;
        CheckButton.Content   = "Checking...";
        try
        {
            _settings = SettingsService.Load();

            foreach (var r in _results)
                if (!r.IsWebApp && !r.HasSelfUpdate) r.Status = CheckStatus.Checking;

            await Task.WhenAll(
                UpdateChecker.CheckEuroscope(_results[0], _settings.EuroscopeExePath),
                UpdateChecker.CheckGng(_results[1], _settings.EuroscopeDataPath),
                UpdateChecker.CheckGitHub(_results[2], _settings.TrackAudioExePath, "pierr3/TrackAudio", skipPreRelease: true),
                UpdateChecker.CheckGitHub(_results[3], _settings.VacsExePath,       "vacs-project/vacs"),
                UpdateChecker.CheckGitHub(_results[4], _settings.VatisExePath,      "vatis-project/vatis"),
                UpdateChecker.CheckVatEfs(_results[5], _settings.VatEfsPath),
                CheckLaunchpadAsync()
            );
            if (_windowClosed) return;

            // Fonts check piggy-backs on the GNG Pack row — sync filesystem-only probe.
            var fonts = FontService.Check(_settings.EuroscopeDataPath);
            _results[1].FontsState   = fonts.State;
            _results[1].FontsTooltip = fonts.Tooltip;
            Logger.Log("CHECK", $"Fonts: {fonts.State} ({string.Join("; ", fonts.Entries.Select(e => $"{e.FileName} installed={(e.InstalledVersion?.ToString("0.00") ?? (e.InstalledPath is null ? "missing" : "?"))} source={(e.SourceVersion?.ToString("0.00") ?? "?")} {(e.IsUpToDate ? "ok" : "needs-action")}"))})");

            LastCheckedText.Text = $"Last checked: {DateTime.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException) when (_windowClosed) { }
        catch (Exception ex)
        {
            Logger.Log("CHECK", $"Update check stopped: {ex.Message}");
            if (!_windowClosed) LastCheckedText.Text = "Update check stopped. Try again.";
        }
        finally
        {
            _isChecking = false;
            if (!_windowClosed)
            {
                CheckButton.IsEnabled = true;
                CheckButton.Content = "↻  Check for Updates";
                UpdateSelfUpdateActionEnabled();
            }
        }
    }

    private void AppConfig_Click(object sender, RoutedEventArgs e)
    {
        var win = new AppConfigWindow(_settings, _settings.EuroscopeDataPath) { Owner = this };
        if (ShowOwnedDialog(win) == true)
        {
            _settings            = win.Settings;
            _settings.IsDarkMode = _isDarkMode;
            SettingsService.Save(_settings);
            UpdateAppConfigButton();
        }
    }

    private async Task CheckLaunchpadAsync()
    {
        // A normal check only discovers updates. Download and restart are separate user actions.
        if (_selfUpdateActionBusy) return;
        if (_launchpadUpdates.IsInstalled)
        {
            await _launchpadUpdates.CheckAsync(_windowLifetime.Token);
            return;
        }

        var row = _results[7];
        row.SelfUpdateBusy = true;
        row.Status = CheckStatus.Checking;
        row.SelfUpdateProgress = null;
        row.ShowSelfUpdateAction = false;
        row.SelfUpdateSummary = "Checking…";
        await UpdateChecker.CheckGitHub(row, Environment.ProcessPath!, "Sjolus/vatsca-sweden-fir-launchpad");
        if (_windowClosed) return;
        row.DownloadUrl = LaunchpadDownloads;
        row.StatusMessage = "This portable/development copy uses manual updates. Open downloads to get the Launchpad installer. " + row.StatusMessage;
        row.SelfUpdateSummary = row.StatusText;
        row.SelfUpdateActionText = "Open downloads ↗";
        row.ShowSelfUpdateAction = true;
        row.SelfUpdateBusy = false;
        UpdateSelfUpdateActionEnabled();
    }

    private void LaunchpadUpdate_StateChanged(object? sender, LaunchpadUpdateState state)
    {
        if (_windowClosed) return;
        if (Dispatcher.CheckAccess()) ApplySelfUpdateState(state);
        else Dispatcher.InvokeAsync(() =>
        {
            if (!_windowClosed) ApplySelfUpdateState(state);
        });
    }

    private void ApplySelfUpdateState(LaunchpadUpdateState state)
    {
        var row = _results[7];
        row.InstalledVersion = FormatLaunchpadVersion(state.CurrentVersion);
        row.LatestVersion = FormatLaunchpadVersion(state.AvailableVersion);
        row.DownloadUrl = LaunchpadDownloads;
        row.StatusMessage = state.Message;
        row.SelfUpdateProgress = state.ProgressPercent;
        row.SelfUpdateNotice = state.Status == LaunchpadUpdateStatus.NoFeed ? "Installer updates not published yet" : string.Empty;
        row.SelfUpdateBusy = state.Status is LaunchpadUpdateStatus.Checking or LaunchpadUpdateStatus.Downloading;
        row.Status = state.Status switch
        {
            LaunchpadUpdateStatus.UpToDate => CheckStatus.UpToDate,
            LaunchpadUpdateStatus.Available or LaunchpadUpdateStatus.Ready => CheckStatus.UpdateAvailable,
            LaunchpadUpdateStatus.Checking or LaunchpadUpdateStatus.Downloading => CheckStatus.Checking,
            LaunchpadUpdateStatus.Error => CheckStatus.Error,
            _ => CheckStatus.Unknown
        };
        row.SelfUpdateSummary = state.Status switch
        {
            LaunchpadUpdateStatus.Checking => "Checking…",
            LaunchpadUpdateStatus.Downloading => state.ProgressPercent.HasValue ? $"Downloading {state.ProgressPercent}%" : "Downloading…",
            LaunchpadUpdateStatus.UpToDate => "Up to date",
            LaunchpadUpdateStatus.Available => "Update available",
            LaunchpadUpdateStatus.Ready => "Ready to restart",
            LaunchpadUpdateStatus.NoFeed => "Updates not published yet",
            LaunchpadUpdateStatus.Unsupported => "Manual updates",
            LaunchpadUpdateStatus.Error => "Update check failed",
            _ => "Not checked"
        };
        row.SelfUpdateActionText = state.Status switch
        {
            LaunchpadUpdateStatus.Available => "Download update",
            LaunchpadUpdateStatus.Ready => "Restart to update",
            LaunchpadUpdateStatus.Unsupported or LaunchpadUpdateStatus.NoFeed => "Open downloads ↗",
            LaunchpadUpdateStatus.Error => "Retry update check",
            _ => "Check for updates"
        };
        row.ShowSelfUpdateAction = state.Status is LaunchpadUpdateStatus.Idle or LaunchpadUpdateStatus.Available or
            LaunchpadUpdateStatus.Ready or LaunchpadUpdateStatus.Unsupported or LaunchpadUpdateStatus.NoFeed or LaunchpadUpdateStatus.Error;
        UpdateSelfUpdateActionEnabled();
    }

    private static string FormatLaunchpadVersion(string? version) =>
        string.IsNullOrWhiteSpace(version) ? "—" : "v" + version.TrimStart('v', 'V');

    private bool CanRunSelfUpdateAction() =>
        !_windowClosed && !_isChecking && !_selfUpdateActionBusy && _openDialogCount == 0 && IsEnabled &&
        !System.Windows.Interop.ComponentDispatcher.IsThreadModal &&
        OwnedWindows.Count == 0 &&
        !Application.Current.Windows.OfType<Window>().Any(window => window != this && window.IsVisible) &&
        _launchpadUpdates.State.Status is not (LaunchpadUpdateStatus.Checking or LaunchpadUpdateStatus.Downloading);

    private void UpdateSelfUpdateActionEnabled() => _results[7].SelfUpdateActionEnabled = CanRunSelfUpdateAction();

    private async void SelfUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!CanRunSelfUpdateAction())
        {
            _results[7].StatusMessage = "Finish the current operation and close Launchpad's other windows before updating Launchpad.";
            return;
        }

        var status = _launchpadUpdates.State.Status;
        if (!_launchpadUpdates.IsInstalled || status == LaunchpadUpdateStatus.NoFeed)
        {
            try { Process.Start(new ProcessStartInfo(LaunchpadDownloads) { UseShellExecute = true }); }
            catch { _results[7].StatusMessage = "The browser could not open. Visit Launchpad's GitHub Releases page for the installer."; }
            return;
        }

        if (status == LaunchpadUpdateStatus.Ready)
        {
            // Recheck here, immediately before the only path that can stop this process.
            // Owned and native modal dialogs must finish before the process can restart.
            if (!CanRunSelfUpdateAction()) return;
            _selfUpdateActionBusy = true;
            UpdateSelfUpdateActionEnabled();
            try { _launchpadUpdates.RestartToApply(); }
            catch (Exception ex)
            {
                _results[7].StatusMessage = ex.Message;
                _results[7].SelfUpdateSummary = "Restart failed";
            }
            finally
            {
                _selfUpdateActionBusy = false;
                UpdateSelfUpdateActionEnabled();
            }
            return;
        }

        _selfUpdateActionBusy = true;
        UpdateSelfUpdateActionEnabled();
        try
        {
            if (status == LaunchpadUpdateStatus.Available)
                await _launchpadUpdates.DownloadAsync(_windowLifetime.Token);
            else
                await _launchpadUpdates.CheckAsync(_windowLifetime.Token);
        }
        catch (OperationCanceledException) when (_windowClosed) { }
        catch (Exception ex)
        {
            if (!_windowClosed) _results[7].StatusMessage = ex.Message;
        }
        finally
        {
            _selfUpdateActionBusy = false;
            if (!_windowClosed) UpdateSelfUpdateActionEnabled();
        }
    }

    private bool? ShowOwnedDialog(Window window)
    {
        _openDialogCount++;
        UpdateSelfUpdateActionEnabled();
        try
        {
            window.Owner = this;
            return window.ShowDialog();
        }
        finally
        {
            _openDialogCount--;
            if (!_windowClosed) UpdateSelfUpdateActionEnabled();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _windowClosed = true;
        _processTimer.Stop();
        _launchpadUpdates.StateChanged -= LaunchpadUpdate_StateChanged;
        _windowLifetime.Cancel();
        _windowLifetime.Dispose();
        base.OnClosed(e);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(_settings) { Owner = this };
        if (ShowOwnedDialog(win) == true)
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
                    var profileDir = Path.Combine(AppDataDir, $"{result.AppName}Profile");
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

    private void Fonts_Click(object sender, RoutedEventArgs e)
    {
        var result = FontService.Check(_settings.EuroscopeDataPath);
        switch (result.State)
        {
            case FontsState.Unknown:
                MessageBox.Show(
                    "Set the EuroScope data folder in Settings to enable font checking.",
                    "Fonts", MessageBoxButton.OK, MessageBoxImage.Information);
                break;
            case FontsState.AllOk:
                MessageBox.Show(
                    "All required fonts are installed and match the GNG Pack version.",
                    "Fonts", MessageBoxButton.OK, MessageBoxImage.Information);
                break;
            case FontsState.NeedsAction:
                FontService.InstallMissing(result);
                break;
        }

        // Re-probe so the badge updates immediately after the click. The user may not
        // have clicked Install in fontview yet; that's fine — the next Check button
        // press (or app restart) re-probes and reflects the actual state.
        var fresh = FontService.Check(_settings.EuroscopeDataPath);
        _results[1].FontsState   = fresh.State;
        _results[1].FontsTooltip = fresh.Tooltip;
    }
}
