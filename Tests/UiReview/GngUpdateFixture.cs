using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

internal enum GngUpdateFixtureState { Start, FreshStart, CheckingSavedPackages, WaitingForLogin, Downloading, PreparingReference, ReferenceWaitingForLogin, DownloadingReference, PreparingPackages, DownloadCancelled, ReferenceCancelled, DownloadFailed, ReferenceFailed, ListLayoutFailed, Review, CachedReview, TestingReview, AttentionReview, RunningReview, Completion, RuntimeFallback }

// Link production XAML and the presentation/navigation partials. BrowserHost stays empty: no WebView2 assembly,
// production download/installer service, native dialog or browser is used here.
public partial class GngUpdateControl : UserControl, IDisposable
{
    private const string SyntheticBackupPath = @"C:\Synthetic\Controller applications and recovery exports\Launchpad GNG installation backups\Sweden FIR package installation with a deliberately long folder name\2026-10-07-synthetic-backup";
    private const string SyntheticFailure = "The download stopped: the network connection was interrupted (NetworkDisconnected). Nothing was installed. Retry the download, or use your normal browser and import the original ZIP.\n" +
        "Synthetic long diagnostic: the transfer ended before the package could be verified. This example deliberately exercises wrapped, selectable error details at minimum window size. No browser, network request, disk write or credential access occurred.\n" +
        "Synthetic download location: C:\\Synthetic\\Controller applications and recovery exports\\Launchpad downloads\\Sweden FIR package with a deliberately long directory name\\ESAA-Update-Only_20261001120000-261001-0003.zip\n" +
        "End of synthetic details: retry remains an explicit action.";
    public event Action? StateChanged;
    public event Action? Dismissed;
    public event Action<string>? FollowUpRequested;
    public bool AttemptedChanges { get; private set; }
    public bool InstallationCompleted { get; private set; }
    public bool PackageAlreadyMatches { get; private set; }
    public bool RestartRequired => false;
    public int MutationRevision { get; private set; }
    public string DataFolder => @"C:\Users\Synthetic Controller\AppData\Roaming\EuroScope";
    public string? InstalledPackageVersion => InstallationCompleted ? "2610/01 rev.3" : null;
    public bool HasError => _showingIssue || SyntheticState is GngUpdateFixtureState.DownloadFailed or GngUpdateFixtureState.ReferenceFailed or GngUpdateFixtureState.ListLayoutFailed;
    public bool IsBusy => _syntheticPreviewBusy || _readingFilePreview || SyntheticState is GngUpdateFixtureState.CheckingSavedPackages or GngUpdateFixtureState.Downloading or GngUpdateFixtureState.DownloadingReference or GngUpdateFixtureState.PreparingReference or GngUpdateFixtureState.PreparingPackages;
    public bool CanDismiss => !IsBusy;
    public bool CanCancel => IsBusy || SyntheticState is GngUpdateFixtureState.WaitingForLogin or GngUpdateFixtureState.ReferenceWaitingForLogin;
    public bool NeedsAttentionView => _readingFilePreview || NestedContent.Content is not null || !IsBusy;
    public string Status => StatusText.Text;
    public string Stage => DownloadStageText.Text;
    public double Progress => Activity.Value;
    public bool ProgressIndeterminate => Activity.IsIndeterminate;
    public Task StartAsync(bool forceDownload = false) { if(forceDownload) FreshDownload_Click(this,new()); else StartBrowser_Click(this,new()); return Task.CompletedTask; }
    public Task ImportPackageAsync() { Import_Click(this,new()); return Task.CompletedTask; }
    public Task DownloadFreshAsync() { FreshDownload_Click(this,new()); return Task.CompletedTask; }
    public void CancelPreparation() { if(CanCancel) SetSyntheticState(GngUpdateFixtureState.DownloadCancelled); }
    public void Dispose() { CancelFilePreviewNavigation(); _closed = true; }
    public void SetCompletionChecks(string message, bool fontsNeedAction, bool profileNeedsReview)
    {
        CompletionChecks.Text = message;
        CompletionChecks.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        CheckFontsButton.Visibility = fontsNeedAction ? Visibility.Visible : Visibility.Collapsed;
        CheckProfileButton.Visibility = profileNeedsReview ? Visibility.Visible : Visibility.Collapsed;
    }
    private readonly TextBlock _browserPlaceholder;
    private bool _ready;
    private bool _syntheticNeedsFull;
    internal GngUpdateFixtureState SyntheticState { get; private set; }
    internal bool SyntheticCacheBypassed { get; private set; }

    public GngUpdateControl()
    {
        InitializeComponent();
        _browserPlaceholder = new TextBlock
        {
            Text = "SYNTHETIC WEBSITE AREA\nThe browser is omitted from this layout fixture.\nNo sign-in, website or download is started.",
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(24)
        };
        _browserPlaceholder.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
        BrowserSurface.Children.Add(_browserPlaceholder);
        _ready = true;
        RefreshSyntheticScenario();
    }

    internal void RefreshSyntheticScenario() => SetSyntheticState(UiFixture.Blank ? GngUpdateFixtureState.FreshStart : GngUpdateFixtureState.Start);

