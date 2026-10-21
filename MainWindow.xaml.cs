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
    private readonly bool _firstSetup;
    private bool _setupRestartRequired;
    private CheckResult LaunchpadRow => _results.Single(row => row.HasSelfUpdate);
    private CheckResult SaamanRow => _results.Single(row => row.HasSaamanManagement);

    public MainWindow()
    {
        InitializeComponent();
        // Fit the initial work area without permanently capping later resizing or monitor moves.
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);

        _firstSetup = !SettingsService.HasSavedSettings;
        _settings   = SettingsService.Load();
        _isDarkMode = _settings.IsDarkMode;
        ApplyDensity();

        if (_isDarkMode)
        {
            App.SetTheme(true);
            ThemeToggleButton.Content = "☀";
        }

        _results = new ObservableCollection<CheckResult>
        {
            new() { AppName = "EuroScope", HasEuroScopeManagement = true },
            new() { AppName = "EuroScope (GNG Pack)", IsFolder = true, HasFontsCheck = true },
            new() { AppName = "TrackAudio", SoftwareApp = SoftwareApp.TrackAudio },
            new() { AppName = "VACS", SoftwareApp = SoftwareApp.Vacs },
            new() { AppName = "vATIS", SoftwareApp = SoftwareApp.Vatis },
            new() { AppName = "VatEFS", SoftwareApp = SoftwareApp.VatEfs, IsWebApp = true, IsLocalUrl = true, LaunchPath = "http://localhost:17770" },
            new() { AppName = "SAAMAN", HasSaamanManagement = true },
            new() { AppName = "VATIRIS", IsWebApp = true, LaunchPath = "https://vatiris.se", Status = CheckStatus.WebApp, InstalledVersion = "N/A", LatestVersion = "N/A" },
            new() { AppName = "Sweden FIR Launchpad", HasSelfUpdate = true },
        };

        _softwareUpdates = new SoftwareUpdateService(new SoftwareReleaseSource(_softwareHttp), new SoftwareInstaller(), _softwareHttp);
        _softwareUpdates.StateChanged += SoftwareUpdate_StateChanged;
        _softwareUpdates.FailureObserved += ReportSoftwareFailure;
        UpdateChecker.FailureObserved += ReportPublicCheckFailure;
        _launchpadUpdates = new LaunchpadUpdateService();
        _launchpadUpdates.StateChanged += LaunchpadUpdate_StateChanged;
        _launchpadUpdates.FailureObserved += ReportSelfUpdateFailure;
        ApplySelfUpdateState(_launchpadUpdates.State);

        ApplyLaunchPaths();
        RefreshEuroscopeProfiles();
        PresentApplicationRows(_results);

        InitializePresentation();
        InitializeActivityHistory();

        _processTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _processTimer.Tick += (_, _) => UpdateRunningStates();
        _processTimer.Start();
        InitializeWorkspaces();
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
        _results[5].SoftwareExecutablePath = string.IsNullOrWhiteSpace(_settings.VatEfsPath) ? "" : Path.Combine(_settings.VatEfsPath, "efs.exe");
        DiscoveryPrompt.Visibility = new[] { _settings.EuroscopeExePath, _settings.TrackAudioExePath, _settings.VacsExePath, _settings.VatisExePath, _settings.VatEfsPath }
            .All(string.IsNullOrWhiteSpace) ? Visibility.Visible : Visibility.Collapsed;
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

        _results[5].SoftwareBlockingReason = VatEfsInstaller.GetBlockingReason();
        foreach (var result in _results)
        {
            if (result.IsLocalUrl && !string.IsNullOrEmpty(result.LaunchPath))
                UpdateLocalUrlReachability(result, listeners!);

            if (result.IsWebApp)
            {
                result.IsRunning = IsWebAppRunning(result.AppName);
                continue;
            }
            if (result.IsFolder || string.IsNullOrEmpty(result.LaunchPath))
            {
                result.IsRunning = false;
                continue;
            }
            result.IsRunning = ApplicationProcessControl.Find(result.LaunchPath).Count > 0;
        }
        RefreshBulkActionAvailability();
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

    private bool IsWebAppRunning(string appName) => TrackedBrowser(appName) != null;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RestorePresentationPlacement();
        if (SetupGuide.Initialize(_settings, _firstSetup)) SaveSessionSettings("Getting started. Your progress is saved as you go.");
        OfferReportingChoices();
        if (_windowClosed) return;
        ReportPresentationPreferences();
        if (_settings.SetupGuideActive)
        {
            SetupWorkspace.ShowSection(SetupGuide.Section(_settings));
            ShowWorkspace("Setup");
        }
        if (_windowClosed) return;
        ReportWorkspaceView();
        UpdateRunningStates();
        UpdateAppConfigButton();
        if (_settings.CheckOnStartup && !_setupRestartRequired && !_settings.SetupGuideActive)
            await RunChecks();
    }

    private void UpdateAppConfigButton()
    {
        bool needsAttention = _controllerApplyNeedsAttention || !ProfileService.IsConfigured(_settings) ||
            !ProfileService.IsInSync(_settings, _settings.EuroscopeDataPath);
        bool showAttention = _controllerApplyNeedsAttention || needsAttention && !_settings.SetupGuideActive;

        AppConfigButton.Content = showAttention ? "Controller profile · review" : "Controller profile";
        _controllerProfileNeedsReview = needsAttention;
        SetupTab.Content = showAttention ? "Setup · !" : "Setup";
        SetupTab.ToolTip = showAttention ? "Review controller profile choices in Setup" : "Your saved applications, GNG package and EuroScope configuration";
        RefreshWorkspace();
        AppConfigButton.ToolTip = showAttention
            ? "Review your controller identity and application configuration."
            : "Your identity and optional EuroScope profile configuration";
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!TryAcquireMaintenanceGuard(out var lease)) return;
        using var guard = lease;
        _isDarkMode = !_isDarkMode;
        App.SetTheme(_isDarkMode);
        ThemeToggleButton.Content = _isDarkMode ? "☀" : "☽";

        // Force row backgrounds to re-evaluate via the converter
        AppList.ItemsSource = null;
        PresentApplicationRows(_results);

        _settings.IsDarkMode = _isDarkMode;
        SaveSessionSettings("Theme preference saved.");
    }

    private async void Check_Click(object sender, RoutedEventArgs e) => await RunChecks(explicitRequest: true);

    private async Task RunChecks(bool explicitRequest = false)
    {
        if (_isChecking || _windowClosed || _softwareActionBusy || _softwareUpdates.IsBusy) return;
        if (!TryAcquireMaintenanceGuard(out var lease)) return;
        using var guard = lease;
        if (explicitRequest) ReportingEvents.Feature(ReportingFeature.CheckUpdates);
        _isChecking = true;
        UpdateSelfUpdateActionEnabled();
        CheckButton.IsEnabled = false;
        CheckButton.Content   = "Checking...";
        try
        {
            // Dialogs update the session copy explicitly. Reloading here would discard
            // completed setup choices if their last write to settings.json failed.
            ApplyLaunchPaths();

            foreach (var r in _results)
                if (!r.IsWebApp && !r.HasSelfUpdate && !r.HasSoftwareUpdate) r.Status = CheckStatus.Checking;

            await Task.WhenAll(
                UpdateChecker.CheckEuroscope(_results[0], _settings.EuroscopeExePath),
                UpdateChecker.CheckGng(_results[1], _settings.EuroscopeDataPath),
                CheckSoftwareAsync(),
                CheckSaamanAsync(),
                CheckLaunchpadAsync()
            );
            if (_windowClosed) return;

            // Fonts check piggy-backs on the GNG Pack row — sync filesystem-only probe.
            var fonts = FontService.Check(_settings.EuroscopeDataPath);
            _results[1].FontsState   = fonts.State;
            _results[1].FontsTooltip = fonts.Tooltip;
            Logger.Log("CHECK", $"Fonts: {fonts.State} ({string.Join("; ", fonts.Entries.Select(e => $"{e.FileName} installed={(e.InstalledVersion?.ToString("0.00") ?? (e.InstalledPath is null ? "missing" : "?"))} source={(e.SourceVersion?.ToString("0.00") ?? "?")} {(e.IsUpToDate ? "ok" : "needs-action")}"))})");

            LastCheckedText.Text = $"Last checked: {DateTime.Now:HH:mm:ss}";
            await ReportCheckedInventoryAsync();
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
        if (_windowClosed || _openDialogCount > 0) return;
        SetupWorkspace.ShowSection("Profile");
        ShowWorkspace("Setup");
        RefreshControllerProfileWorkspace();
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

        var row = LaunchpadRow;
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
        var row = LaunchpadRow;
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
        !_setupRestartRequired && !_windowClosed && !_isChecking && !_selfUpdateActionBusy && !_softwareActionBusy && !_softwareUpdates.IsBusy && _openDialogCount == 0 && IsEnabled &&
        !System.Windows.Interop.ComponentDispatcher.IsThreadModal &&
        OwnedWindows.Count == 0 &&
        !Application.Current.Windows.OfType<Window>().Any(window => window != this && window.IsVisible) &&
        _launchpadUpdates.State.Status is not (LaunchpadUpdateStatus.Checking or LaunchpadUpdateStatus.Downloading);

    private void UpdateSelfUpdateActionEnabled()
    {
        LaunchpadRow.SelfUpdateActionEnabled = CanRunSelfUpdateAction();
        UpdateSoftwareActions();
    }

    private async void SelfUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!CanRunSelfUpdateAction())
        {
            LaunchpadRow.StatusMessage = "Finish the current operation and close Launchpad's other windows before updating Launchpad.";
            return;
        }

        if (!TryAcquireMaintenanceGuard(out var lease)) return;
        using var guard = lease;
        var status = _launchpadUpdates.State.Status;
        if (!_launchpadUpdates.IsInstalled || status == LaunchpadUpdateStatus.NoFeed)
        {
            try { Process.Start(new ProcessStartInfo(LaunchpadDownloads) { UseShellExecute = true }); }
            catch { LaunchpadRow.StatusMessage = "The browser could not open. Visit Launchpad's GitHub Releases page for the installer."; }
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
                LaunchpadRow.StatusMessage = ex.Message;
                LaunchpadRow.SelfUpdateSummary = "Restart failed";
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
            if (!_windowClosed) LaunchpadRow.StatusMessage = ex.Message;
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
            if (window is SettingsWindow) ReportView(ReportingView.Preferences);
            else if (window is AppConfigWindow) ReportView(ReportingView.ControllerProfile);
            return window.ShowDialog();
        }
        finally
        {
            _openDialogCount--;
            if (!_windowClosed)
            {
                UpdateSelfUpdateActionEnabled();
                _lastPresentationReport = null;
                ReportPresentationPreferences();
                RefreshWorkspace();
                ReportWorkspaceView();
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _windowClosed = true;
        _processTimer.Stop();
        _launchpadUpdates.StateChanged -= LaunchpadUpdate_StateChanged;
        _launchpadUpdates.FailureObserved -= ReportSelfUpdateFailure;
        _softwareUpdates.StateChanged -= SoftwareUpdate_StateChanged;
        _softwareUpdates.FailureObserved -= ReportSoftwareFailure;
        UpdateChecker.FailureObserved -= ReportPublicCheckFailure;
        _windowLifetime.Cancel();
        _windowLifetime.Dispose();
        _softwareHttp.Dispose();
        base.OnClosed(e);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        SetupWorkspace.ShowSection("Preferences");
        ShowWorkspace("Setup");
    }
    private async void DiscoverInstallations_Click(object sender, RoutedEventArgs e)
    {
        if (!CanRunSoftwareAction()) return;
        ShowWorkspace("Setup");
        await DiscoverWorkspaceLocations();
    }

    private void OpenSettings(bool discover = false)
    {
        if (_isChecking || _softwareActionBusy || _softwareUpdates.IsBusy) return;
        if (!TryAcquireMaintenanceGuard(out var lease)) return;
        using var guard = lease;
        var win = new SettingsWindow(_settings, discoverOnLoad: discover) { Owner = this };
        if (ShowOwnedDialog(win) == true)
        {
            var previous = _settings;
            _settings            = win.Settings;
            _settings.IsDarkMode = _isDarkMode;
            SaveSessionSettings("Settings saved.");
            ApplyPresentationPreferences();
            ApplyLaunchPaths();
            RefreshEuroscopeProfiles();
            UpdateRunningStates();
            UpdateAppConfigButton();
            ResetChangedSoftwareChecks(previous);
            RefreshWorkspace();
        }
    }

    private bool SaveSessionSettings(string successMessage)
    {
        bool saved = SettingsService.TrySave(_settings);
        ReportPresentationPreferences();
        if (!saved) ReportingService.RecordError(ReportingErrorCode.SettingsSaveFailed,
            new ReportingDetails { Application = ReportingApplication.Launchpad, Operation = ReportingOperation.Save });
        LastCheckedText.Text = saved
            ? successMessage
            : "Your Launchpad choices are kept for this session, but could not be saved. Try saving again in Setup → Preferences. Completed profile or application changes have not been undone.";
        _preferencesWorkspace?.SetSaveResult(saved);
        RefreshWorkspace();
        return saved;
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (!CanRunSoftwareAction() || !TryAcquireMaintenanceGuard(out var lease)) return;
        using var guard = lease;
        if (sender is not FrameworkElement { DataContext: CheckResult result }) return;
        if (!result.LaunchEnabled || result.HasSoftwareUpdate && (_softwareActionBusy || _softwareUpdates.IsBusy)) return;

        try
        {
            if (result.IsFolder)
            {
                Process.Start("explorer.exe", result.LaunchPath);
                Logger.Log("LAUNCH", $"{result.AppName}: opened folder {result.LaunchPath}");
            }
            else if (RunningCopies(result).Count > 0)
            {
                ShowRunningApplication(result);
            }
            else
            {
                if (!StartApplicationCore(ApplicationTarget(result)))
                    throw new InvalidOperationException("The application did not accept the start request.");
                result.StatusMessage = "Start requested.";
            }
            UpdateRunningStates();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not launch {result.AppName}:\n\n{ex.Message}",
                "Launch failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleMaximize(); return; }
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

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
                if (!TryAcquireMaintenanceGuard(out var lease)) return;
                using var guard = lease;
                result.SelectedProfile = captured;
                _settings.LastEuroscopeProfile = captured.FilePath ?? string.Empty;
                SaveSessionSettings("EuroScope profile choice saved.");
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
        if (!TryAcquireMaintenanceGuard(out var lease)) return;
        using var guard = lease;
        var result = FontService.Check(_settings.EuroscopeDataPath);
        ReportingEvents.Feature(ReportingFeature.FontCheck, ReportingApplication.Gng);
        switch (result.State)
        {
            case FontsState.Unknown:
                MessageBox.Show(
                    "Set the EuroScope data folder in Setup → Swedish GNG to enable font checking.",
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
