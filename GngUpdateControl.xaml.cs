using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class GngUpdateControl : UserControl, IDisposable
{
    private const string RuntimeInformation = "https://developer.microsoft.com/en-us/microsoft-edge/webview2/";
    private static readonly string AppDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VatscaUpdateChecker", "Gng");
    private readonly string? _selectedProfile;
    private readonly string[] _protectedPaths;
    private readonly string[] _cleanupProtectedPaths;
    private readonly AppSettings? _saamanSettings;
    private readonly string? _installedVersion;
    private readonly Dictionary<Window, WebView2> _popups = new();
    private readonly HashSet<Window> _downloadHiddenPopups = [];
    private bool _browserInteractionBlocked;
    private readonly GngDownloadRequestState _downloadRequest = new();
    private readonly HashSet<(WebView2 Browser, ulong Navigation, long Revision)> _selectedDocuments = [];
    private readonly Dictionary<WebView2, GngBrowserNavigationState> _navigations = new();
    private CancellationTokenSource? _downloadWait;
    private CancellationTokenSource? _preparationCancellation;
    private bool _selectionInFlight;
    private TaskCompletionSource? _selectionFinished;
    private bool _popupRefreshUsed;
    private GngPackageKind _requestedPackageKind;
    private string? _primaryPackagePath;
    private string? _referenceVersion;
    private bool _preparingReference;
    private bool _startingBrowser;
    private WebView2? _browser;
    private CoreWebView2Environment? _environment;
    private CoreWebView2DownloadOperation? _download;
    private readonly GngDownloadProgressMeter _downloadProgressMeter = new();
    private readonly Stopwatch _downloadClock = new();
    private readonly DispatcherTimer _downloadProgressTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private TimeSpan? _lastDownloadProgressUpdate;
    private string? _transferSummary;
    private WebView2? _downloadOwner;
    private string? _downloadPath;
    private string? _downloadFailure;
    private long _downloadGeneration;
    private string? _newCompleteZip;
    private string? _oldCompleteZip;
    private GngUpdatePlan? _plan;
    private string? _reviewPackagePath;
    private string? _reviewReferencePath;
    private bool _reviewAllowTesting;
    private string? _cachedFullReferencePath;
    private string? _cachedFullReferenceVersion;
    private ReportingSource _reportingPackageSource = ReportingSource.Import;
    private string? _reportedPackagePath;
    private bool _usingSavedPackages;
    private bool _forceFreshCopies;
    private bool _busy;
    private bool _closed;
    private bool _queuedPreview;
    private bool _showingCompletion;
    private bool _cleanupOpen;
    private bool _recoveryRequired;
    private string? _pendingBackup;
    private readonly string? _suggestedRecoveryFolder;
    private readonly DispatcherTimer _euroScopeTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    private bool _writing;
    private bool _readingFilePreview;
    private bool _preparationCancelled;
    private bool _started;
    private bool _showingIssue;
    private GngCleanupControl? _cleanupControl;
    public event Action? StateChanged;
    public event Action? Dismissed;
    public event Action<string>? FollowUpRequested;
    public string? InstalledPackageVersion { get; private set; }
    public bool PackageAlreadyMatches { get; private set; }
    public int MutationRevision { get; private set; }
    public int CompletionRevision { get; private set; }
    public bool HasError => _showingIssue || _recoveryRequired;
    private string? _lastBackupFolder;
    private string? _reviewedRecoveryFolder;
    public void SetCompletionChecks(string message, bool fontsNeedAction, bool profileNeedsReview)
    {
        CompletionChecks.Text = message;
        CompletionChecks.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        CheckFontsButton.Visibility = fontsNeedAction ? Visibility.Visible : Visibility.Collapsed;
        CheckProfileButton.Visibility = profileNeedsReview ? Visibility.Visible : Visibility.Collapsed;
    }
    private void CheckFonts_Click(object sender, RoutedEventArgs e) { if (CanDismiss) FollowUpRequested?.Invoke("Fonts"); }
    private void CheckProfile_Click(object sender, RoutedEventArgs e) { if (CanDismiss) FollowUpRequested?.Invoke("Profile"); }
    public bool IsBusy => _busy || _queuedPreview || _selectionInFlight || _download is not null || _downloadRequest.IsPending || _cleanupControl?.IsBusy == true;
    public bool CanDismiss => !_busy && !_queuedPreview && !_selectionInFlight && _download is null && _cleanupControl?.IsBusy != true;
    public bool CanCancel => !_writing && !_closed && (_busy || _queuedPreview || _download is not null || _downloadRequest.IsPending || _selectionInFlight);
    public string Status => StatusText?.Text ?? string.Empty;
    public string Stage => DownloadStageText?.Text ?? string.Empty;
    public string? TransferSummary => _transferSummary;
    public double Progress => Activity?.Value ?? 0;
    public bool ProgressIndeterminate => Activity?.IsIndeterminate == true;
    public string DataFolder { get; }
    public bool AttemptedChanges { get; private set; }
    public bool InstallationCompleted { get; private set; }
    public bool RestartRequired { get; private set; }
    private bool Idle => !_busy && !_queuedPreview && !_cleanupOpen && _download is null && !_closed && !RestartRequired;
    private bool CanGetPackage => Idle && !_recoveryRequired && !_selectionInFlight;

    public GngUpdateControl(string dataFolder, string? selectedProfile, IEnumerable<string> protectedPaths,
        IEnumerable<string>? cleanupProtectedPaths = null, string? recoveryFolder = null, AppSettings? saamanSettings = null,
        string? installedVersion = null)
    {
        InitializeComponent();
        StatusText.TextChanged += (_, _) => NotifyStateChanged();
        Activity.ValueChanged += (_, _) => NotifyStateChanged();
        DataFolder = dataFolder ?? string.Empty;
        _selectedProfile = selectedProfile;
        _saamanSettings = saamanSettings?.Copy();
        _installedVersion = installedVersion;
        _suggestedRecoveryFolder = recoveryFolder;
        _protectedPaths = protectedPaths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        _cleanupProtectedPaths = (cleanupProtectedPaths ?? _protectedPaths).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        FolderText.Text = string.IsNullOrWhiteSpace(DataFolder) ? "No GNG folder chosen. Select it in Setup." : DataFolder;
        FolderText.ToolTip = "Swedish GNG folder:\n" + FolderText.Text + "\nChange this folder in Setup.";
        UpdatePackageKind();
        ShowPendingRecovery();
        if (!_recoveryRequired && !string.IsNullOrWhiteSpace(recoveryFolder))
            ShowCompletion("Review GNG recovery", "Choose Restore backup to review the selected recovery copy. Nothing is restored until you confirm the backup and destination.", recoveryFolder);
        RefreshEuroScopeStatus();
        _euroScopeTimer.Tick += (_, _) => RefreshEuroScopeStatus();
        _downloadProgressTimer.Tick += (_, _) => UpdateDownloadProgress();
        Loaded += (_, _) => _euroScopeTimer.Start();
    }

    private void RefreshEuroScopeStatus()
    {
        if (_closed) return;
        SetEuroScopeBlockingReason(EuroScopeProcessGuard.GetBlockingReason());
        UpdateControls();
    }

    private void ShowPendingRecovery(string? errorDetails = null)
    {
        try
        {
            _pendingBackup = GngUpdateService.GetPendingUpdate(DataFolder);
            if (_pendingBackup is null) { _recoveryRequired = false; return; }
            _recoveryRequired = true;
            var details = "An earlier installation or restore did not finish. Restore the backup below before downloading another package or using EuroScope. Files edited since installation will be kept for review.";
            if (!string.IsNullOrWhiteSpace(errorDetails)) details += Environment.NewLine + Environment.NewLine + errorDetails;
            ShowCompletion("Restore the unfinished GNG update", details, _pendingBackup);
            StatusText.Text = "Choose Restore backup to recover this data folder. Keep the backup until recovery is complete.";
        }
        catch (Exception ex)
        {
            _recoveryRequired = true;
            string location;
            try { location = GngUpdateService.GetBackupRoot(DataFolder); }
            catch (Exception) { location = "The recovery location could not be validated. Keep your existing GNG backup folders."; }
            var details = "Recovery information could not be read safely. Keep the backup folders and resolve this problem before installing another package. " + ex.Message;
            if (!string.IsNullOrWhiteSpace(errorDetails)) details += Environment.NewLine + Environment.NewLine + errorDetails;
            ShowCompletion("GNG recovery needs attention", details, location);
            StatusText.Text = "Restore backup remains available. Do not remove recovery information to bypass this problem.";
        }
    }

    public async Task StartAsync(bool forceDownload = false)
    {
        if (_started && !forceDownload) { NotifyStateChanged(); return; }
        _started = true;
        if (_recoveryRequired || !string.IsNullOrWhiteSpace(_suggestedRecoveryFolder)) { NotifyStateChanged(); return; }
        try { await GetPackageAsync(forceDownload); }
        catch (OperationCanceledException) when (_preparationCancelled) { ShowPreparationStopped(); SetBusy(false); }
        catch (Exception ex) { ShowIssue("GNG download needs attention", ex.Message); SetBusy(false); }
    }

    public Task DownloadFreshAsync() => StartDownloadSafelyAsync(true);
    public Task ImportPackageAsync() => ImportAsync();

    public void CancelPreparation()
    {
        if (!CanCancel) return;
        CancelFilePreviewNavigation();
        _preparationCancelled = true;
        _preparationCancellation?.Cancel();
        if (_download is not null) CancelDownload_Click(this, new RoutedEventArgs());
        else DisarmDownloadRequest(closeBrowsers: true);
        ShowPreparationStopped();
        UpdateControls();
    }

    private void NotifyStateChanged() { if (!_closed) StateChanged?.Invoke(); }
    private async void StartBrowser_Click(object sender, RoutedEventArgs e) => await StartDownloadSafelyAsync(false);
    private async Task StartDownloadSafelyAsync(bool fresh)
    {
        try { await GetPackageAsync(fresh); }
        catch (OperationCanceledException) when (_preparationCancelled) { ShowPreparationStopped(); SetBusy(false); }
        catch (Exception ex) { ShowIssue("GNG download needs attention", ex.Message); SetBusy(false); }
    }

    private async void FreshDownload_Click(object sender, RoutedEventArgs e)
    {
        if (!CanGetPackage) return;
        _primaryPackagePath = _referenceVersion = null;
        await StartDownloadSafelyAsync(true);
    }

    private async Task GetPackageAsync(bool forceDownload)
    {
        if (!CanGetPackage) return;
        CancelFilePreviewNavigation();
        PackageAlreadyMatches = false;
        _preparationCancelled = false;
        _preparationCancellation?.Dispose();
        _preparationCancellation = new CancellationTokenSource();
        _showingIssue = false;
        _forceFreshCopies = forceDownload;
        _reviewPackagePath = _reviewReferencePath = null;
        _cachedFullReferencePath = _cachedFullReferenceVersion = null;
        if (_primaryPackagePath is null) _usingSavedPackages = false;
        DisarmDownloadRequest(closeBrowsers: true);
        if (_primaryPackagePath is null) UpdatePackageKind();
        else _requestedPackageKind = GngPackageKind.Full;
        if (!forceDownload && _primaryPackagePath is null)
        {
            GngCachedPackages? saved;
            SetBusy(true, "Checking for the latest Swedish GNG update…", "Checking saved packages");
            try { saved = await GngPackageCache.TryFindAsync(_requestedPackageKind, _preparationCancellation.Token); }
            finally { SetBusy(false); }
            if (_closed || _preparationCancelled) return;
            if (saved is not null)
            {
                _usingSavedPackages = true;
                _reportingPackageSource = ReportingSource.Cache;
                if (saved.PackagePath is { } primary && (_requestedPackageKind == GngPackageKind.Full || saved.FullPackagePath is not null))
                {
                    await PreviewPackageAsync(primary, saved.FullPackagePath);
                    return;
                }
                if (saved.PackagePath is { } update)
                {
                    _primaryPackagePath = update;
                    _referenceVersion = saved.Version;
                    _requestedPackageKind = GngPackageKind.Full;
                    PackageKindText.Text = "Update Only " + saved.Version + " is saved. Only its matching Full reference needs downloading.";
                }
                else
                {
                    _cachedFullReferencePath = saved.FullPackagePath;
                    _cachedFullReferenceVersion = saved.Version;
                    PackageKindText.Text = "The Full reference for " + saved.Version + " is saved. Only Update Only needs downloading.";
                }
            }
        }
        _downloadRequest.Arm(referenceFollows: _primaryPackagePath is null && _requestedPackageKind == GngPackageKind.UpdateOnly);
        _popupRefreshUsed = false;
        SetBusy(true, "Starting the AeroNav browser…", "Preparing download");
        try { await InitializeBrowserAsync(); }
        finally { SetBusy(false); }
    }

    private void UpdatePackageKind()
    {
        var validation = new ConfiguredPathValidationService().Validate(ConfiguredPathKind.EuroScopeDataFolder, DataFolder);
        _requestedPackageKind = validation.Status == PathValidationStatus.Verified ? GngPackageKind.UpdateOnly : GngPackageKind.Full;
        PackageKindText.Text = _requestedPackageKind == GngPackageKind.UpdateOnly
            ? "Existing Swedish GNG found — Update Only will be installed after review. A matching Full Package will also be kept for cleanup comparisons."
            : "A complete Swedish GNG installation was not found — Full Package will be used for setup or repair.";
    }

    private async Task InitializeBrowserAsync()
    {
        // Present the native host before either async creation step, including
        // a replacement browser needed for the second package download.
        _startingBrowser = true;
        UpdateControls();
        using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _preparationCancellation?.Token ?? CancellationToken.None);
        startupCancellation.CancelAfter(TimeSpan.FromSeconds(60));
        var startupToken = startupCancellation.Token;
        try
        {
            if (_browser?.CoreWebView2 is { } existing)
            {
                StartPanel.Visibility = Visibility.Collapsed;
                BrowserToolbar.Visibility = Visibility.Visible;
                SetStage(_primaryPackagePath is null ? "Finding your package" : "Getting Full reference (2 of 2)");
                _navigations[_browser].ExpectNavigation();
                existing.Navigate(GngBrowserPolicy.AeroNavHome);
                return;
            }
            var profileFolder = Path.Combine(AppDataFolder, "Browser");
            SoftwareInstaller.RejectReparse(profileFolder);
            Directory.CreateDirectory(profileFolder);
            SoftwareInstaller.RejectReparse(profileFolder);
            _environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profileFolder).WaitAsync(startupToken);
            if (_closed || _preparationCancelled) return;
            _browser?.Dispose();
            _browser = new WebView2();
            SetBrowserBackground(_browser);
            BrowserHost.Content = _browser;
            await _browser.EnsureCoreWebView2Async(_environment).WaitAsync(startupToken);
            if (_closed || _preparationCancelled) { ResetBrowser(); return; }
            ConfigureBrowser(_browser, OriginText);
            StartBrowserButton.Visibility = Visibility.Visible;
            FreshDownloadButton.Visibility = Visibility.Visible;
            RuntimeInstallButton.Visibility = RuntimeInfoButton.Visibility = Visibility.Collapsed;
            StartPanel.Visibility = Visibility.Collapsed;
            BrowserToolbar.Visibility = Visibility.Visible;
            SetStage(_primaryPackagePath is null ? "Sign in if requested" : "Getting Full reference (2 of 2)");
            _navigations[_browser].ExpectNavigation();
            _browser.CoreWebView2.Navigate(GngBrowserPolicy.AeroNavHome);
            StatusText.Text = _primaryPackagePath is null
                ? "Sign in if AeroNav asks. Launchpad will download the Swedish package and show it for review."
                : "Continuing the download (2 of 2)…";
        }
        catch (Exception) when (_preparationCancelled || _closed)
        {
            ResetBrowser();
        }
        catch (OperationCanceledException)
        {
            ResetBrowser();
            ShowBrowserFallback("The AeroNav browser took too long to start. Retry the download, or use your normal browser and import the ZIP.");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ResetBrowser();
            ShowBrowserFallback("The Microsoft WebView2 Runtime is missing. Install it below, or use your normal browser and import the downloaded ZIP.", runtimeMissing: true);
        }
        catch (Exception)
        {
            // Browser exceptions can contain sign-in URLs or callback parameters.
            ResetBrowser();
            ShowBrowserFallback("The embedded browser could not start. Use your normal browser and Import ZIP, or check the WebView2 Runtime and reopen Launchpad.");
        }
        finally { _startingBrowser = false; UpdateControls(); }
    }

    private async void InstallRuntime_Click(object sender, RoutedEventArgs e)
    {
        if (!CanGetPackage) return;
        if (MessageBox.Show(Window.GetWindow(this), "Download and install Microsoft Edge WebView2 for all Windows users?\n\nWindows may ask for administrator permission. This installs the browser runtime only. You can instead download the GNG ZIP in your normal browser and import it.",
                "Install Microsoft WebView2", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (!MaintenanceLock.TryAcquire(out var lease))
        {
            StatusText.Text = "Another Launchpad maintenance operation is active. Finish it first.";
            return;
        }
        using (lease)
        {
            _writing = true;
            SetBusy(true, "Preparing Microsoft WebView2 Runtime setup…");
            string? failure = null;
            try
            {
                var result = await WebViewRuntimeService.InstallAsync(new Progress<string>(message => { if (_busy) StatusText.Text = message; }),
                    onRestartRequired: () => RestartRequired = true);
                RestartRequired |= result.RestartRequired;
                ActivityHistoryService.Record(ActivityApplication.Gng, ActivityOperation.Prerequisite,
                    ActivityOutcome.Completed, restartRequired: RestartRequired);
                if (!RestartRequired)
                {
                    ResetBrowser();
                    ShowBrowserFallback("Microsoft WebView2 is installed. Choose Download Swedish GNG to continue.");
                }
            }
            catch (Exception ex)
            {
                ActivityHistoryService.Record(ActivityApplication.Gng, ActivityOperation.Prerequisite,
                    ActivityOutcome.Stopped, restartRequired: RestartRequired);
                failure = "Runtime setup stopped: " + ex.Message; StatusText.Text = failure;
            }
            finally
            {
                if (RestartRequired) StatusText.Text = (failure is null ? "" : failure + Environment.NewLine) + "Restart Windows before further installation or recovery.";
                _writing = false;
                SetBusy(false);
            }
        }
    }

    private void ConfigureBrowser(WebView2 webView, TextBlock origin)
    {
        var core = webView.CoreWebView2;
        var navigationState = new GngBrowserNavigationState();
        _navigations[webView] = navigationState;
        var color = ((SolidColorBrush)FindResource("AppBg")).Color;
        core.Profile.PreferredColorScheme = color.R + color.G + color.B < 384 ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsBuiltInErrorPageEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.NavigationStarting += (_, args) =>
        {
            if (!GngBrowserPolicy.IsAllowedNavigation(args.Uri))
            {
                args.Cancel = true;
                if (!_closed && !_busy && _download is null) StatusText.Text = "Only HTTPS websites are supported here. External application links are blocked.";
                return;
            }
            if (navigationState.Start(args.NavigationId, _downloadRequest.Generation, args.IsRedirected))
                origin.Text = GngBrowserPolicy.VisibleOrigin(args.Uri);
        };
        core.SourceChanged += (_, _) => { if (!_closed) origin.Text = GngBrowserPolicy.VisibleOrigin(core.Source); };
        core.FrameNavigationStarting += (_, args) => args.Cancel = !GngBrowserPolicy.IsAllowedFrameNavigation(args.Uri);
        core.HistoryChanged += (_, _) => { if (!_closed && ReferenceEquals(webView, _browser)) UpdateControls(); };
        core.NavigationCompleted += (_, args) =>
        {
            // A ZIP navigation can report a page failure after the transfer completes.
            // Only a current page request waiting for content owns this diagnostic.
            if (!_closed && IsCurrentBrowser(webView) && !args.IsSuccess && args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled &&
                !_busy && !_queuedPreview && !_selectionInFlight && _download is null && BrowserPanel.Visibility == Visibility.Visible &&
                navigationState.CanReportFailure(args.NavigationId, _downloadRequest.Generation) &&
                _downloadRequest.CanReportNavigationFailure(_downloadRequest.Generation))
            {
                Logger.Log("GNG", $"Package page failed: {args.WebErrorStatus}; HTTP {args.HttpStatusCode}; reference={_primaryPackagePath is not null}.");
                ReportingService.RecordError(ReportingErrorCode.NetworkUnavailable,
                    new ReportingDetails { Application = ReportingApplication.Gng, Operation = ReportingOperation.Download, Stage = ReportingStage.Prepare });
                DisarmDownloadRequest();
                ShowIssue("Package page could not load", _primaryPackagePath is null
                    ? $"AeroNav could not load ({args.WebErrorStatus}). Retry the download, or open it in your browser and import the ZIP."
                    : $"The Full reference page could not load ({args.WebErrorStatus}). Your Update Only ZIP is saved. Choose Retry reference or import its matching Full ZIP.");
                UpdateControls();
            }
            if (args.IsSuccess && navigationState.TryMarkReady(args.NavigationId)) QueuePackageSelection(webView, args.NavigationId);
        };
        core.DOMContentLoaded += (_, args) =>
        {
            if (navigationState.TryMarkReady(args.NavigationId)) QueuePackageSelection(webView, args.NavigationId);
        };
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        core.LaunchingExternalUriScheme += (_, args) => args.Cancel = true;
        core.ServerCertificateErrorDetected += (_, args) => args.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
        core.DownloadStarting += (_, args) => DownloadStarting(webView, args);
        core.NewWindowRequested += Browser_NewWindowRequested;
        core.ProcessFailed += (_, _) =>
        {
            if (_closed) return;
            ReportingService.RecordError(ReportingErrorCode.BrowserProcessFailed,
                new ReportingDetails { Application = ReportingApplication.Gng, Operation = ReportingOperation.Download });
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_closed) return;
                if (_downloadOwner == webView && _download is not null)
                {
                    DisarmDownloadRequest();
                    try { _download.Cancel(); } catch { }
                    DetachDownload();
                    Activity.Visibility = Visibility.Collapsed;
                    ShowIssue("Download stopped", "The browser stopped during the download. The package was not installed. Reopen this window or import a complete ZIP.");
                    UpdateControls();
                }
                if (ReferenceEquals(webView, _browser) && !_busy && !_queuedPreview)
                {
                    ResetBrowser();
                    ShowBrowserFallback("The browser stopped. Close and reopen this window, or use your normal browser and import the ZIP.");
                }
            }));
        };
    }

    private void SetBrowserBackground(WebView2 webView)
    {
        var color = ((SolidColorBrush)FindResource("AppBg")).Color;
        webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
    }

    private void ResetBrowser()
    {
        BrowserHost.Content = null;
        if (_browser is not null) _navigations.Remove(_browser);
        try { _browser?.Dispose(); }
        catch (Exception) { /* A failed browser process may already have released its control. */ }
        _browser = null;
        _environment = null;
    }

    private void DisarmDownloadRequest(bool closeBrowsers = false)
    {
        _downloadRequest.Disarm();
        _downloadWait?.Cancel();
        _downloadWait?.Dispose();
        _downloadWait = null;
        _selectedDocuments.Clear();
        if (!closeBrowsers) return;
        ClosePopups();
        ResetBrowser();
        _navigations.Clear();
        if (!_closed) { BrowserToolbar.Visibility = Visibility.Collapsed; StartPanel.Visibility = Visibility.Visible; }
    }

    private bool IsCurrentBrowser(WebView2 browser) => ReferenceEquals(browser, _browser) || _popups.Values.Contains(browser);

    private void QueuePackageSelection(WebView2 browser, ulong navigation)
    {
        if (_closed || !_downloadRequest.IsPending) return;
        Dispatcher.BeginInvoke(new Action(async () => await TrySelectPackageAsync(browser, navigation)));
    }

    private void QueueReadyPackagePages()
    {
        foreach (var entry in _navigations.ToArray())
            if (entry.Value.ReadyNavigationId is { } id) QueuePackageSelection(entry.Key, id);
    }

    private void RetryChangedInspection(long generation, ulong navigation, long revision, GngBrowserNavigationState navigationState,
        bool browserPresent, bool packagePage)
    {
        var retry = _downloadRequest.RetryChangedInspection(generation);
        Logger.Log("GNG", $"Inspection discarded after page navigation: result={retry}; navigation={navigation}; revision={revision}; currentRevision={navigationState.Revision}; browserPresent={browserPresent}; packagePage={packagePage}.");
        if (retry == GngInspectionRetry.WaitingForPage)
        {
            SetStage("Waiting for AeroNav");
            StatusText.Text = "AeroNav is loading another page. Launchpad will continue when the package page is ready; complete sign-in if asked.";
        }
        else if (retry == GngInspectionRetry.LimitReached)
            ShowIssue("Package page is still changing", "The page changed repeatedly while being checked. Let AeroNav finish loading, then choose Retry download. Nothing was installed.");
    }

    private async Task TrySelectPackageAsync(WebView2 browser, ulong navigation)
    {
        if (!CanGetPackage || !IsCurrentBrowser(browser) || BrowserPanel.Visibility != Visibility.Visible ||
            !_navigations.TryGetValue(browser, out var navigationState) || !navigationState.CanInspect(navigation)) return;
        var revision = navigationState.Revision;
        if (_selectedDocuments.Contains((browser, navigation, revision))) return;
        long generation = 0;
        bool started = false;
        try
        {
            var core = browser.CoreWebView2;
            if (core is null || !Uri.TryCreate(core.Source, UriKind.Absolute, out var page) || !GngPackageSelection.IsPackagePage(page) ||
                !_downloadRequest.TryBeginSelection(out generation)) return;
            _selectedDocuments.Add((browser, navigation, revision));
            started = true;
            _selectionInFlight = true;
            _selectionFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            UpdateControls();
            var kind = _requestedPackageKind;
            var referenceVersion = _referenceVersion;
            // Read metadata before clicking: the download may navigate away and prevent
            // the click script from returning. No credentials or download URLs leave the page.
            var json = await core.ExecuteScriptAsync(GngPackageSelection.CreateInspectionScript(kind, referenceVersion)).WaitAsync(TimeSpan.FromSeconds(15));
            if (_closed || generation != _downloadRequest.Generation) return;
            if (!IsCurrentBrowser(browser))
            {
                RetryChangedInspection(generation, navigation, revision, navigationState, browserPresent: false, packagePage: false);
                return;
            }
            var packagePage = Uri.TryCreate(core.Source, UriKind.Absolute, out page) && GngPackageSelection.IsPackagePage(page);
            if (!navigationState.IsCurrent(navigation, revision) || !packagePage)
            {
                RetryChangedInspection(generation, navigation, revision, navigationState, browserPresent: true, packagePage);
                return;
            }
            var result = GngPackageSelection.ParseResult(json);
            if (!_downloadRequest.CompleteSelection(generation, result.Status, result.Identity)) return;
            if (result.Status == "ready")
            {
                SetStage(_primaryPackagePath is null ? "Starting package download" : "Starting Full reference (2 of 2)");
                StatusText.Text = _primaryPackagePath is null
                    ? "Starting Swedish GNG " + result.Version + " download…"
                    : "Starting Full reference package " + result.Version + " download. It will not be installed.";
                Logger.Log("GNG", $"Package selected: kind={kind}; version={result.Version}; identity={result.Identity}.");
                _ = WaitForDownloadAsync(generation);
                var clickJson = await core.ExecuteScriptAsync(GngPackageSelection.CreateDownloadScript(kind, result.Identity!, referenceVersion)).WaitAsync(TimeSpan.FromSeconds(15));
                if (_closed || generation != _downloadRequest.Generation || !IsCurrentBrowser(browser)) return;
                var clickResult = GngPackageSelection.ParseResult(clickJson);
                if (_downloadRequest.CompleteClick(generation, clickResult.Status) && clickResult.Status != "clicked")
                {
                    _downloadWait?.Cancel();
                    Logger.Log("GNG", "Package click stopped: " + clickResult.Status + ".");
                    ShowIssue("Download did not start", "The package page changed or did not start the selected download. Choose Retry download." +
                        (_primaryPackagePath is null ? "" : " Your Update Only ZIP is saved; Retry reference downloads only the missing Full ZIP."));
                }
            }
            else if (result.Status is "waiting-for-login" or "wrong-page")
            {
                SetStage("Waiting for sign-in");
                StatusText.Text = "Complete the website sign-in. Launchpad will download the Swedish package when AeroNav is ready.";
            }
            else
                ShowIssue("Package selection needs attention", _primaryPackagePath is not null
                    ? "The matching Full reference package could not be selected. Your update ZIP is saved. Retry reference, import its matching Full ZIP, or Start over to download both again."
                    : result.Status == "ambiguous"
                    ? "AeroNav lists more than one matching package. No download was started. Choose Retry download, or use your browser and Import ZIP."
                    : "The Swedish package could not be selected on this page. Choose Retry download, or use your browser and Import ZIP.");
        }
        catch (Exception ex)
        {
            if (!_closed && generation == _downloadRequest.Generation && _downloadRequest.Phase == GngDownloadRequestPhase.Selecting &&
                (!IsCurrentBrowser(browser) || !navigationState.IsCurrent(navigation, revision)))
            {
                // Navigating or closing a sign-in popup may abort ExecuteScriptAsync.
                // It is safe to retry only the side-effect-free inspection, never the click.
                RetryChangedInspection(generation, navigation, revision, navigationState, IsCurrentBrowser(browser), packagePage: false);
                return;
            }
            if (!_closed && generation == _downloadRequest.Generation && _downloadRequest.Phase != GngDownloadRequestPhase.Downloading)
            {
                // Close the browser on timeout so a delayed script cannot click after a retry.
                Logger.Log("GNG", "Package selection failed: " + ex.GetType().Name + ".");
                DisarmDownloadRequest(closeBrowsers: true);
                ShowIssue("Package selection needs attention", _primaryPackagePath is null
                    ? "The package page could not be checked. Choose Retry download, or use your browser and Import ZIP."
                    : "The reference package page could not be checked. Your update ZIP is saved; retry or import the matching Full reference.");
            }
        }
        finally
        {
            if (started)
            {
                _selectionInFlight = false;
                _selectionFinished?.TrySetResult();
                if (!_closed)
                {
                    UpdateControls();
                    if (_downloadRequest.Phase == GngDownloadRequestPhase.WaitingForPage)
                    {
                        if (!IsCurrentBrowser(browser)) RefreshAfterSignInWindow();
                        QueueReadyPackagePages();
                    }
                }
            }
        }
    }

    private async Task WaitForDownloadAsync(long generation)
    {
        _downloadWait?.Cancel();
        _downloadWait?.Dispose();
        _downloadWait = new CancellationTokenSource();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), _downloadWait.Token);
            if (!_closed && _downloadRequest.ExpireDownloadWait(generation))
            {
                ShowIssue("Download did not start", _primaryPackagePath is null
                    ? "AeroNav did not start the download. Choose Retry download, or use your browser and Import ZIP."
                    : "AeroNav did not start the Full reference download. Your update ZIP is saved; retry reference or import the matching Full ZIP.");
                UpdateControls();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void RefreshAfterSignInWindow()
    {
        try
        {
            if (_closed || _popupRefreshUsed || !CanGetPackage || _downloadRequest.Phase != GngDownloadRequestPhase.WaitingForPage ||
                _browser?.CoreWebView2 is not { } core || !Uri.TryCreate(core.Source, UriKind.Absolute, out var page) || !GngPackageSelection.IsPackagePage(page)) return;
            _popupRefreshUsed = true;
            _navigations[_browser].ExpectNavigation();
            core.Reload();
        }
        catch (Exception)
        {
            DisarmDownloadRequest();
            if (!_closed) { StatusText.Text = "The package page could not refresh. Choose Retry download."; UpdateControls(); }
        }
    }

    private async void Browser_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (!Idle || _recoveryRequired || !_downloadRequest.IsPending || BrowserPanel.Visibility != Visibility.Visible || _popups.Count >= 3 || !GngBrowserPolicy.IsAllowedNavigation(e.Uri) || _environment is null) return;
        var deferral = e.GetDeferral();
        Window? popup = null;
        WebView2? child = null;
        try
        {
            var origin = new TextBlock { Text = GngBrowserPolicy.VisibleOrigin(e.Uri), Margin = new Thickness(12), TextTrimming = TextTrimming.CharacterEllipsis };
            origin.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryText");
            var layout = new DockPanel();
            DockPanel.SetDock(origin, Dock.Top);
            layout.Children.Add(origin);
            child = new WebView2();
            SetBrowserBackground(child);
            layout.Children.Add(child);
            var workArea = SystemParameters.WorkArea;
            popup = new Window
            {
                Owner = Window.GetWindow(this), Title = "AeroNav · Browser window", Width = Math.Min(700, workArea.Width - 24), Height = Math.Min(700, workArea.Height - 24),
                MinWidth = 460, MinHeight = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = layout
            };
            popup.SetResourceReference(BackgroundProperty, "RowBg");
            var childWindow = popup;
            popup.Closing += (_, args) =>
            {
                if (_downloadOwner == child && _download is not null)
                {
                    args.Cancel = true;
                    StatusText.Text = "Cancel the download or wait for completion before closing its browser window.";
                }
            };
            popup.Closed += (_, _) =>
            {
                _popups.Remove(childWindow);
                _downloadHiddenPopups.Remove(childWindow);
                _navigations.Remove(child);
                child.Dispose();
                if (!_closed) Dispatcher.BeginInvoke(new Action(RefreshAfterSignInWindow));
            };
            _popups.Add(popup, child);
            popup.Show();
            await child.EnsureCoreWebView2Async(_environment);
            if (_closed || !_popups.ContainsKey(popup)) return;
            ConfigureBrowser(child, origin);
            child.CoreWebView2.WindowCloseRequested += (_, _) => Dispatcher.BeginInvoke(new Action(childWindow.Close));
            // Share this environment/profile and preserve window.opener for provider sign-in.
            e.NewWindow = child.CoreWebView2;
        }
        catch (Exception)
        {
            popup?.Close();
            if (popup is null) child?.Dispose();
            if (!_closed) StatusText.Text = "The sign-in window could not open. Use your browser and import the downloaded ZIP.";
        }
        finally
        {
            try { deferral.Complete(); }
            catch (Exception) { /* The browser may have closed while its child was starting. */ }
        }
    }

    private void DownloadStarting(WebView2 owner, CoreWebView2DownloadStartingEventArgs e)
    {
        _reportingPackageSource = ReportingSource.Download;
        e.Handled = true;
        e.Cancel = true;
        if (!Idle || _recoveryRequired || !IsCurrentBrowser(owner) || BrowserPanel.Visibility != Visibility.Visible || _showingCompletion ||
            _downloadRequest.Phase != GngDownloadRequestPhase.WaitingForDownload) return;
        if (!GngBrowserPolicy.TryGetDownloadFilename(e.DownloadOperation.Uri, e.ResultFilePath, out var filename))
        {
            DisarmDownloadRequest();
            ShowIssue("Download not accepted", "This is not a recognized ESAA ZIP from AeroNav. If the website uses another download host, use your browser and Import ZIP.");
            UpdateControls();
            return;
        }
        if (GngBrowserPolicy.IsCompletePackage(filename) != (_requestedPackageKind == GngPackageKind.Full))
        {
            DisarmDownloadRequest();
            ShowIssue("Download not accepted", "AeroNav returned a different package type than requested. No download was accepted. Retry or import the correct ZIP.");
            UpdateControls();
            return;
        }
        if (_downloadRequest.SelectedIdentity is not null && !_downloadRequest.MatchesSelectedFilename(filename))
        {
            DisarmDownloadRequest();
            ShowIssue("Download not accepted", "The download does not match the package selected on AeroNav. Retry or import the intended ZIP.");
            UpdateControls();
            return;
        }
        if (e.DownloadOperation.TotalBytesToReceive is > GngBrowserPolicy.MaximumDownloadBytes)
        {
            DisarmDownloadRequest();
            ShowIssue("Download too large", "The package download exceeds the supported 2 GB limit.");
            UpdateControls();
            return;
        }
        if (!_downloadRequest.TryStartDownload()) return;
        _downloadWait?.Cancel();
        try
        {
            // WebView2 overwrites existing targets. A separate directory prevents collisions.
            var folder = Path.Combine(AppDataFolder, "Downloads", Guid.NewGuid().ToString("N"));
            SoftwareInstaller.RejectReparse(folder);
            Directory.CreateDirectory(folder);
            SoftwareInstaller.RejectReparse(folder);
            _downloadPath = Path.Combine(folder, filename);
            e.ResultFilePath = _downloadPath;
            _download = e.DownloadOperation;
            _downloadProgressMeter.Reset();
            _downloadClock.Restart();
            _lastDownloadProgressUpdate = null;
            _downloadProgressTimer.Start();
            _downloadGeneration = _downloadRequest.Generation;
            _downloadOwner = owner;
            _downloadFailure = null;
            _download.BytesReceivedChanged += Download_ProgressChanged;
            _download.StateChanged += Download_StateChanged;
            _plan = null;
            UpdateControls();
            UpdateDownloadProgress();
            e.Cancel = _download is null || _downloadFailure is not null;
            Logger.Log("GNG", $"Download accepted: kind={_requestedPackageKind}; expectedBytes={_download?.TotalBytesToReceive}; request={_downloadGeneration}.");
            var operation = _download;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // Fast/cached downloads may already be complete when handlers attach.
                if (operation is not null && ReferenceEquals(_download, operation)) Download_StateChanged(operation, EventArgs.Empty);
            }));
        }
        catch (Exception)
        {
            DisarmDownloadRequest();
            DetachDownload();
            Activity.Visibility = Visibility.Collapsed;
            ShowIssue("Download could not start", "The download could not be prepared. Use your browser and import the original ZIP.");
            UpdateControls();
        }
    }

    private void Download_ProgressChanged(object? sender, object e)
    {
        if (ReferenceEquals(sender, _download)) UpdateDownloadProgress();
    }

    private void UpdateDownloadProgress()
    {
        try { UpdateDownloadProgressCore(); }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // A timer tick can precede the browser's queued ProcessFailed cleanup.
            // Native errors can contain URLs, so report a fixed message only.
            DisarmDownloadRequest();
            try { _download?.Cancel(); } catch (Exception) { }
            DetachDownload();
            ClosePopups();
            ResetBrowser();
            Activity.Visibility = Visibility.Collapsed;
            ShowIssue("Download stopped", "The browser stopped reporting the download. Nothing was installed. Retry the download, or import a complete ZIP.");
            UpdateControls();
        }
    }

    private void UpdateDownloadProgressCore()
    {
        if (_download is null || _closed) return;
        var elapsed = _downloadClock.Elapsed;
        var total = _download.TotalBytesToReceive.GetValueOrDefault();
        var received = _download.BytesReceived;
        if (received > GngBrowserPolicy.MaximumDownloadBytes || total > GngBrowserPolicy.MaximumDownloadBytes)
        {
            _downloadFailure = "The download exceeded the supported 2 GB limit and was cancelled.";
            _download.Cancel();
            return;
        }
        if (_lastDownloadProgressUpdate is { } previous && elapsed - previous < TimeSpan.FromMilliseconds(250)) return;
        _lastDownloadProgressUpdate = elapsed;
        var progress = _downloadProgressMeter.Sample(received, (long)total, elapsed);
        _transferSummary = progress.CompactText;
        Activity.Visibility = Visibility.Visible;
        Activity.IsIndeterminate = progress.Percent is null;
        if (progress.Percent is { } percent) Activity.Value = percent;
        SetStage(_primaryPackagePath is not null ? "Downloading Full reference (2 of 2)" :
            _requestedPackageKind == GngPackageKind.UpdateOnly ? "Downloading Update Only (1 of 2)" : "Downloading Full Package");
        StatusText.Text = progress.DetailText + (_primaryPackagePath is not null
            ? " · Reference copy for future cleanup comparisons. It will not be installed."
            : " · You will review the update before installing.");
        NotifyStateChanged();
    }

    private void Download_StateChanged(object? sender, object e)
    {
        if (_download is null || !ReferenceEquals(sender, _download) || _closed || _download.State == CoreWebView2DownloadState.InProgress) return;
        var completed = _download.State == CoreWebView2DownloadState.Completed;
        var path = _downloadPath;
        var generation = _downloadGeneration;
        var requestStopped = generation != _downloadRequest.Generation;
        var failure = _downloadFailure;
        var reason = completed ? "None" : _download.InterruptReason.ToString();
        var cancelled = requestStopped || !completed && _download.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled;
        Logger.Log("GNG", $"Download ended: completed={completed}; reason={reason}; bytes={_download.BytesReceived}; request={generation}.");
        if (!completed) DisarmDownloadRequest();
        _queuedPreview = completed && !requestStopped && path is not null;
        DetachDownload();
        UpdateControls();
        Activity.Visibility = Visibility.Collapsed;
        if (!_queuedPreview)
        {
            ShowIssue(cancelled ? "Download cancelled" : "Download stopped", failure ?? (_primaryPackagePath is not null
                ? (cancelled ? "Reference download cancelled. " : $"Reference download stopped ({reason}). ") + "Your Update Only ZIP is saved. Retry reference or import its matching Full ZIP; nothing has been installed."
                : cancelled ? "Download cancelled. Choose Retry download when ready." : $"The transfer stopped ({reason}). Retry the download, or use your browser and Import ZIP."));
            if (cancelled && failure is null)
            {
                _showingIssue = false;
                _preparationCancelled = true;
                UpdateControls();
            }
            return;
        }
        // Leave the WebView callback before disposing popup browsers or showing native UI.
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                if (_selectionInFlight && _selectionFinished is not null) await _selectionFinished.Task;
                _queuedPreview = false;
                if (_closed) return;
                ClosePopups();
                await HandleDownloadedPackageAsync(path!, generation);
            }
            finally { _queuedPreview = false; UpdateControls(); }
        }));
    }

    private async Task HandleDownloadedPackageAsync(string path, long generation)
    {
        if (generation != _downloadRequest.Generation) return;
        if (!_downloadRequest.MatchesSelectedFilename(path))
        {
            Logger.Log("GNG", "Completed download rejected: selected package identity mismatch.");
            DisarmDownloadRequest();
            ShowIssue("Downloaded package needs attention", "The downloaded filename could not be matched to the selected AeroNav package. Nothing was installed. Retry the download or import the intended ZIP.");
            UpdateControls();
            return;
        }
        if (_primaryPackagePath is not null)
        {
            DisarmDownloadRequest();
            await PreviewPackageAsync(_primaryPackagePath, path);
            return;
        }
        if (_requestedPackageKind != GngPackageKind.UpdateOnly)
        {
            DisarmDownloadRequest();
            await PreviewPackageAsync(path);
            return;
        }
        _preparingReference = true;
        string? referenceFromCache = null;
        SetBusy(true, "Checking the first download before continuing…", "Checking downloaded update (1 of 2)");
        try
        {
            var version = await Task.Run(() => GngUpdateService.ValidatePackage(path));
            if (_closed) return;
            // Stop can disarm this request while archive validation runs off the UI thread.
            if (generation != _downloadRequest.Generation)
            {
                ShowIssue("Download request stopped", "The ZIP is saved. Choose Download Swedish GNG when you are ready to prepare a new review. Nothing was installed.");
                return;
            }
            _primaryPackagePath = path;
            _referenceVersion = version;
            _requestedPackageKind = GngPackageKind.Full;
            PackageKindText.Text = "Update Only " + version + " is saved. Its matching Full Package will be kept for cleanup comparisons and will not be installed.";
            Logger.Log("GNG", "Primary archive validated: version=" + version + ".");
            if (_cachedFullReferencePath is { } cachedReference && _cachedFullReferenceVersion == version)
            {
                referenceFromCache = cachedReference;
                _cachedFullReferencePath = _cachedFullReferenceVersion = null;
                DisarmDownloadRequest();
            }
            else
            {
                if (_cachedFullReferencePath is not null) _usingSavedPackages = false;
                _cachedFullReferencePath = _cachedFullReferenceVersion = null;
                if (!_downloadRequest.TryBeginReference())
                {
                    ShowIssue("Download request stopped", "Update Only ZIP saved. Retry reference or import its matching Full ZIP to continue.");
                    return;
                }
                _selectedDocuments.Clear();
                _popupRefreshUsed = false;
                StatusText.Text = "Continuing the download (2 of 2)…";
                Logger.Log("GNG", "Requesting the matching Full reference.");
                await InitializeBrowserAsync();
            }
        }
        catch (Exception ex)
        {
            DisarmDownloadRequest();
            Logger.Log("GNG", "Primary archive validation failed: " + ex.GetType().Name + ".");
            ShowIssue("Downloaded update needs attention", "The ZIP downloaded, but its contents could not be validated. Nothing was installed. " + ex.Message);
        }
        finally { _preparingReference = false; SetBusy(false); }
        if (referenceFromCache is not null && !_closed) await PreviewPackageAsync(path, referenceFromCache);
    }

    private void DetachDownload()
    {
        _downloadProgressTimer.Stop();
        _downloadClock.Stop();
        _downloadProgressMeter.Reset();
        _lastDownloadProgressUpdate = null;
        _transferSummary = null;
        if (_download is not null)
        {
            _download.BytesReceivedChanged -= Download_ProgressChanged;
            _download.StateChanged -= Download_StateChanged;
        }
        _download = null;
        _downloadOwner = null;
        _downloadPath = null;
        _downloadFailure = null;
    }

    private async Task PreviewPackageAsync(string path, string? referencePath = null, bool allowTesting = false)
    {
        if (!CanGetPackage) return;
        CancelFilePreviewNavigation();
        PackageAlreadyMatches = false;
        _plan = null;
        _reviewPackagePath = path;
        _reviewReferencePath = referencePath;
        _reviewAllowTesting = allowTesting;
        PackageText.Text = Path.GetFileName(path);
        PackageSummaryText.Text = string.Empty;
        var preserveListLayout = PreserveListLayout.IsChecked == true;
        var keepDetailsOpen = _showingReviewDetails;
        InvalidateReviewAssessment();
        ReviewPanel.Visibility = Visibility.Collapsed;
        SetBusy(true, "Checking the update and your EuroScope files…", "Preparing installation review");
        try
        {
            var saamanPolicy = _saamanSettings is null ? null : SaamanProfileService.Capture(_saamanSettings, DataFolder);
            var plan = await Task.Run(() => GngUpdateService.BuildPlan(path, DataFolder, _protectedPaths, referencePath, preserveListLayout, saamanPolicy, allowTesting: allowTesting));
            if (_closed || _preparationCancelled) return;
            _plan = plan;
            _primaryPackagePath = _referenceVersion = null;
            PackageText.Text = (plan.IsTestingPackage ? "Testing package · " : "Swedish GNG ") + plan.Version;
            var changes = plan.Files.Count(file => file.WritesFile);
            PackageSummaryText.Text = string.IsNullOrWhiteSpace(_installedVersion) ? "New installation · " + plan.Version : _installedVersion + " → " + plan.Version;
            InstallButton.Content = plan.IsTestingPackage ? "Install testing package" : "Install update";
            SetPackageNotes(plan.Warnings, GngUpdateService.GetBackupRoot(DataFolder));
            ReviewSummaryText.Text = "Your sign-in details are kept. " +
                (plan.PreserveListLayout ? "List positions are kept where matched. " : "Lists will use the package defaults. ") +
                "A backup is created before changes.";
            SetReviewAssessment(GngReviewNotes.Assess(plan, _selectedProfile));
            ReviewSummaryText.ScrollToHome();
            SetReviewFiles(plan.Files);
            ShowReviewDetails(keepDetailsOpen);
            if (changes == 0)
            {
                PackageAlreadyMatches = true;
                CompletionRevision++;
                _plan = null;
                ShowCompletion("GNG already matches", "Your files already match Swedish GNG " + plan.Version + ". No installation is needed.", string.Empty);
                StatusText.Text = "No files were changed.";
                return;
            }
            BrowserPanel.Visibility = CompletePanel.Visibility = Visibility.Collapsed;
            ReviewPanel.Visibility = Visibility.Visible;
            _showingCompletion = false;
            StepText.Text = string.Empty;
            StatusText.Text = _usingSavedPackages ? "Saved package ready." : "Package ready.";
            SetStage("Ready to update");
            Logger.Log("GNG", "Installation preview prepared.");
            if (!string.Equals(_reportedPackagePath, path, StringComparison.OrdinalIgnoreCase))
            {
                _reportedPackagePath = path;
                ReportingEvents.Feature(ReportingFeature.GngPackageReview, ReportingApplication.Gng);
                ReportingService.RecordUsage(ReportingEventName.GngPackageObtained, new ReportingDetails
                { Application = ReportingApplication.Gng, Operation = ReportingOperation.Download, Source = _reportingPackageSource,
                    Outcome = ReportingOutcome.Completed, PackageVersion = ReportingEvents.PublicVersion(plan.Version) });
            }
        }
        catch (Exception ex)
        {
            Logger.Log("GNG", "Installation preview failed: " + ex.GetType().Name + ".");
            ReportingEvents.RecordFailure(ex, ReportingApplication.Gng, ReportingOperation.Update);
            InvalidateReviewAssessment(ex.Message);
            SetReviewFiles([]);
            NoFileChangesText.Text = "The file comparison is unavailable until the update checks succeed.";
            BrowserPanel.Visibility = CompletePanel.Visibility = Visibility.Collapsed;
            ReviewPanel.Visibility = Visibility.Visible;
            ShowReviewDetails(keepDetailsOpen);
            ShowIssue("Update needs attention", "The package could not be prepared. Your data folder is unchanged. " + ex.Message +
                (_primaryPackagePath is null ? "" : " Your Update Only ZIP is saved. Retry reference, import a matching Full ZIP, or Start over to download both again."));
        }
        finally { SetBusy(false); }
    }

    private async void PreserveListLayout_Changed(object sender, RoutedEventArgs e)
    {
        // Checked also fires while InitializeComponent is still creating the controls.
        if (PreserveListLayout is null || !CanGetPackage || _showingCompletion) return;
        if (_reviewPackagePath is { } path)
        {
            _preparationCancelled = _showingIssue = false;
            await PreviewPackageAsync(path, _reviewReferencePath, _reviewAllowTesting);
        }
    }

    private async void RefreshReview_Click(object sender, RoutedEventArgs e)
    {
        if (!CanGetPackage || _reviewPackagePath is not { } path) return;
        _preparationCancelled = _showingIssue = false;
        await PreviewPackageAsync(path, _reviewReferencePath, _reviewAllowTesting);
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        RefreshEuroScopeStatus();
        if (!CanGetPackage || _plan is null || !_plan.Files.Any(file => file.WritesFile) || _euroScopeBlockingReason is not null) return;
        var plan = _plan;
        _writing = true;
        SetBusy(true, "Checking files and preparing the backup…", "Updating Swedish GNG");
        ReportingEvents.Start(ReportingApplication.Gng, ReportingOperation.Install);
        ClosePopups();
        var progress = new Progress<string>(message => { if (_busy) StatusText.Text = message; });
        try
        {
            AttemptedChanges = true;
            var result = await Task.Run(() => GngUpdateService.Install(plan, progress));
            ActivityHistoryService.Record(ActivityApplication.Gng, ActivityOperation.Install, ActivityOutcome.Completed,
                plan.Version, result.BackupFolder, DataFolder);
            InstalledPackageVersion = plan.Version;
            InstallationCompleted = true;
            CompletionRevision++;
            _newCompleteZip = result.CompletePackagePath;
            _oldCompleteZip = result.PreviousCompletePackagePath;
            ShowCompletion("Swedish GNG is updated", $"Version {plan.Version} is installed.", result.BackupFolder);
            await PrepareCleanupSummaryAsync();
            StatusText.Text = "Update complete.";
        }
        catch (Exception ex)
        {
            _plan = null;
                ShowIssue("GNG update stopped", "The update could not finish. " + ex.Message);
            ShowPendingRecovery("Installation stopped: " + ex.Message);
            ReportingEvents.RecordFailure(ex, ReportingApplication.Gng, ReportingOperation.Install);
            ActivityHistoryService.Record(ActivityApplication.Gng, ActivityOperation.Install, ActivityOutcome.Stopped,
                plan.Version, _pendingBackup, DataFolder);
        }
        finally { _writing = false; MutationRevision++; SetBusy(false); }
    }

    private async Task PrepareCleanupSummaryAsync()
    {
        CleanupButton.Visibility = Visibility.Collapsed;
        CleanupHint.Text = string.Empty;
        if (_oldCompleteZip is not { } oldZip || _newCompleteZip is not { } newZip) return;
        StatusText.Text = "Checking whether old package files can be archived…";
        try
        {
            var paths = _saamanSettings is null ? _cleanupProtectedPaths :
                _cleanupProtectedPaths.Concat(SaamanProfileService.GetProtectedPaths(_saamanSettings)).ToArray();
            var comparison = await Task.Run(() => GngCleanupService.BuildPlan(DataFolder, oldZip, newZip, _selectedProfile, paths));
            int count = comparison.Candidates.Count(file => file.IsEligible);
            CleanupHint.Text = count == 0 ? "No old files need cleanup." :
                $"{count:N0} old {(count == 1 ? "file can" : "files can")} be archived. Cleanup is optional.";
            CleanupButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Logger.Log("GNG", "Optional cleanup comparison failed: " + ex.GetType().Name + ".");
            CleanupHint.Text = "Cleanup could not be checked. You can retry from Setup.";
        }
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        RefreshEuroScopeStatus();
        if (!Idle || _selectionInFlight || _euroScopeBlockingReason is not null) return;
        DisarmDownloadRequest(closeBrowsers: true);
        var backupFolder = _pendingBackup ?? _lastBackupFolder ?? _suggestedRecoveryFolder;
        if (string.IsNullOrWhiteSpace(backupFolder))
        {
            var picker = new OpenFolderDialog { Title = "Choose a GNG installation backup" };
            try { var root = GngUpdateService.GetBackupRoot(DataFolder); if (Directory.Exists(root)) picker.InitialDirectory = root; }
            catch (Exception ex) { ShowIssue("Recovery unavailable", ex.Message); return; }
            if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
            backupFolder = picker.FolderName;
        }
        _reviewedRecoveryFolder = backupFolder;
        ShowCompletion("Restore GNG backup", "Files edited since the update are kept for review. EuroScope must be closed.", backupFolder);
        RestoreConfirmText.Text = "Restore to " + DataFolder;
        RestoreConfirmPanel.Visibility = Visibility.Visible;
        ReportingEvents.Feature(ReportingFeature.GngRestoreReview, ReportingApplication.Gng);
        NotifyStateChanged();
    }

    private void CancelRestore_Click(object sender, RoutedEventArgs e)
    {
        _reviewedRecoveryFolder = null;
        RestoreConfirmPanel.Visibility = Visibility.Collapsed;
        NotifyStateChanged();
    }

    private async void ConfirmRestore_Click(object sender, RoutedEventArgs e)
    {
        if (!Idle || _reviewedRecoveryFolder is not { } backupFolder) return;
        RefreshEuroScopeStatus();
        if (_euroScopeBlockingReason is not null) return;
        _primaryPackagePath = _referenceVersion = null;
        _writing = true;
        RestoreConfirmPanel.Visibility = Visibility.Collapsed;
        SetBusy(true, "Checking the backup and restoring files…", "Restoring backup");
        ReportingEvents.Start(ReportingApplication.Gng, ReportingOperation.Restore);
        ClosePopups();
        var progress = new Progress<string>(message => { if (_busy) StatusText.Text = message; });
        try
        {
            AttemptedChanges = true;
            // A recovery attempt may undo an installation before failing or leaving review items.
            InstallationCompleted = false;
            _newCompleteZip = _oldCompleteZip = null;
            CleanupButton.Visibility = Visibility.Collapsed;
            var result = await Task.Run(() => GngUpdateService.Restore(backupFolder, DataFolder, _protectedPaths, progress));
            ActivityHistoryService.Record(ActivityApplication.Gng, ActivityOperation.Restore,
                result.SkippedFiles.Count == 0 ? ActivityOutcome.Completed : ActivityOutcome.NeedsAttention,
                recoveryFolder: result.BackupFolder, dataFolder: DataFolder);
            _recoveryRequired = result.SkippedFiles.Count > 0;
            _pendingBackup = _recoveryRequired ? result.BackupFolder : null;
            var details = $"{result.InstalledFileCount:N0} files restored.";
            if (result.SkippedFiles.Count > 0)
                details += $" {result.SkippedFiles.Count:N0} files need attention. Recovery remains incomplete. Preserve changed files outside the data folder, resolve the items below, then restore again.\n\n" + string.Join(Environment.NewLine, result.SkippedFiles);
            ShowCompletion(result.SkippedFiles.Count == 0 ? "Backup restored" : "Recovery needs attention", details, result.BackupFolder);
            StatusText.Text = result.SkippedFiles.Count == 0 ? "Restore finished." : "Keep the recovery backup and resolve the listed files before using EuroScope.";
        }
        catch (Exception ex)
        {
            ShowCompletion("Restore stopped", "Keep the backup and resolve this problem before trying recovery again. " + ex.Message, backupFolder);
            StatusText.Text = "Restore did not complete. Review the recovery details above before using EuroScope.";
            ShowPendingRecovery("Restore stopped: " + ex.Message);
            _showingIssue = true;
            ReportingEvents.RecordFailure(ex, ReportingApplication.Gng, ReportingOperation.Restore);
            ActivityHistoryService.Record(ActivityApplication.Gng, ActivityOperation.Restore, ActivityOutcome.Stopped,
                recoveryFolder: backupFolder, dataFolder: DataFolder);
        }
        finally { _writing = false; MutationRevision++; SetBusy(false); }
    }

    private void ShowCompletion(string title, string text, string backup)
    {
        OtherOptions.IsExpanded = false;
        _reviewAssessment = null;
        _reviewIsCurrent = false;
        _showingReviewDetails = false;
        _showingIssue = false;
        CompletionChecks.Visibility = CheckFontsButton.Visibility = CheckProfileButton.Visibility = Visibility.Collapsed;
        SetStage(title);
        _plan = null;
        _reviewPackagePath = _reviewReferencePath = null;
        _showingCompletion = true;
        BrowserPanel.Visibility = ReviewPanel.Visibility = Visibility.Collapsed;
        CompletePanel.Visibility = Visibility.Visible;
        CompleteTitle.Text = title;
        CompleteText.Text = text;
        CompleteText.ScrollToHome();
        _lastBackupFolder = string.IsNullOrWhiteSpace(backup) ? null : backup;
        BackupText.Text = backup;
        RecoveryDetails.Visibility = string.IsNullOrWhiteSpace(backup) ? Visibility.Collapsed : Visibility.Visible;
        RecoveryDetails.IsExpanded = _recoveryRequired || title == "Review GNG recovery";
        RestoreConfirmPanel.Visibility = Visibility.Collapsed;
        CleanupButton.Visibility = Visibility.Collapsed;
        CleanupHint.Text = string.Empty;
        StepText.Text = string.Empty;
        BackupText.Visibility = string.IsNullOrWhiteSpace(backup) ? Visibility.Collapsed : Visibility.Visible;
        CloseButton.Content = "Done";
        UpdateControls();
    }

    private void Cleanup_Click(object sender, RoutedEventArgs e)
    {
        if (!Idle || _newCompleteZip is null) return;
        try
        {
            GngUpdateService.RequireNoPendingUpdate(DataFolder);
            var cleanupPaths = _saamanSettings is null ? _cleanupProtectedPaths :
                _cleanupProtectedPaths.Concat(SaamanProfileService.GetProtectedPaths(_saamanSettings)).ToArray();
            var cleanup = new GngCleanupControl(DataFolder, _selectedProfile, cleanupPaths, _oldCompleteZip, _newCompleteZip);
            _cleanupControl = cleanup;
            _cleanupOpen = true;
            cleanup.StateChanged += NotifyStateChanged;
            cleanup.CloseRequested += (_, _) =>
            {
                if (!cleanup.CanDismiss) return;
                AttemptedChanges |= cleanup.AttemptedChanges;
                if (cleanup.AttemptedChanges) MutationRevision++;
                cleanup.StateChanged -= NotifyStateChanged;
                _cleanupControl = null;
                _cleanupOpen = false;
                CloseNestedView();
            };
            ShowNestedView(cleanup);
        }
        catch (Exception ex) { _cleanupOpen = false; ShowIssue("Cleanup unavailable", ex.Message); ShowPendingRecovery(); }
        UpdateControls();
    }

    private void ShowNestedView(UIElement content)
    {
        NestedContent.Content = content;
        OperationBody.Visibility = Visibility.Collapsed;
        NestedContent.Visibility = Visibility.Visible;
        NotifyStateChanged();
    }

    private void CloseNestedView()
    {
        NestedContent.Content = null;
        NestedContent.Visibility = Visibility.Collapsed;
        OperationBody.Visibility = Visibility.Visible;
        UpdateControls();
    }

    private void SetStage(string title)
    {
        DownloadStageText.Text = title switch
        {
            "Checking saved packages" or "Preparing installation review" or "Checking downloaded update (1 of 2)" or "Checking reviewed package" => "Checking update",
            "Preparing download" or "Finding your package" or "Starting package download" => "Starting download",
            "Getting Full reference (2 of 2)" or "Starting Full reference (2 of 2)" => "Starting reference copy",
            "Downloading Full reference (2 of 2)" => "Reference 2/2",
            "Downloading Update Only (1 of 2)" => "Download 1/2",
            "Downloading Full Package" => "Downloading update",
            "Updating Swedish GNG" => "Installing update",
            _ => title
        };
        DownloadStageText.Visibility = string.IsNullOrWhiteSpace(title) ? Visibility.Collapsed : Visibility.Visible;
        NotifyStateChanged();
    }

    private void ShowIssue(string title, string details)
    {
        _showingIssue = true;
        SetStage(title);
        StatusText.Text = details;
        StatusText.ScrollToHome();
    }

    private void ShowPreparationStopped()
    {
        _showingIssue = false;
        SetStage("GNG preparation stopped");
        StatusText.Text = "Nothing was installed. Retry when you are ready.";
    }

    private void SetBusy(bool busy, string? message = null, string? stage = null)
    {
        _busy = busy;
        if (busy) SetStage(stage ?? "Working…");
        if (message is not null) StatusText.Text = message;
        Activity.IsIndeterminate = busy;
        Activity.Visibility = busy || _download is not null ? Visibility.Visible : Visibility.Collapsed;
        UpdateControls();
        if (!busy && _downloadRequest.Phase == GngDownloadRequestPhase.WaitingForPage)
            QueueReadyPackagePages();
    }

    private void UpdateControls()
    {
        if (_closed) return;
        StartBrowserButton.IsEnabled = RuntimeInstallButton.IsEnabled = ImportButton.IsEnabled = ChooseAnotherButton.IsEnabled = CanGetPackage;
        StartBrowserButton.IsEnabled = CanGetPackage && !_downloadRequest.IsPending;
        FreshDownloadButton.IsEnabled = CanGetPackage && !_downloadRequest.IsPending;
        PreserveListLayout.IsEnabled = CanGetPackage && !_showingCompletion && !_downloadRequest.IsPending;
        FilesGrid.IsEnabled = FileSearchBox.IsEnabled = FileFilterBox.IsEnabled = CanGetPackage;
        CleanupButton.IsEnabled = CanGetPackage && _newCompleteZip is not null;
        CloseButton.IsEnabled = CanDismiss;
        ExternalBrowserButton.IsEnabled = RuntimeInfoButton.IsEnabled = CanGetPackage;
        bool reviewing = ReviewPanel.Visibility == Visibility.Visible;
        ViewDetailsButton.Visibility = reviewing && !_showingReviewDetails ? Visibility.Visible : Visibility.Collapsed;
        OtherOptions.Visibility = reviewing && _showingReviewDetails && OptionsTab.IsSelected || BrowserPanel.Visibility == Visibility.Visible && (StartPanel.Visibility == Visibility.Visible || _showingIssue)
            ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.Content = _showingCompletion ? "Done" : "Cancel";
        InstallButton.Visibility = reviewing && _plan is not null ? Visibility.Visible : Visibility.Collapsed;
        RefreshReviewButton.Visibility = _reviewPackagePath is not null && _plan is null && !_busy ? Visibility.Visible : Visibility.Collapsed;
        RefreshReviewButton.IsEnabled = CanGetPackage;
        DownloadActivityPanel.Visibility = !_busy && !_showingIssue && !_preparationCancelled &&
            (ReviewPanel.Visibility == Visibility.Visible || CompletePanel.Visibility == Visibility.Visible)
            ? Visibility.Collapsed : Visibility.Visible;
        UpdateEuroScopeControls(CanGetPackage && _plan is not null, Idle && !_selectionInFlight);
        CancelDownloadButton.Visibility = _download is not null ? Visibility.Visible : Visibility.Collapsed;
        var core = _browser?.CoreWebView2;
        BackButton.IsEnabled = CanGetPackage && core?.CanGoBack == true;
        ReloadButton.IsEnabled = HomeButton.IsEnabled = CanGetPackage && core is not null;
        RetryDownloadButton.IsEnabled = CanGetPackage && !_downloadRequest.IsPending;
        StopRequestButton.IsEnabled = _startingBrowser && !_preparationCancelled || Idle && _downloadRequest.IsPending ||
            _preparingReference && _downloadRequest.Phase == GngDownloadRequestPhase.Downloading;
        StopRequestButton.Visibility = StopRequestButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        StartBrowserButton.Content = _primaryPackagePath is null ? "Download Swedish GNG" : "Retry reference download";
        RetryDownloadButton.Content = _primaryPackagePath is null ? "Retry download" : "Retry reference";
        ImportButton.Content = _primaryPackagePath is null ? "Import ZIP…" : "Import reference ZIP…";
        StartOverButton.Visibility = _primaryPackagePath is null ? Visibility.Collapsed : Visibility.Visible;
        StartOverButton.IsEnabled = CanGetPackage;
        UpdateBrowserInteraction();
        UpdateReviewContentLayout();
        NotifyStateChanged();
    }

    private void UpdateBrowserInteraction()
    {
        var blocked = _download is not null || _queuedPreview ||
            _busy && !_startingBrowser && BrowserToolbar.Visibility == Visibility.Visible && StartPanel.Visibility != Visibility.Visible;
        var justBlocked = blocked && !_browserInteractionBlocked;
        _browserInteractionBlocked = blocked;
        SetBrowserInteractionBlocked(blocked);
        foreach (var (window, browser) in _popups.ToArray())
        {
            browser.IsEnabled = browser.IsHitTestVisible = browser.Focusable = !blocked;
            if (blocked && window.IsVisible)
            {
                // Keep the browser alive if it owns the transfer, but expose the main progress/cancel controls.
                _downloadHiddenPopups.Add(window);
                window.Hide();
            }
            else if (!blocked && _downloadHiddenPopups.Remove(window)) window.Show();
        }
        if (justBlocked)
        {
            if (CancelDownloadButton.Visibility == Visibility.Visible) CancelDownloadButton.Focus();
            else if (StopRequestButton.Visibility == Visibility.Visible) StopRequestButton.Focus();
            else StatusText.Focus();
        }
    }

    private void ShowBrowserFallback(string message, bool runtimeMissing = false)
    {
        if (_closed) return;
        _showingIssue = true;
        OtherOptions.IsExpanded = true;
        DisarmDownloadRequest();
        BrowserToolbar.Visibility = Visibility.Collapsed;
        StartPanel.Visibility = Visibility.Visible;
        StartHeading.Text = "Continue here or in your browser";
        SetStage(runtimeMissing ? "Browser runtime required" : "Download needs attention");
        StartExplanation.Text = message;
        StartBrowserButton.Visibility = runtimeMissing ? Visibility.Collapsed : Visibility.Visible;
        FreshDownloadButton.Visibility = StartBrowserButton.Visibility;
        RuntimeInstallButton.Visibility = runtimeMissing ? Visibility.Visible : Visibility.Collapsed;
        RuntimeInfoButton.Visibility = Visibility.Visible;
        StatusText.Text = "ZIP import is available even when the embedded browser is unavailable.";
        UpdateControls();
    }

    private async void Import_Click(object sender, RoutedEventArgs e) => await ImportAsync();
    public async Task ImportAsync()
    {
        if (!CanGetPackage) return;
        _preparationCancelled = false;
        _showingIssue = false;
        DisarmDownloadRequest(closeBrowsers: true);
        var dialog = new OpenFileDialog { Title = _primaryPackagePath is null ? "Import an original ESAA GNG ZIP" : "Import the matching Swedish Full Package reference ZIP", Filter = "GNG ZIP packages (*.zip)|*.zip", CheckFileExists = true, Multiselect = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            _reportingPackageSource = ReportingSource.Import;
            _cachedFullReferencePath = _cachedFullReferenceVersion = null;
            if (_primaryPackagePath is null) _usingSavedPackages = false;
            if (_primaryPackagePath is null) await PreviewPackageAsync(dialog.FileName, allowTesting: true);
            else await PreviewPackageAsync(_primaryPackagePath, dialog.FileName);
        }
    }

    private void ChooseAnother_Click(object sender, RoutedEventArgs e)
    {
        if (!CanGetPackage) return;
        _preparationCancelled = _showingIssue = false;
        _showingReviewDetails = false;
        DisarmDownloadRequest(closeBrowsers: true);
        _primaryPackagePath = _referenceVersion = null;
        _plan = null;
        _reviewPackagePath = _reviewReferencePath = null;
        _cachedFullReferencePath = _cachedFullReferenceVersion = null;
        _usingSavedPackages = false;
        _forceFreshCopies = false;
        _showingCompletion = false;
        BrowserPanel.Visibility = Visibility.Visible;
        StartPanel.Visibility = Visibility.Visible;
        BrowserToolbar.Visibility = Visibility.Collapsed;
        StartHeading.Text = "Get Swedish GNG";
        StartExplanation.Text = "Use the latest package from AeroNav. Saved copies are reused; sign in only if needed.";
        StartOptions.IsExpanded = OtherOptions.IsExpanded = false;
        UpdatePackageKind();
        SetStage("");
        ReviewPanel.Visibility = CompletePanel.Visibility = Visibility.Collapsed;
        StepText.Text = "1  Get a package   →   2  Review and install   →   3  Optional cleanup";
        CloseButton.Content = "Cancel";
        UpdateControls();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        var browser = _browser;
        if (CanGetPackage && browser?.CoreWebView2 is { CanGoBack: true } core)
        {
            _navigations[browser].ExpectNavigation();
            core.GoBack();
        }
    }
    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        var browser = _browser;
        if (CanGetPackage && browser?.CoreWebView2 is { } core)
        {
            _navigations[browser].ExpectNavigation();
            core.Reload();
        }
    }
    private void Home_Click(object sender, RoutedEventArgs e)
    {
        var browser = _browser;
        if (CanGetPackage && browser?.CoreWebView2 is { } core)
        {
            _navigations[browser].ExpectNavigation();
            core.Navigate(GngBrowserPolicy.AeroNavHome);
        }
    }
    private async void RetryDownload_Click(object sender, RoutedEventArgs e)
    {
        if (CanGetPackage && !_downloadRequest.IsPending) await StartDownloadSafelyAsync(_forceFreshCopies);
    }
    private void StartOver_Click(object sender, RoutedEventArgs e) => ChooseAnother_Click(sender, e);
    private void StopRequest_Click(object sender, RoutedEventArgs e)
    {
        if (_startingBrowser) { CancelPreparation(); return; }
        if (!(Idle && _downloadRequest.IsPending) &&
            !(_preparingReference && _downloadRequest.Phase == GngDownloadRequestPhase.Downloading)) return;
        DisarmDownloadRequest(closeBrowsers: true);
        ShowBrowserFallback(_primaryPackagePath is null
            ? "Download request stopped. Choose Download Swedish GNG when you are ready to try again."
            : "Reference request stopped. Your Update Only ZIP is saved; retry reference, import its matching Full ZIP, or Start over.");
        StatusText.Text = "Request stopped. Nothing was installed.";
        SetStage("Download request stopped");
    }
    private void CancelDownload_Click(object sender, RoutedEventArgs e)
    {
        DisarmDownloadRequest();
        try { _download?.Cancel(); }
        catch (Exception)
        {
            DetachDownload();
            ClosePopups();
            ResetBrowser();
            ShowBrowserFallback("The browser stopped before cancellation could be confirmed. Sign in again or import a complete ZIP.");
            Activity.Visibility = Visibility.Collapsed;
            StatusText.Text = "The browser was closed. No package was installed.";
            UpdateControls();
        }
    }
    private void Close_Click(object sender, RoutedEventArgs e) { if (CanDismiss) Dismissed?.Invoke(); }
    private void ExternalBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (!CanGetPackage) return;
        DisarmDownloadRequest(closeBrowsers: true);
        ShowBrowserFallback("Download the Swedish package in your normal browser, then choose Import ZIP here.");
        OpenFixedWebsite(GngBrowserPolicy.AeroNavHome);
    }
    private void RuntimeInfo_Click(object sender, RoutedEventArgs e) { if (Idle) OpenFixedWebsite(RuntimeInformation); }
    private void OpenFixedWebsite(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { StatusText.Text = "The browser could not open. Visit files.aero-nav.com/ESAA in your normal browser."; }
    }

    private void ClosePopups() { foreach (var popup in _popups.Keys.ToArray()) popup.Close(); }
    public void Dispose()
    {
        if (_closed) return;
        if (!CanDismiss) throw new InvalidOperationException("Finish or cancel the GNG operation before disposing its view.");
        CancelFilePreviewNavigation();
        _closed = true;
        _preparationCancellation?.Dispose();
        _preparationCancellation = null;
        _euroScopeTimer.Stop();
        DisarmDownloadRequest();
        DetachDownload();
        ClosePopups();
        ResetBrowser();
    }
}