    internal void SetSyntheticState(GngUpdateFixtureState state)
    {
        if (!_ready) return;
        SyntheticState = state;
        PackageAlreadyMatches = false;
        if (state is GngUpdateFixtureState.Start or GngUpdateFixtureState.FreshStart)
            _syntheticNeedsFull = state == GngUpdateFixtureState.FreshStart;
        var downloading = state is GngUpdateFixtureState.Downloading or GngUpdateFixtureState.DownloadingReference;
        var preparing = state is GngUpdateFixtureState.PreparingReference or GngUpdateFixtureState.PreparingPackages;
        var checkingCache = state == GngUpdateFixtureState.CheckingSavedPackages;
        var review = state == GngUpdateFixtureState.ListLayoutFailed || state is GngUpdateFixtureState.Review or GngUpdateFixtureState.CachedReview or GngUpdateFixtureState.TestingReview or GngUpdateFixtureState.AttentionReview or GngUpdateFixtureState.RunningReview;
        var waitingForLogin = state is GngUpdateFixtureState.WaitingForLogin or GngUpdateFixtureState.ReferenceWaitingForLogin;
        var failed = state is GngUpdateFixtureState.DownloadFailed or GngUpdateFixtureState.ReferenceFailed or GngUpdateFixtureState.ListLayoutFailed;
        var reference = state is GngUpdateFixtureState.DownloadingReference or GngUpdateFixtureState.ReferenceWaitingForLogin or GngUpdateFixtureState.ReferenceCancelled or GngUpdateFixtureState.ReferenceFailed;
        var browsing = downloading || preparing || waitingForLogin || state is GngUpdateFixtureState.DownloadCancelled or GngUpdateFixtureState.ReferenceCancelled or GngUpdateFixtureState.DownloadFailed or GngUpdateFixtureState.ReferenceFailed;
        FolderText.Text = DataFolder;
        BrowserHost.Content = null;
        BrowserPanel.Visibility = browsing || checkingCache || state is GngUpdateFixtureState.Start or GngUpdateFixtureState.FreshStart or GngUpdateFixtureState.RuntimeFallback ? Visibility.Visible : Visibility.Collapsed;
        StartPanel.Visibility = browsing ? Visibility.Collapsed : Visibility.Visible;
        BrowserToolbar.Visibility = _browserPlaceholder.Visibility = browsing ? Visibility.Visible : Visibility.Collapsed;
        BackButton.IsEnabled = ReloadButton.IsEnabled = HomeButton.IsEnabled = browsing && !downloading && !preparing;
        RetryDownloadButton.IsEnabled = state is GngUpdateFixtureState.DownloadCancelled or GngUpdateFixtureState.ReferenceCancelled or GngUpdateFixtureState.DownloadFailed or GngUpdateFixtureState.ReferenceFailed;
        RetryDownloadButton.Content = reference ? "Retry reference" : "Retry download";
        StopRequestButton.IsEnabled = state == GngUpdateFixtureState.PreparingReference || waitingForLogin;
        StopRequestButton.Visibility = StopRequestButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        ImportButton.IsEnabled = ExternalBrowserButton.IsEnabled = RestoreButton.IsEnabled = CloseButton.IsEnabled = !downloading && !preparing && !checkingCache;
        PreserveListLayout.IsEnabled = !downloading && !preparing && !checkingCache && !waitingForLogin && state != GngUpdateFixtureState.Completion;
        ImportButton.Content = reference ? "Import reference ZIP…" : "Import ZIP…";
        StartOverButton.Visibility = reference ? Visibility.Visible : Visibility.Collapsed;
        StartOverButton.IsEnabled = reference && !downloading;
        ReviewPanel.Visibility = review ? Visibility.Visible : Visibility.Collapsed;
        CompletePanel.Visibility = state == GngUpdateFixtureState.Completion ? Visibility.Visible : Visibility.Collapsed;
        DownloadActivityPanel.Visibility = review || state == GngUpdateFixtureState.Completion ? Visibility.Collapsed : Visibility.Visible;
        RefreshReviewButton.Visibility = Visibility.Collapsed;
        RuntimeInstallButton.Visibility = RuntimeInfoButton.Visibility = state == GngUpdateFixtureState.RuntimeFallback ? Visibility.Visible : Visibility.Collapsed;
        OtherOptions.IsExpanded = state == GngUpdateFixtureState.RuntimeFallback;
        RecoveryDetails.Visibility = Visibility.Visible;
        RestoreConfirmPanel.Visibility = Visibility.Collapsed;
        CompletionChecks.Visibility = CheckFontsButton.Visibility = CheckProfileButton.Visibility = Visibility.Collapsed;
        StartBrowserButton.Visibility = FreshDownloadButton.Visibility = state == GngUpdateFixtureState.RuntimeFallback ? Visibility.Collapsed : Visibility.Visible;
        StartBrowserButton.IsEnabled = FreshDownloadButton.IsEnabled = !downloading && !preparing && !checkingCache && !waitingForLogin;
        StartBrowserButton.Content = reference ? "Retry reference download" : "Download Swedish GNG";
        InstallButton.Visibility = review ? Visibility.Visible : Visibility.Collapsed;
        InstallButton.IsEnabled = false;
        CancelDownloadButton.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        Activity.Visibility = downloading || preparing || checkingCache ? Visibility.Visible : Visibility.Collapsed;
        Activity.IsIndeterminate = preparing || checkingCache;
        Activity.Value = downloading ? 42 : 0;
        DownloadStageText.Visibility = downloading || preparing || checkingCache || failed || waitingForLogin || review ? Visibility.Visible : Visibility.Collapsed;
        DownloadStageText.Text = state switch
        {
            GngUpdateFixtureState.WaitingForLogin => "Waiting for sign-in",
            GngUpdateFixtureState.CheckingSavedPackages => "Checking saved packages",
            GngUpdateFixtureState.ReferenceWaitingForLogin => "Sign in for Full reference",
            GngUpdateFixtureState.Downloading => "Downloading update (1 of 2)",
            GngUpdateFixtureState.PreparingReference => "Checking downloaded update",
            GngUpdateFixtureState.DownloadingReference => "Downloading Full reference (2 of 2)",
            GngUpdateFixtureState.PreparingPackages => "Checking packages",
            GngUpdateFixtureState.DownloadFailed => "Download stopped",
            GngUpdateFixtureState.ReferenceFailed => "Needs attention",
            GngUpdateFixtureState.ListLayoutFailed => "Installation review needs attention",
            GngUpdateFixtureState.Review or GngUpdateFixtureState.CachedReview or GngUpdateFixtureState.TestingReview or GngUpdateFixtureState.AttentionReview or GngUpdateFixtureState.RunningReview => "Ready to update",
            _ => string.Empty
        };
        CleanupButton.Visibility = state == GngUpdateFixtureState.Completion ? Visibility.Visible : Visibility.Collapsed;
        OriginText.Text = browsing ? "https://files.aero-nav.com" : "Browser not started (synthetic)";
        StartHeading.Text = state == GngUpdateFixtureState.RuntimeFallback ? "The embedded browser needs WebView2" : "Get Swedish GNG";
        PackageKindText.Text = reference
            ? "Update Only 2610/01 rev.3 is saved. Its matching Full Package will be kept for cleanup comparisons and will not be installed."
            : _syntheticNeedsFull
            ? "A complete Swedish GNG installation was not found — Full Package will be used for setup or repair."
            : "Existing Swedish GNG found — Update Only will be installed after review. A matching Full Package will also be kept for cleanup comparisons.";
        StartExplanation.Text = state == GngUpdateFixtureState.RuntimeFallback
            ? "Synthetic runtime fallback. Install Microsoft's WebView2 runtime explicitly, or use your normal browser and import the ZIP. This fixture never checks or installs a runtime."
            : "Use the latest package from AeroNav. Saved copies are reused; sign in only if needed.";
        var testing = state == GngUpdateFixtureState.TestingReview;
        PackageText.Text = (testing ? "Testing package · " : "Swedish GNG ") + "2610/01 rev.3";
        PackageSummaryText.Text = "2605/01 rev.1 → 2610/01 rev.3";
        InstallButton.Content = testing ? "Install testing package" : "Install update";
        WarningsText.Text = "SYNTHETIC REVIEW — no files will change.\n" +
            "Package defaults and shipped plugins will be refreshed. Personal sign-in fields and Local/Hoppie files stay.\n" +
            "Recovery backup: " + SyntheticBackupPath;
        ReviewSummaryText.Text = "Your sign-in details are kept. List positions are kept where matched. A backup is created before changes.";
        SetSyntheticAssessment(state);
        ShowReviewDetails(false);
        ViewDetailsButton.Visibility = review ? Visibility.Visible : Visibility.Collapsed;
        OtherOptions.Visibility = review || state == GngUpdateFixtureState.Completion ? Visibility.Collapsed : BrowserPanel.Visibility;
        OtherOptions.IsExpanded = state == GngUpdateFixtureState.RuntimeFallback;
        RecoveryDetails.IsExpanded = false;
        CloseButton.Content = state == GngUpdateFixtureState.Completion ? "Done" : "Cancel";
        SetEuroScopeBlockingReason(state == GngUpdateFixtureState.RunningReview ? "EuroScope is running. Close every EuroScope window before installing." : null);
        if (state == GngUpdateFixtureState.ListLayoutFailed)
        {
            InvalidateReviewAssessment("Your list settings could not be matched safely. Open View details to use the package's list layout, then refresh changes.");
            RefreshReviewButton.Visibility = Visibility.Visible;
            InstallButton.Visibility = Visibility.Collapsed;
        }
        SetReviewFiles(ExampleRows(PreserveListLayout.IsChecked == true));
        if (state == GngUpdateFixtureState.ListLayoutFailed)
        {
            PackageSummaryText.Text = string.Empty;
            SetReviewFiles([]);
            NoFileChangesText.Text = "The file comparison is unavailable until the update checks succeed.";
        }
        CompleteTitle.Text = "Swedish GNG is updated";
        CompleteText.Text = "Version 2610/01 rev.3 is installed.";
        BackupText.Text = SyntheticBackupPath;
        CleanupHint.Text = "Old files can be reviewed separately. Cleanup is optional.";
        StatusText.Text = state switch
        {
            GngUpdateFixtureState.WaitingForLogin => "Sign in on AeroNav. Launchpad will download the Swedish package after sign-in. Installation still requires your review.",
            GngUpdateFixtureState.CheckingSavedPackages => "Checking AeroNav's current AIRAC/revision and matching saved ZIPs before opening the browser. Synthetic progress only; no network or cache files are read.",
            GngUpdateFixtureState.ReferenceWaitingForLogin => "Sign in again if AeroNav asks. Your Update Only ZIP is saved; Launchpad is waiting for its matching Full reference. Synthetic sign-in state only.",
            GngUpdateFixtureState.Downloading => "Downloading 1 of 2: ESAA Update Only · 2610 / 01 rev. 3 — 42% (21 MB of 50 MB). The package will be reviewed before installation. Synthetic progress only.",
            GngUpdateFixtureState.PreparingReference => "Checking the Update Only ZIP before downloading its matching Full reference… Stop remains available. Synthetic validation only; no files are read or written.",
            GngUpdateFixtureState.DownloadingReference => "Downloading 2 of 2: ESAA Full Package · 2610 / 01 rev. 3 — 42% (84 MB of 200 MB). Kept as a cleanup reference; Update Only remains the installation package. Synthetic progress only.",
            GngUpdateFixtureState.PreparingPackages => "Checking the downloaded packages and calculating the proposed changes before review. Nothing is installed. Synthetic validation only; no files are read or written.",
            GngUpdateFixtureState.DownloadCancelled => "Synthetic download cancelled. Choose Retry download to try again; it does not restart automatically.",
            GngUpdateFixtureState.ReferenceCancelled => "Synthetic reference download cancelled. Update Only is saved. Retry reference, import its matching Full ZIP, or choose Start over. Nothing has been installed.",
            GngUpdateFixtureState.DownloadFailed => SyntheticFailure,
            GngUpdateFixtureState.ReferenceFailed => "The Full reference download stopped: NetworkDisconnected. Your Update Only ZIP is saved. Retry reference, import its matching Full ZIP, or Start over. Nothing was installed.\n" + SyntheticFailure,
            GngUpdateFixtureState.ListLayoutFailed => "This synthetic custom list route cannot be preserved safely. Nothing was installed. Clear Keep my list positions and visibility above to prepare a new review using package list defaults. No settings file was read.",
            GngUpdateFixtureState.Review or GngUpdateFixtureState.TestingReview or GngUpdateFixtureState.AttentionReview or GngUpdateFixtureState.RunningReview => "Package ready.",
            GngUpdateFixtureState.CachedReview => "Saved package ready.",
            GngUpdateFixtureState.Completion => "Update complete.",
            GngUpdateFixtureState.RuntimeFallback => "Synthetic missing-runtime state. Import ZIP and the normal-browser route remain available.",
            _ => "Choose Download Swedish GNG or Import ZIP to begin. Opening this window does not start a download."
        };
        if (!review || state == GngUpdateFixtureState.ListLayoutFailed) _plan = null;
        SetBrowserInteractionBlocked(downloading || preparing);
        UpdateSyntheticEuroScopeControls();
        UpdateReviewContentLayout();
        StateChanged?.Invoke();
    }

    internal void SetSyntheticEuroScopeState(string? blockingReason)
    {
        SetEuroScopeBlockingReason(blockingReason);
        UpdateSyntheticEuroScopeControls();
    }

    private void UpdateSyntheticEuroScopeControls() => UpdateEuroScopeControls(
        SyntheticState is GngUpdateFixtureState.Review or GngUpdateFixtureState.CachedReview or GngUpdateFixtureState.TestingReview or GngUpdateFixtureState.AttentionReview or GngUpdateFixtureState.RunningReview,
        SyntheticState is not (GngUpdateFixtureState.Downloading or GngUpdateFixtureState.DownloadingReference or
            GngUpdateFixtureState.PreparingReference or GngUpdateFixtureState.PreparingPackages or GngUpdateFixtureState.CheckingSavedPackages));

    internal static GngUpdateFile[] ExampleRows(bool preserveListLayout = true) =>
    [
        new("ESAA APP ACC.prf", "Merge profile", "Refresh the sector and shipped plugins; keep fake session fields and custom display names.") { BeforeHash = "old-profile", AfterHash = "new-profile" },
        new("ESAA/Plugins/TopSky.dll", "Promote plugin", "Promote the staged package DLL; keep the original active DLL in the recovery backup.") { BeforeHash = "old-plugin", AfterHash = "new-plugin" },
        new("ESAA/Plugins/TopSkySettingsLocal.txt", "Keep personal file", "Preserve existing local settings byte for byte.") { BeforeHash = "personal-local", AfterHash = "personal-local" },
        new("ESAA/Plugins/TopSkyCPDLChoppieCode.txt", "Keep personal file", "Keep the synthetic personal file without exposing its contents.") { BeforeHash = "personal-hoppie", AfterHash = "personal-hoppie" },
        new("ESAA/Settings/Lists.txt", preserveListLayout ? "Merge list layout" : "Replace package default",
            preserveListLayout ? "Keep list positions and visibility; refresh package columns, items and plugin settings." : "Use the package list positions, visibility, columns, items and plugin settings.")
            { BeforeHash = "old-list-layout", AfterHash = preserveListLayout ? "merged-list-layout" : "package-list-layout" },
        new("ESAA/Plugins/TopSkySettings.txt", "Unchanged", "Already matches the planned package result.") { BeforeHash = "same-default", AfterHash = "same-default" }
    ];

    internal static GngUpdateFile[] UnchangedRows()
    {
        var rows = ExampleRows();
        return [rows[2], rows[3], rows[5]];
    }

    internal void SetSyntheticReviewRows(IReadOnlyList<GngUpdateFile> rows)
    {
        PackageAlreadyMatches = !rows.Any(file => file.WritesFile);
        SetReviewFiles(rows);
        if (!rows.Any(file => file.WritesFile))
        {
            ReviewPanel.Visibility = Visibility.Collapsed;
            CompletePanel.Visibility = Visibility.Visible;
            InstallButton.Visibility = Visibility.Collapsed;
            InstallButton.IsEnabled = false;
            CompleteTitle.Text = "GNG already matches";
            CompleteText.Text = "No files were changed.";
            _plan = null;
            RecoveryDetails.Visibility = Visibility.Collapsed;
            ViewDetailsButton.Visibility = OtherOptions.Visibility = Visibility.Collapsed;
            _reviewIsCurrent = false;
        }
        else
        {
            ReviewPanel.Visibility = Visibility.Visible;
            CompletePanel.Visibility = Visibility.Collapsed;
            InstallButton.Visibility = Visibility.Visible;
            SetSyntheticAssessment(SyntheticState);
            if (_plan is not null) _plan = _plan with { Files = rows };
            ShowReviewDetails(false);
            UpdateSyntheticEuroScopeControls();
        }
        UpdateReviewContentLayout();
    }

    private void SetSyntheticAssessment(GngUpdateFixtureState state)
    {
        var plan = new GngUpdatePlan("synthetic.zip", "2610/01 rev.3", DataFolder, "synthetic.zip", ExampleRows(PreserveListLayout.IsChecked == true), [])
        {
            IsTestingPackage = state == GngUpdateFixtureState.TestingReview,
            PreserveListLayout = PreserveListLayout.IsChecked == true,
            ReviewContext = new GngReviewContext
            {
                HasExistingFiles = true, HasVerifiedBaseline = true,
                RdfOverrideCount = state == GngUpdateFixtureState.AttentionReview ? 1 : 0,
                PreservedCustomPluginCount = state == GngUpdateFixtureState.AttentionReview ? 1 : 0,
                ClassifiedWarnings = []
            }
        };
        _plan = plan;
        SetReviewAssessment(GngReviewNotes.Assess(plan));
    }

    internal void SetSyntheticPackageNotes()
    {
        SetPackageNotes(["Your local TopSky settings override RDF_Mode. Compatibility needs review."], SyntheticBackupPath);
        SetSyntheticAssessment(GngUpdateFixtureState.AttentionReview);
        UpdateSyntheticEuroScopeControls();
    }

    internal void SetSyntheticReviewProgress()
    {
        PreserveListLayout.IsEnabled = false;
        DownloadActivityPanel.Visibility = Visibility.Visible;
        WarningsText.Text = string.Join(Environment.NewLine, Enumerable.Repeat("Synthetic warning: retain this recovery backup and review customized package defaults before installing.", 15));
        StatusText.Text = string.Join(Environment.NewLine, Enumerable.Repeat("Synthetic long installation status with a recovery path C:\\Synthetic\\Recovery\\dated-backup.", 10));
        DownloadStageText.Text = "Checking reviewed package";
        DownloadStageText.Visibility = Visibility.Visible;
        Activity.IsIndeterminate = true;
        Activity.Visibility = Visibility.Visible;
    }

    private void StartBrowser_Click(object sender, RoutedEventArgs e)
    {
        SyntheticCacheBypassed = false;
        SetSyntheticState(GngUpdateFixtureState.CheckingSavedPackages);
    }
    private void FreshDownload_Click(object sender, RoutedEventArgs e)
    {
        SyntheticCacheBypassed = true;
        SetSyntheticState(GngUpdateFixtureState.WaitingForLogin);
    }
    internal void CompleteSyntheticCacheLookup(bool matchingPackages) => SetSyntheticState(matchingPackages
        ? GngUpdateFixtureState.CachedReview : GngUpdateFixtureState.WaitingForLogin);
    private void RetryDownload_Click(object sender, RoutedEventArgs e) => SetSyntheticState(SyntheticState is GngUpdateFixtureState.ReferenceCancelled or GngUpdateFixtureState.ReferenceFailed
        ? GngUpdateFixtureState.DownloadingReference : GngUpdateFixtureState.WaitingForLogin);
    private void StopRequest_Click(object sender, RoutedEventArgs e) => SetSyntheticState(SyntheticState is GngUpdateFixtureState.PreparingReference or GngUpdateFixtureState.ReferenceWaitingForLogin
        ? GngUpdateFixtureState.ReferenceCancelled : GngUpdateFixtureState.DownloadCancelled);
    private void StartOver_Click(object sender, RoutedEventArgs e) => SetSyntheticState(GngUpdateFixtureState.Start);
    private void Back_Click(object sender, RoutedEventArgs e) => StatusText.Text = "Synthetic browser-back action only.";
    private void Reload_Click(object sender, RoutedEventArgs e) => StatusText.Text = "Synthetic browser-reload action only.";
    private void Home_Click(object sender, RoutedEventArgs e) => StatusText.Text = "Synthetic AeroNav action only. No website opens.";
    private void InstallRuntime_Click(object sender, RoutedEventArgs e) => StatusText.Text = "Synthetic runtime action only. Nothing was downloaded or installed.";
    private void RuntimeInfo_Click(object sender, RoutedEventArgs e) => StatusText.Text = "Synthetic runtime information action only. No link opens.";
    private void ExternalBrowser_Click(object sender, RoutedEventArgs e) => StatusText.Text = "Synthetic external-browser action only. No browser opens.";
    private void RefreshReview_Click(object sender, RoutedEventArgs e) => SetSyntheticState(SyntheticState == GngUpdateFixtureState.TestingReview ? GngUpdateFixtureState.TestingReview : GngUpdateFixtureState.Review);
    private void Import_Click(object sender, RoutedEventArgs e) => SetSyntheticState(GngUpdateFixtureState.Review);
    private void ChooseAnother_Click(object sender, RoutedEventArgs e) => SetSyntheticState(GngUpdateFixtureState.Start);
    private void PreserveListLayout_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || PreserveListLayout is null || !PreserveListLayout.IsEnabled) return;
        InstallButton.IsEnabled = false;
        if (SyntheticState == GngUpdateFixtureState.ListLayoutFailed)
            SetSyntheticState(GngUpdateFixtureState.Review);
        else if (SyntheticState is GngUpdateFixtureState.Review or GngUpdateFixtureState.CachedReview or GngUpdateFixtureState.TestingReview or GngUpdateFixtureState.AttentionReview or GngUpdateFixtureState.RunningReview)
            SetReviewFiles(ExampleRows(PreserveListLayout.IsChecked == true));
        UpdateSyntheticEuroScopeControls();
        SetSyntheticAssessment(SyntheticState);
        StatusText.Text = PreserveListLayout.IsChecked == true
            ? "Synthetic preference: keep list positions and visibility. The new plan must be applied explicitly."
            : "Synthetic preference: use package list defaults. The new plan must be applied explicitly.";
    }
    private void Install_Click(object sender, RoutedEventArgs e)
    {
        if ((SyntheticState is GngUpdateFixtureState.Review or GngUpdateFixtureState.CachedReview or GngUpdateFixtureState.TestingReview or GngUpdateFixtureState.AttentionReview or GngUpdateFixtureState.RunningReview) && _euroScopeBlockingReason is null)
        {
            AttemptedChanges = InstallationCompleted = true;
            MutationRevision++;
            SetSyntheticState(GngUpdateFixtureState.Completion);
        }
    }
    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_euroScopeBlockingReason is not null) return;
        SetSyntheticState(GngUpdateFixtureState.Completion);
        CompleteTitle.Text = "Restore GNG backup";
        CompleteText.Text = "The reviewed backup will restore the previous package files. Files edited since installation will be kept for review.";
        RestoreConfirmText.Text = "Restore files into " + DataFolder + "?";
        RestoreConfirmPanel.Visibility = Visibility.Visible;
    }
    private void Cleanup_Click(object sender, RoutedEventArgs e) => StatusText.Text = "Synthetic cleanup handoff only. A separate review would be required; no files or windows were opened.";
    private void CancelDownload_Click(object sender, RoutedEventArgs e) => SetSyntheticState(SyntheticState == GngUpdateFixtureState.DownloadingReference
        ? GngUpdateFixtureState.ReferenceCancelled : GngUpdateFixtureState.DownloadCancelled);
    private void Close_Click(object sender, RoutedEventArgs e) { if (CanDismiss) Dismissed?.Invoke(); }
    private void CheckFonts_Click(object sender, RoutedEventArgs e) => FollowUpRequested?.Invoke("Fonts");
    private void CheckProfile_Click(object sender, RoutedEventArgs e) => FollowUpRequested?.Invoke("Profile");
    private void ConfirmRestore_Click(object sender, RoutedEventArgs e)
    {
        if (_euroScopeBlockingReason is not null) return;
        AttemptedChanges = true; InstallationCompleted = false; MutationRevision++;
        SetSyntheticState(GngUpdateFixtureState.Completion);
        CompleteTitle.Text = "Backup restored";
        CompleteText.Text = "3 files restored.";
    }
    private void CancelRestore_Click(object sender, RoutedEventArgs e) => RestoreConfirmPanel.Visibility = Visibility.Collapsed;

}


// Only the test harness wraps the real UserControl in a non-visible Window for measurements.
public sealed class GngUpdateWindow : Window
{
    public GngUpdateControl Update { get; }
    private bool _investigation;
    public GngUpdateWindow()
    {
        Title = "Swedish GNG"; Width = 940; Height = 680; MinWidth = 616; MinHeight = 440;
        SetResourceReference(BackgroundProperty, "AppBg");
        SetResourceReference(ForegroundProperty, "PrimaryText");
        SetResourceReference(FontSizeProperty, "TextSize12");
        Update = new(); Content = Update;
        NameScope.SetNameScope(this, NameScope.GetNameScope(Update));
        Update.Dismissed += Close;
        UiFixture.Initialize(this);
    }
    internal GngUpdateFixtureState SyntheticState => Update.SyntheticState;
    internal bool SyntheticCacheBypassed => Update.SyntheticCacheBypassed;
    internal void RefreshSyntheticScenario() { if (!_investigation) Update.RefreshSyntheticScenario(); }
    internal static GngUpdateWindow CreateInvestigation()
    {
        var window = new GngUpdateWindow { _investigation = true };
        window.Update.PrepareSyntheticFileNavigation(CreateInvestigationPlan());
        return window;
    }
    internal static GngUpdatePlan CreateInvestigationPlan()
    {
        string[] profiles = ["ESAA APP ACC.prf", "ESAA TWR.prf", "ESAA AFIS.prf", "ESSA TWR CDM.prf", "ESSB TWR.prf", "ESGG TWR.prf", "ESMS TWR.prf", "ESNU TWR.prf", "ESNQ TWR.prf"];
        string[] plugins = ["TopSky", "GroundRadar", "RDF", "VatEFS", "SAAMAN", "CDM", "VCH", "VATIRIS", "VATSIMBookings", "CustomDisplay"];
        var files = Enumerable.Range(0,150).Select(index =>
        {
            string path = index < profiles.Length ? profiles[index] : index < profiles.Length + plugins.Length
                ? "ESAA/Plugins/" + plugins[index-profiles.Length] + ".dll" : (index % 5) switch
                {
                    0 => $"ESAA/ASR/ESSA_APP_{index:000}.asr",
                    1 => $"ESAA/Plugins/TopSky/Maps/Stockholm_terminal_area/ESSA_Arrival_routes_{index:000}.txt",
                    2 => $"ESAA/Settings/Lists_{index:000}.txt",
                    3 => $"ESAA/Plugins/GroundRadar/AirportData/ESSA_Stand_and_taxiway_labels_{index:000}.txt",
                    _ => $"ESAA/Alias/Swedish_controller_messages_{index:000}.txt"
                };
            string action = index % 10 == 9 ? "Unchanged" : index % 17 == 16 ? "Keep personal file" :
                path.EndsWith(".prf") ? "Merge profile" : path.EndsWith(".dll") ? "Promote plugin" :
                index % 13 == 0 ? "Replace customized default" : index % 5 == 2 ? "Merge list layout" : "Replace package default";
            return new GngUpdateFile(path,action,"Synthetic file only. No installed file is read or changed.") { BeforeHash="before-"+index, AfterHash="after-"+index };
        }).ToArray();
        return new("ESAA-Full-synthetic.zip","2610/01 rev.3",@"C:\Synthetic\EuroScope","synthetic.zip",files,[])
        {
            ReviewContext = new GngReviewContext { HasExistingFiles=true,HasVerifiedBaseline=true,
                CustomizedReplacementCount=files.Count(file=>file.Action=="Replace customized default"),ClassifiedWarnings=[] }
        };
    }
    internal void SetSyntheticState(GngUpdateFixtureState state) => Update.SetSyntheticState(state);
    internal void SetSyntheticEuroScopeState(string? value) => Update.SetSyntheticEuroScopeState(value);
    internal void SetSyntheticReviewRows(IReadOnlyList<GngUpdateFile> rows) => Update.SetSyntheticReviewRows(rows);
    internal void SetSyntheticPackageNotes() => Update.SetSyntheticPackageNotes();
    internal void SetSyntheticReviewProgress() => Update.SetSyntheticReviewProgress();
    internal void CompleteSyntheticCacheLookup(bool matchingPackages) => Update.CompleteSyntheticCacheLookup(matchingPackages);
    internal static GngUpdateFile[] ExampleRows(bool preserveListLayout = true) => GngUpdateControl.ExampleRows(preserveListLayout);
    internal static GngUpdateFile[] UnchangedRows() => GngUpdateControl.UnchangedRows();
}

internal static partial class LayoutMatrix
{
    internal static string RunGngOnly()
    {
        var count = 0;
        foreach (var dark in new[] { false, true })
        {
            if (UiFixture.Dark != dark) UiFixture.ToggleTheme();
            count += CheckGngUpdateLayouts();
            count += GngFileNavigationTests.Run();
            count += CheckGngInvestigationLayouts();
            count += CheckCompactGngChangeNavigation();
        }
        return $"PASS: {count} hidden GNG layouts, including 600 × 400 inline content.";
    }

    private static int CheckGngInvestigationLayouts()
    {
        int count = 0;
        foreach (double textScale in new[] { 1d, 1.5d })
        foreach (bool minimum in new[] { false, true })
        {
            UiFixture.SetTextScale(textScale); UiFixture.Minimum = minimum; UiFixture.Scale = 1;
            var window = GngUpdateWindow.CreateInvestigation();
            try
            {
                LayoutValidation.CheckWindowContent(window); DrainBindings();
                var grid = Required<DataGrid>(window,"FilesGrid");
                var content = (FrameworkElement)window.Content;
                Require(Required<TabItem>(window,"FilesTab").IsSelected, "Investigation must open directly on Files.");
                for (DependencyObject? parent = VisualTreeHelper.GetParent(grid); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                    Require(parent is not ScrollViewer, "The file list must own its scroll area, without an ancestor ScrollViewer.");
                Require(grid.ActualHeight > content.ActualHeight * .3, "Files must receive a substantial share of the available height at enlarged text.");
                Require(grid.Columns[0].ActualWidth > 120 && grid.Columns[1].ActualWidth >= 174 && grid.Columns[2].ActualWidth >= 87,
                    "File, change and View columns must have usable first-render widths: " + string.Join(", ",grid.Columns.Select(column=>column.ActualWidth.ToString("0.0"))));
                Require(grid.Items.Count > 100, "The investigation fixture must exercise a real-sized change list.");
                var rows = LayoutValidation.Descendants(grid).OfType<DataGridRow>().ToArray();
                Require(rows.Length > 0 && rows.Length < 100, "The long list must realize a bounded, virtualized set of rows.");
                Require(rows.Max(row=>row.ActualHeight)-rows.Min(row=>row.ActualHeight)<1,
                    "Long paths and change labels must not create uneven row heights.");
                var scroll = LayoutValidation.Descendants(grid).OfType<ScrollViewer>().First();
                Require(scroll.ScrollableWidth < 1, "The narrow file list should not require horizontal scrolling.");
                var filter = Required<ComboBox>(window,"FileFilterBox");
                var search = Required<TextBox>(window,"FileSearchBox");
                filter.SelectedIndex = 1;
                Require(grid.Items.Count>0 && grid.Items.Cast<GngUpdateFile>().All(file=>file.WritesFile && file.RelativePath.EndsWith(".prf",StringComparison.OrdinalIgnoreCase)), "Profile filter must show changed profiles only.");
                filter.SelectedIndex = 2;
                Require(grid.Items.Count>0 && grid.Items.Cast<GngUpdateFile>().All(file=>file.WritesFile && file.RelativePath.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)), "Plugin filter must show changed DLLs only.");
                filter.SelectedIndex = 3;
                Require(grid.Items.Count==150,"All files must expose unchanged and personal entries too.");
                filter.SelectedIndex = 0; search.Text="ESSA";
                var source = grid.ItemsSource; var selected=grid.SelectedItem;
                Required<TabItem>(window,"OptionsTab").IsSelected=true;
                Required<TabItem>(window,"NotesTab").IsSelected=true;
                Required<TabItem>(window,"FilesTab").IsSelected=true;
                Require(search.Text=="ESSA" && ReferenceEquals(source,grid.ItemsSource) && ReferenceEquals(selected,grid.SelectedItem),
                    "Switching detail tabs must preserve the current search, selection and result list.");
                search.Text="no-synthetic-file-matches-this";
                Require(grid.Items.Count==0 && Required<TextBlock>(window,"NoFileChangesText").Text.Contains("search") &&
                    !window.Update.PackageAlreadyMatches && Required<Button>(window,"InstallButton").IsEnabled,
                    "An empty search must not turn a valid update into an already-matching or disabled installation.");
                search.Text=string.Empty;
                window.Update.PrepareSyntheticFileNavigation(GngUpdateWindow.CreateInvestigationPlan());
                Require(search.Text.Length==0 && filter.SelectedIndex==0,"Only a newly prepared plan resets search and filters.");
                Require(!window.IsVisible,"Investigation tests must not display a window.");
                count++;
            }
            finally { window.Close(); }
        }
        UiFixture.SetTextScale(1); UiFixture.Minimum=false;
        return count;
    }

    private static int CheckGngUpdateLayouts()
    {
        var count = 0;
        foreach (var minimum in new[] { false, true })
        foreach (var state in Enum.GetValues<GngUpdateFixtureState>())
        {
            UiFixture.Blank = false; UiFixture.Minimum = minimum; UiFixture.Scale = 1;
            var window = new GngUpdateWindow();
            try
            {
                window.SetSyntheticState(state);
                LayoutValidation.CheckWindowContent(window); DrainBindings();
                Require(Required<ContentControl>(window,"BrowserHost").Content is null, "Synthetic GNG tests must never initialize a browser.");
                Require(window.FindName("ConfirmInstall") is null, "The redundant review acknowledgement must not return.");
                Require(Required<TextBox>(window,"FolderText").IsReadOnly, "Destination must remain read-only during an operation.");
                Require(!window.IsVisible, "Hidden GNG tests must not display a native window.");
                if (state == GngUpdateFixtureState.TestingReview)
                    Require(Required<TextBox>(window,"AttentionText").Text.Contains("testing package") && window.Update.HasWarning &&
                        Equals(Required<Button>(window,"InstallButton").Content,"Install testing package"),
                        "Manual Testing packages need a visible warning and explicit testing installation action.");
                if (state is GngUpdateFixtureState.Review or GngUpdateFixtureState.CachedReview or GngUpdateFixtureState.TestingReview or GngUpdateFixtureState.AttentionReview or GngUpdateFixtureState.RunningReview) CheckGngReview(window);
                if (state == GngUpdateFixtureState.ListLayoutFailed)
                {
                    Require(IsPresented(Required<Button>(window,"ViewDetailsButton")) && !IsPresented(Required<Button>(window,"InstallButton")),
                        "Failed list matching must allow options to be corrected without enabling installation.");
                    Required<Button>(window,"ViewDetailsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(Required<DataGrid>(window,"FilesGrid").Items.Count == 0 &&
                        Required<TextBlock>(window,"NoFileChangesText").Text.Contains("unavailable"),
                        "A failed rebuild must not keep showing the previous file comparison as a current plan.");
                    Required<TabItem>(window,"OptionsTab").IsSelected = true;
                    LayoutValidation.CheckWindowContent(window); DrainBindings();
                    var layout = Required<CheckBox>(window,"PreserveListLayout");
                    Require(IsPresented(layout) && layout.IsEnabled, "List preservation must remain reachable after its preview fails.");
                    layout.IsChecked = false;
                    Require(!window.Update.InstallationCompleted && Required<Button>(window,"InstallButton").IsEnabled && window.Update.HasWarning,
                        "Using package list layout must prepare another explicit review with a reset warning, never install.");
                }
                if (state == GngUpdateFixtureState.CheckingSavedPackages)
                {
                    Require(window.Update.IsBusy && !window.Update.NeedsAttentionView, "Cache preparation must stay in the main Apps view.");
                    window.CompleteSyntheticCacheLookup(true);
                    Require(window.SyntheticState == GngUpdateFixtureState.CachedReview && !window.Update.InstallationCompleted,
                        "A cache hit must prepare review without installing.");
                }
                if (state is GngUpdateFixtureState.Downloading or GngUpdateFixtureState.DownloadingReference)
                {
                    Require(window.Update.IsBusy && window.Update.CanCancel && !window.Update.CanDismiss && !window.Update.NeedsAttentionView,
                        "Downloads must remain cancelable in Apps without dismissing their operation.");
                    Require(Required<FrameworkElement>(window,"BrowserSurface").Visibility == Visibility.Hidden,
                        "Download overlay must hide the native browser surface without recreating it.");
                    window.Update.CancelPreparation();
                    Require(!window.Update.IsBusy && !window.Update.InstallationCompleted, "Cancel must stop preparation without installing.");
                }
                if (state == GngUpdateFixtureState.Completion)
                {
                    Require(!Required<Expander>(window,"RecoveryDetails").IsExpanded && !IsPresented(Required<Button>(window,"CleanupButton")) &&
                        !IsPresented(Required<Button>(window,"ImportButton")) && Equals(Required<Button>(window,"CloseButton").Content,"Done"),
                        "Successful completion must keep optional maintenance out of the normal result.");
                    window.Update.SetCompletionChecks("Fonts need attention.",true,false);
                    Require(IsPresented(Required<Button>(window,"CheckFontsButton")) && !IsPresented(Required<Button>(window,"CheckProfileButton")),
                        "Completion should show only relevant follow-up actions.");
                }
                CheckFooter(window);
                count++;
            }
            catch(Exception ex) { throw new System.IO.InvalidDataException($"GNG {state}, minimum={minimum}: {ex.Message}",ex); }
            finally { window.Close(); }
        }
        return count;
    }

    private static void CheckGngReview(GngUpdateWindow window)
    {
        window.SetSyntheticState(GngUpdateFixtureState.Review);
        LayoutValidation.CheckWindowContent(window); DrainBindings();
        var install = Required<Button>(window,"InstallButton");
        Require(IsPresented(install) && install.IsEnabled && Equals(install.Content,"Install update") && !window.Update.InstallationCompleted,
            "Review must expose one explicit installation action without running it.");
        Require(Required<TextBlock>(window,"AssessmentHeading").Text == "Ready to install" &&
            Required<TextBox>(window,"AttentionText").Text.Contains("Installation checks passed") && !window.Update.HasWarning,
            "An ordinary reviewed update must clearly report that its installation checks passed.");
        Require(!IsPresented(Required<TextBlock>(window,"SummaryInstructionText")), "Ready summary should not repeat its primary button instruction.");
        var summaryBounds = Bounds(Required<ScrollViewer>(window,"ReviewInformationScroll"),(FrameworkElement)window.Content);
        var actionBounds = Bounds(install,(FrameworkElement)window.Content);
        Require(actionBounds.Top >= summaryBounds.Bottom && actionBounds.Top - summaryBounds.Bottom < 32,
            "The primary action must stay next to the short summary, rather than at the bottom of an empty pane.");
        Require(!IsPresented(Required<Grid>(window,"ReviewDetailsPanel")) && !IsPresented(Required<Expander>(window,"OtherOptions")) &&
            !IsPresented(Required<Button>(window,"ImportButton")) && !IsPresented(Required<DataGrid>(window,"FilesGrid")),
            "Normal review must not present paths, imports, maintenance choices or a file list.");
        Require(!Required<TextBlock>(window,"PackageSummaryText").Text.Contains("\\") &&
            !Required<TextBlock>(window,"PackageSummaryText").Text.Contains("files"),
            "The summary version line must not include a raw path or file counts.");
        window.SetSyntheticEuroScopeState("Close EuroScope before updating.");
        Require(!install.IsEnabled && window.Update.HasWarning &&
            Required<TextBlock>(window,"AssessmentHeading").Text == "Close EuroScope first" &&
            !Required<TextBox>(window,"AttentionText").Text.Contains("No issues found"),
            "Running EuroScope must replace the ready indication and block installation.");
        window.SetSyntheticEuroScopeState(null);
        Require(install.IsEnabled && !window.Update.InstallationCompleted, "Closing EuroScope must never start installation.");
        window.SetSyntheticPackageNotes();
        Require(window.Update.HasWarning && Required<TextBlock>(window,"AssessmentHeading").Text == "Check before installing" &&
            Required<TextBox>(window,"AttentionText").Text.Contains("TopSky"),
            "Real concerns must be visible in the summary without opening technical details.");
        Required<Button>(window,"ViewDetailsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        LayoutValidation.CheckWindowContent(window); DrainBindings();
        Require(IsPresented(Required<Grid>(window,"ReviewDetailsPanel")) && Required<TabItem>(window,"FilesTab").IsSelected && !Required<TabItem>(window,"OptionsTab").IsSelected,
            "Details must open directly on file investigation; installation options belong to their own tab.");
        CheckGngReviewFilter(window);
        Required<Button>(window,"BackToSummaryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        LayoutValidation.CheckWindowContent(window); DrainBindings();
        Require(IsPresented(Required<ScrollViewer>(window,"ReviewInformationScroll")) && !IsPresented(Required<Grid>(window,"ReviewDetailsPanel")),
            "Back to summary must return without discarding the prepared update.");
        CheckFooter(window);
    }

    private static void CheckGngReviewFilter(GngUpdateWindow window)
    {
        var grid=Required<DataGrid>(window,"FilesGrid");
        var filter=Required<ComboBox>(window,"FileFilterBox");
        Require(filter.SelectedIndex == 0 && grid.Items.Count == 3, "Changes-only review should hide unchanged and personal files.");
        filter.SelectedIndex=3;
        Require(grid.Items.Count == 6, "The optional unchanged filter must expose every file.");
        window.SetSyntheticReviewRows(GngUpdateWindow.UnchangedRows());
        Require(!Required<Button>(window,"InstallButton").IsEnabled && !IsPresented(Required<Button>(window,"InstallButton")) &&
            !window.Update.InstallationCompleted && !window.Update.AttemptedChanges &&
            Required<TextBlock>(window,"CompleteTitle").Text == "GNG already matches",
            "A matching package must finish without an installation or changed-files claim.");
        window.SetSyntheticReviewRows(GngUpdateWindow.ExampleRows());
        Required<Button>(window,"ViewDetailsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        LayoutValidation.CheckWindowContent(window); DrainBindings();
        var view=LayoutValidation.Descendants(grid).OfType<Button>().First(button=>Equals(button.Content,"View…"));
        Require(view.IsEnabled, "File comparisons must remain available; functional navigation is checked by the shared production controller tests.");
        var bounds=Bounds(Required<Button>(window,"InstallButton"),(FrameworkElement)window.Content);
        Require(bounds.Width>20 && bounds.Left>=-.5 && bounds.Right<=((FrameworkElement)window.Content).ActualWidth+.5,
            "The Update action must fit inside the narrow view.");
    }

    private static void CheckWizardGngChoice(SetupWizardWindow window)
    {
        var choice = Required<Button>(window, "GngButton");
        Require(IsPresented(choice) && choice.IsEnabled && !window.SyntheticGngSetupRequested,
            "Wizard GNG setup must be an available explicit action, never opened by advancing to its page.");
        choice.BringIntoView(); DrainBindings(); ((FrameworkElement)window.Content).UpdateLayout();
        var scroll = Required<ScrollViewer>(window, "ContentScroll");
        Require(new Rect(0, 0, scroll.ViewportWidth, scroll.ViewportHeight).IntersectsWith(Bounds(choice, scroll)),
            "The explicit GNG setup action cannot be reached by scrolling.");
        window.SetSyntheticRestart(true);
        Require(!choice.IsEnabled && !window.SyntheticGngSetupRequested, "A restart requirement must disable GNG setup without opening it.");
        window.SetSyntheticRestart(false);
        Require(choice.IsEnabled && !window.SyntheticGngSetupRequested, "Releasing the restart guard must not open GNG automatically.");
        choice.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(window.SyntheticGngSetupRequested, "An explicit click must record the inert GNG setup request.");
        Required<TextBlock>(window, "StatusText").Text = "";
    }
}


internal static partial class LayoutSnapshots
{
    internal static void CaptureGng(string output)
    {
        System.IO.Directory.CreateDirectory(output);
        CaptureEmbeddedGngInvestigation(output);
        foreach (var dark in new[] { false, true })
        foreach (double scale in new[] { 1d, 1.5d })
        foreach (var minimum in new[] { false, true })
        {
            if (UiFixture.Dark != dark) UiFixture.ToggleTheme();
            UiFixture.SetTextScale(scale); UiFixture.Minimum=minimum; UiFixture.Scale=1;
            var investigation=GngUpdateWindow.CreateInvestigation();
            try { Save(investigation,System.IO.Path.Combine(output,$"{(dark ? "dark" : "light")}-investigate-files-{(minimum ? "small" : "normal")}-{scale*100:0}.png")); }
            finally { investigation.Close(); }
        }
        UiFixture.SetTextScale(1);
        foreach (var dark in new[] { false, true })
        foreach (var setup in new[] { false, true })
        foreach (var state in new[] { GngUpdateFixtureState.Review, GngUpdateFixtureState.AttentionReview, GngUpdateFixtureState.RunningReview, GngUpdateFixtureState.TestingReview, GngUpdateFixtureState.Completion })
        {
            if (UiFixture.Dark != dark) UiFixture.ToggleTheme();
            UiFixture.Blank = UiFixture.Minimum = false; UiFixture.Scale = 1; UiFixture.SetTextScale(1);
            var main = new MainWindow { Tag = "Compact", Width = 900, Height = 550 };
            try
            {
                var flow = new GngUpdateControl();
                flow.SetSyntheticState(state);
                main.ShowSyntheticWorkspace(setup ? "Setup" : "Apps");
                if (setup)
                {
                    main.SetupWorkspace.ShowSection("Gng");
                    main.SetupWorkspace.SetGngOperationContent(flow);
                    main.SetupWorkspace.ShowGngOperation(true);
                }
                else
                {
                    ((FrameworkElement)main.FindName("AppsWorkspace")).Visibility = Visibility.Collapsed;
                    var host = (ContentControl)main.FindName("GngFlowHost");
                    host.Content = flow; host.Visibility = Visibility.Visible;
                    ((FrameworkElement)main.FindName("AppBulkActions")).Visibility = Visibility.Collapsed;
                    ((FrameworkElement)main.FindName("CheckButton")).Visibility = Visibility.Collapsed;
                    ((FrameworkElement)main.FindName("MaintenanceButton")).Visibility = Visibility.Collapsed;
                }
                Save(main, System.IO.Path.Combine(output, $"{(dark ? "dark" : "light")}-gng-{(setup ? "setup" : "main")}-{state.ToString().ToLowerInvariant()}-small.png"));
            }
            finally { main.Close(); }
        }
        foreach (var dark in new[] { false, true })
        foreach (var minimum in new[] { false, true })
        foreach (var state in new[] { GngUpdateFixtureState.Review, GngUpdateFixtureState.AttentionReview, GngUpdateFixtureState.RunningReview, GngUpdateFixtureState.TestingReview, GngUpdateFixtureState.WaitingForLogin, GngUpdateFixtureState.Completion, GngUpdateFixtureState.DownloadFailed })
        {
            if (UiFixture.Dark != dark) UiFixture.ToggleTheme();
            UiFixture.Blank = false; UiFixture.Minimum = minimum; UiFixture.Scale = 1;
            var window = new GngUpdateWindow();
            try
            {
                window.SetSyntheticState(state);
                Save(window, System.IO.Path.Combine(output, $"{(dark ? "dark" : "light")}-gng-{state.ToString().ToLowerInvariant()}-{(minimum ? "small" : "normal")}.png"));
                if (state == GngUpdateFixtureState.Review)
                {
                    ((Button)window.FindName("ViewDetailsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Save(window, System.IO.Path.Combine(output, $"{(dark ? "dark" : "light")}-gng-files-{(minimum ? "small" : "normal")}.png"));
                }
            }
            finally { window.Close(); }
        }
    }
}
