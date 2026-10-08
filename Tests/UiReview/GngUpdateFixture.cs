using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

internal enum GngUpdateFixtureState { Start, FreshStart, CheckingSavedPackages, WaitingForLogin, Downloading, PreparingReference, ReferenceWaitingForLogin, DownloadingReference, PreparingPackages, DownloadCancelled, ReferenceCancelled, DownloadFailed, ReferenceFailed, ListLayoutFailed, Review, CachedReview, Completion, RuntimeFallback }

// Link the production XAML only. BrowserHost stays empty: no WebView2 assembly,
// production download/installer service, native dialog or browser is used here.
public partial class GngUpdateWindow : Window
{
    private const string SyntheticBackupPath = @"C:\Synthetic\Controller applications and recovery exports\Launchpad GNG installation backups\Sweden FIR package installation with a deliberately long folder name\2026-10-07-synthetic-backup";
    private const string SyntheticFailure = "The download stopped: the network connection was interrupted (NetworkDisconnected). Nothing was installed. Retry the download, or use your normal browser and import the original ZIP.\n" +
        "Synthetic long diagnostic: the transfer ended before the package could be verified. This example deliberately exercises wrapped, selectable error details at minimum window size. No browser, network request, disk write or credential access occurred.\n" +
        "Synthetic download location: C:\\Synthetic\\Controller applications and recovery exports\\Launchpad downloads\\Sweden FIR package with a deliberately long directory name\\ESAA-Update-Only_20261001120000-261001-0003.zip\n" +
        "End of synthetic details: retry remains an explicit action.";
    private readonly TextBlock _browserPlaceholder;
    private bool _ready;
    private bool _syntheticNeedsFull;
    internal GngUpdateFixtureState SyntheticState { get; private set; }
    internal bool SyntheticCacheBypassed { get; private set; }

    public GngUpdateWindow()
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
        UiFixture.Initialize(this);
    }

    internal void RefreshSyntheticScenario() => SetSyntheticState(UiFixture.Blank ? GngUpdateFixtureState.FreshStart : GngUpdateFixtureState.Start);

    internal void SetSyntheticState(GngUpdateFixtureState state)
    {
        if (!_ready) return;
        SyntheticState = state;
        if (state is GngUpdateFixtureState.Start or GngUpdateFixtureState.FreshStart)
            _syntheticNeedsFull = state == GngUpdateFixtureState.FreshStart;
        var downloading = state is GngUpdateFixtureState.Downloading or GngUpdateFixtureState.DownloadingReference;
        var preparing = state is GngUpdateFixtureState.PreparingReference or GngUpdateFixtureState.PreparingPackages;
        var checkingCache = state == GngUpdateFixtureState.CheckingSavedPackages;
        var review = state is GngUpdateFixtureState.Review or GngUpdateFixtureState.CachedReview;
        var waitingForLogin = state is GngUpdateFixtureState.WaitingForLogin or GngUpdateFixtureState.ReferenceWaitingForLogin;
        var failed = state is GngUpdateFixtureState.DownloadFailed or GngUpdateFixtureState.ReferenceFailed or GngUpdateFixtureState.ListLayoutFailed;
        var reference = state is GngUpdateFixtureState.DownloadingReference or GngUpdateFixtureState.ReferenceWaitingForLogin or GngUpdateFixtureState.ReferenceCancelled or GngUpdateFixtureState.ReferenceFailed;
        var browsing = downloading || preparing || waitingForLogin || state is GngUpdateFixtureState.DownloadCancelled or GngUpdateFixtureState.ReferenceCancelled or GngUpdateFixtureState.DownloadFailed or GngUpdateFixtureState.ReferenceFailed;
        FolderText.Text = UiFixture.Blank ? "No EuroScope data folder chosen (synthetic)." : @"C:\Synthetic\EuroScope data";
        BrowserHost.Content = null;
        BrowserPanel.Visibility = browsing || checkingCache || state is GngUpdateFixtureState.Start or GngUpdateFixtureState.FreshStart or GngUpdateFixtureState.RuntimeFallback or GngUpdateFixtureState.ListLayoutFailed ? Visibility.Visible : Visibility.Collapsed;
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
        RuntimeInstallButton.Visibility = RuntimeInfoButton.Visibility = state == GngUpdateFixtureState.RuntimeFallback ? Visibility.Visible : Visibility.Collapsed;
        StartBrowserButton.Visibility = FreshDownloadButton.Visibility = state == GngUpdateFixtureState.RuntimeFallback ? Visibility.Collapsed : Visibility.Visible;
        StartBrowserButton.IsEnabled = FreshDownloadButton.IsEnabled = !downloading && !preparing && !checkingCache && !waitingForLogin;
        StartBrowserButton.Content = reference ? "Retry reference download" : "Download Swedish GNG";
        InstallButton.Visibility = review ? Visibility.Visible : Visibility.Collapsed;
        InstallButton.IsEnabled = false;
        ConfirmInstall.IsChecked = false;
        CancelDownloadButton.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        Activity.Visibility = downloading || preparing || checkingCache ? Visibility.Visible : Visibility.Collapsed;
        Activity.IsIndeterminate = preparing || checkingCache;
        Activity.Value = downloading ? 42 : 0;
        DownloadStageText.Visibility = downloading || preparing || checkingCache || failed || waitingForLogin ? Visibility.Visible : Visibility.Collapsed;
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
            _ => string.Empty
        };
        CleanupButton.Visibility = state == GngUpdateFixtureState.Completion ? Visibility.Visible : Visibility.Collapsed;
        OriginText.Text = browsing ? "https://files.aero-nav.com" : "Browser not started (synthetic)";
        StartHeading.Text = state == GngUpdateFixtureState.RuntimeFallback ? "The embedded browser needs WebView2" : "Get your GNG package";
        PackageKindText.Text = reference
            ? "Update Only 2610/01 rev.3 is saved. Its matching Full Package will be kept for cleanup comparisons and will not be installed."
            : _syntheticNeedsFull
            ? "A complete Swedish GNG installation was not found — Full Package will be used for setup or repair."
            : "Existing Swedish GNG found — Update Only will be installed after review. A matching Full Package will also be kept for cleanup comparisons.";
        StartExplanation.Text = state == GngUpdateFixtureState.RuntimeFallback
            ? "Synthetic runtime fallback. Install Microsoft's WebView2 runtime explicitly, or use your normal browser and import the ZIP. This fixture never checks or installs a runtime."
            : "Launchpad checks AeroNav's current version and matching saved packages first, then downloads what is missing. Sign in if asked. You review the changes before installing; your website sign-in stays in this browser.";
        PackageText.Text = "ESAA Update Only · 2610 / 01 rev. 3";
        PackageSummaryText.Text = state == GngUpdateFixtureState.CachedReview
            ? "Matching saved Update Only + Full reference · synthetic review · nothing downloaded"
            : "Synthetic review · personal settings kept · EuroScope stays closed";
        WarningsText.Text = "SYNTHETIC REVIEW — no files will change.\n" +
            "Package defaults and shipped plugins will be refreshed. Personal sign-in fields and Local/Hoppie files stay.\n" +
            "Recovery backup: " + SyntheticBackupPath;
        SetReviewFiles(ExampleRows(PreserveListLayout.IsChecked == true));
        CompleteTitle.Text = "GNG installation complete";
        CompleteText.Text = "Synthetic completion only. Package defaults and plugins were reviewed; personal settings remain. No files were installed and EuroScope was not started.";
        BackupText.Text = SyntheticBackupPath;
        CleanupHint.Text = "Optional: both original complete packages are available. Review old files separately before moving anything into a cleanup backup. Nothing is selected automatically.";
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
            GngUpdateFixtureState.Review => "Review the proposed changes, then confirm before installing. Synthetic data only.",
            GngUpdateFixtureState.CachedReview => "Matching saved packages are ready for review. The browser was not opened and nothing was downloaded or installed. Synthetic data only.",
            GngUpdateFixtureState.Completion => "Synthetic complete. Keep the recovery location; optional cleanup still needs a separate review.",
            GngUpdateFixtureState.RuntimeFallback => "Synthetic missing-runtime state. Import ZIP and the normal-browser route remain available.",
            _ => "Choose Download Swedish GNG or Import ZIP to begin. Opening this window does not start a download."
        };
        SetBrowserInteractionBlocked(downloading || preparing);
        UpdateSyntheticEuroScopeControls();
    }

    internal void SetSyntheticEuroScopeState(string? blockingReason)
    {
        SetEuroScopeBlockingReason(blockingReason);
        UpdateSyntheticEuroScopeControls();
    }

    private void UpdateSyntheticEuroScopeControls() => UpdateEuroScopeControls(
        SyntheticState is GngUpdateFixtureState.Review or GngUpdateFixtureState.CachedReview,
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

    internal void SetSyntheticReviewRows(IReadOnlyList<GngUpdateFile> rows) => SetReviewFiles(rows);

    internal void SetSyntheticReviewProgress()
    {
        PreserveListLayout.IsEnabled = false;
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
    private void Import_Click(object sender, RoutedEventArgs e) => SetSyntheticState(GngUpdateFixtureState.Review);
    private void ChooseAnother_Click(object sender, RoutedEventArgs e) => SetSyntheticState(GngUpdateFixtureState.Start);
    private void ViewFileChanges_Click(object sender, RoutedEventArgs e) => StatusText.Text = "Synthetic selected-file preview request. No external file was read and no dialog opened.";
    private void FilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => StatusText.Text = "Synthetic selected-file preview request. No external file was read and no dialog opened.";
    private void PreserveListLayout_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || PreserveListLayout is null || !PreserveListLayout.IsEnabled) return;
        ConfirmInstall.IsChecked = false;
        InstallButton.IsEnabled = false;
        if (SyntheticState == GngUpdateFixtureState.ListLayoutFailed)
            SetSyntheticState(GngUpdateFixtureState.Review);
        else if (SyntheticState is GngUpdateFixtureState.Review or GngUpdateFixtureState.CachedReview)
            SetReviewFiles(ExampleRows(PreserveListLayout.IsChecked == true));
        StatusText.Text = PreserveListLayout.IsChecked == true
            ? "Synthetic preference: keep list positions and visibility. Review must be confirmed again."
            : "Synthetic preference: use package list defaults. Review must be confirmed again.";
    }
    private void ConfirmationChanged(object sender, RoutedEventArgs e)
    {
        if (_ready) UpdateSyntheticEuroScopeControls();
    }
    private void Install_Click(object sender, RoutedEventArgs e)
    {
        if ((SyntheticState is GngUpdateFixtureState.Review or GngUpdateFixtureState.CachedReview) && ConfirmInstall.IsChecked == true && _euroScopeBlockingReason is null)
            SetSyntheticState(GngUpdateFixtureState.Completion);
    }
    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_euroScopeBlockingReason is not null) return;
        SetSyntheticState(GngUpdateFixtureState.Completion);
        CompleteTitle.Text = "Synthetic restore result";
        CompleteText.Text = "Existing edited destinations were kept. No files were restored by this fixture.";
    }
    private void Cleanup_Click(object sender, RoutedEventArgs e) => StatusText.Text = "Synthetic cleanup handoff only. A separate review would be required; no files or windows were opened.";
    private void CancelDownload_Click(object sender, RoutedEventArgs e) => SetSyntheticState(SyntheticState == GngUpdateFixtureState.DownloadingReference
        ? GngUpdateFixtureState.ReferenceCancelled : GngUpdateFixtureState.DownloadCancelled);
    private void Window_Closing(object? sender, CancelEventArgs e) { }
    private void Window_Closed(object? sender, EventArgs e) { }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

}

internal static partial class LayoutMatrix
{
    private static int CheckGngUpdateLayouts()
    {
        var count = 0;
        foreach (var minimum in new[] { false, true })
        foreach (var state in Enum.GetValues<GngUpdateFixtureState>())
        {
            UiFixture.Blank = false;
            UiFixture.Minimum = minimum;
            UiFixture.Scale = 1;
            var window = new GngUpdateWindow();
            try
            {
                window.SetSyntheticState(state);
                LayoutValidation.CheckWindowContent(window);
                DrainBindings();
                LayoutValidation.CheckWindowContent(window);
                CheckFooter(window);
                var content = (FrameworkElement)window.Content;
                var browser = Required<ContentControl>(window, "BrowserHost");
                Require(browser.Content is null, "The GNG fixture must never create an embedded browser.");
                var folder = Required<TextBox>(window, "FolderText");
                Require(folder.IsReadOnly && folder.Focusable && !string.IsNullOrWhiteSpace(folder.Text),
                    "The GNG destination must remain fixed, readable and selectable.");
                var status = Required<TextBox>(window, "StatusText");
                Require(status.IsReadOnly && status.IsReadOnlyCaretVisible && status.Focusable &&
                    status.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
                    "GNG status and recovery text must remain keyboard-readable and scrollable.");
                CheckGngActivityPanel(window, state);
                CheckGngBrowserInteraction(window, state);
                CheckGngListLayoutChoicePresentation(window, state);
                CheckGngDownloadActions(window, state);
                Require(IsPresented(Required<FrameworkElement>(window, "ReviewPanel")) == (state is GngUpdateFixtureState.Review or GngUpdateFixtureState.CachedReview),
                    "Only the GNG review state should show its file table and confirmation.");
                Require(IsPresented(Required<FrameworkElement>(window, "CompletePanel")) == (state == GngUpdateFixtureState.Completion),
                    "Only the GNG completion state should show its result and cleanup action.");
                switch (state)
                {
                    case GngUpdateFixtureState.Start:
                    case GngUpdateFixtureState.FreshStart:
                        var start = Required<Button>(window, "StartBrowserButton");
                        Require(IsPresented(start) && start.IsEnabled && Equals(start.Content, "Download Swedish GNG"),
                            "Automatic GNG download must begin with the explicit Download Swedish GNG action.");
                        var preserve = Required<CheckBox>(window, "PreserveListLayout");
                        preserve.IsChecked = false;
                        Require(window.SyntheticState == state && !IsPresented(Required<FrameworkElement>(window, "ReviewPanel")),
                            "Changing preservation before obtaining a package must only change the preference.");
                        preserve.IsChecked = true;
                        var packageKind = Required<TextBlock>(window, "PackageKindText");
                        Require(IsPresented(packageKind) && packageKind.TextWrapping == TextWrapping.Wrap &&
                            packageKind.Text.Contains(state == GngUpdateFixtureState.FreshStart ? "Full Package" : "Update Only", StringComparison.Ordinal),
                            "The automatic fresh/update package choice must be explained before download.");
                        start.BringIntoView(); DrainBindings(); content.UpdateLayout();
                        var startScroll = LayoutValidation.Descendants(Required<FrameworkElement>(window, "StartPanel")).OfType<ScrollViewer>().Single();
                        var viewport = new Rect(0, 0, startScroll.ViewportWidth, startScroll.ViewportHeight);
                        Require(viewport.IntersectsWith(Bounds(packageKind, startScroll)) && viewport.IntersectsWith(Bounds(start, startScroll)) &&
                            Bounds(packageKind, startScroll).Bottom <= Bounds(start, startScroll).Top,
                            "The package choice must be readable immediately before its download action, including at minimum size.");
                        start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Require(window.SyntheticState == GngUpdateFixtureState.CheckingSavedPackages && !window.SyntheticCacheBypassed && browser.Content is null,
                            "The default download action must check matching packages before any browser state.");
                        Require(!IsPresented(Required<Button>(window, "InstallButton")), "GNG must not offer installation before a review.");
                        LayoutValidation.CheckWindowContent(window);
                        window.SetSyntheticState(state);
                        Required<Button>(window, "FreshDownloadButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Require(window.SyntheticState == GngUpdateFixtureState.WaitingForLogin && window.SyntheticCacheBypassed && browser.Content is null,
                            "Fresh copies must explicitly bypass the synthetic cache check without opening a real browser.");
                        break;
                    case GngUpdateFixtureState.CheckingSavedPackages:
                        Require(IsPresented(Required<ProgressBar>(window, "Activity")) && Required<ProgressBar>(window, "Activity").IsIndeterminate &&
                            IsPresented(Required<FrameworkElement>(window, "StartPanel")) && !IsPresented(Required<FrameworkElement>(window, "BrowserToolbar")),
                            "Checking matching saved packages must show progress before browser initialization.");
                        window.CompleteSyntheticCacheLookup(matchingPackages: true);
                        LayoutValidation.CheckWindowContent(window);
                        Require(window.SyntheticState == GngUpdateFixtureState.CachedReview && browser.Content is null &&
                            Required<CheckBox>(window, "ConfirmInstall").IsChecked != true && !Required<Button>(window, "InstallButton").IsEnabled,
                            "A matching cache result must still require review and explicit installation confirmation.");
                        window.SetSyntheticState(GngUpdateFixtureState.CheckingSavedPackages);
                        window.CompleteSyntheticCacheLookup(matchingPackages: false);
                        LayoutValidation.CheckWindowContent(window);
                        Require(window.SyntheticState == GngUpdateFixtureState.WaitingForLogin && browser.Content is null,
                            "A synthetic cache miss may continue to sign-in without creating browser content in the fixture.");
                        break;
                    case GngUpdateFixtureState.WaitingForLogin:
                    case GngUpdateFixtureState.ReferenceWaitingForLogin:
                    case GngUpdateFixtureState.Downloading:
                    case GngUpdateFixtureState.PreparingReference:
                    case GngUpdateFixtureState.DownloadingReference:
                    case GngUpdateFixtureState.PreparingPackages:
                    case GngUpdateFixtureState.DownloadCancelled:
                    case GngUpdateFixtureState.ReferenceCancelled:
                    case GngUpdateFixtureState.DownloadFailed:
                    case GngUpdateFixtureState.ReferenceFailed:
                        CheckGngBrowserLayout(window, state);
                        break;
                    case GngUpdateFixtureState.Review:
                    case GngUpdateFixtureState.CachedReview:
                        CheckGngEuroScopeGuard(window);
                        CheckGngReview(window);
                        break;
                    case GngUpdateFixtureState.ListLayoutFailed:
                        Required<CheckBox>(window, "PreserveListLayout").IsChecked = false;
                        LayoutValidation.CheckWindowContent(window);
                        Require(window.SyntheticState == GngUpdateFixtureState.Review &&
                            Required<DataGrid>(window, "FilesGrid").Items.Cast<GngUpdateFile>().Any(file => file.RelativePath.EndsWith("Lists.txt", StringComparison.Ordinal) && file.Action == "Replace package default") &&
                            Required<CheckBox>(window, "ConfirmInstall").IsChecked != true && !Required<Button>(window, "InstallButton").IsEnabled,
                            "Opting out after a list-layout preview failure must permit a fresh, unconfirmed package-default review.");
                        break;
                    case GngUpdateFixtureState.Completion:
                        var backup = Required<TextBox>(window, "BackupText");
                        var cleanup = Required<Button>(window, "CleanupButton");
                        Require(backup.IsReadOnly && backup.Focusable && !string.IsNullOrWhiteSpace(backup.Text),
                            "GNG completion must retain a selectable recovery location.");
                        cleanup.BringIntoView(); DrainBindings(); content.UpdateLayout();
                        var completion = Required<ScrollViewer>(window, "CompletePanel");
                        Require(IsPresented(cleanup) && new Rect(0, 0, completion.ViewportWidth, completion.ViewportHeight).IntersectsWith(Bounds(cleanup, completion)),
                            "Optional GNG cleanup must be reachable by scrolling at the minimum size.");
                        cleanup.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Require(status.Text.Contains("separate review", StringComparison.Ordinal), "Cleanup must remain an inert, separate review step.");
                        break;
                    case GngUpdateFixtureState.RuntimeFallback:
                        foreach (var name in new[] { "RuntimeInstallButton", "RuntimeInfoButton", "ImportButton", "ExternalBrowserButton" })
                            Require(IsPresented(Required<Button>(window, name)) && Required<Button>(window, name).IsEnabled,
                                "Missing WebView2 must retain the explicit fallback action: " + name);
                        Require(!IsPresented(Required<Button>(window, "StartBrowserButton")) && !IsPresented(Required<Button>(window, "FreshDownloadButton")),
                            "Unavailable embedded-download actions must not obscure runtime recovery.");
                        Required<Button>(window, "RuntimeInstallButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Require(browser.Content is null, "Runtime fallback created browser content.");
                        break;
                }
                Require(!window.IsVisible, "A GNG layout check showed a native window.");
                CheckFooter(window);
                count++;
            }
            catch (Exception error)
            {
                throw new System.IO.InvalidDataException($"GNG {state}, {(UiFixture.Dark ? "dark" : "light")}, {(minimum ? "minimum" : "default")}: {error.Message}", error);
            }
            finally { window.Close(); }
        }
        return count;
    }

    private static void CheckGngDownloadActions(GngUpdateWindow window, GngUpdateFixtureState state)
    {
        var start = Required<Button>(window, "StartBrowserButton");
        var fresh = Required<Button>(window, "FreshDownloadButton");
        var locked = state is GngUpdateFixtureState.CheckingSavedPackages or GngUpdateFixtureState.WaitingForLogin or
            GngUpdateFixtureState.ReferenceWaitingForLogin or GngUpdateFixtureState.Downloading or
            GngUpdateFixtureState.DownloadingReference or GngUpdateFixtureState.PreparingReference or GngUpdateFixtureState.PreparingPackages;
        Require(start.IsEnabled == !locked && fresh.IsEnabled == !locked && Equals(fresh.Content, "Download fresh copies"),
            "Default and fresh-copy actions must both lock during lookup, pending requests and active work.");
        Require(start.ToolTip is string normalTip && normalTip.Contains("reuse matching saved packages", StringComparison.Ordinal) &&
            fresh.ToolTip is string freshTip && freshTip.Contains("Ignore saved packages", StringComparison.Ordinal),
            "Download tooltips must distinguish reuse from the explicit cache-bypass action.");
        if (!IsPresented(Required<FrameworkElement>(window, "StartPanel")) || state == GngUpdateFixtureState.RuntimeFallback) return;
        var panel = Required<FrameworkElement>(window, "StartPanel");
        var startBounds = Bounds(start, panel);
        var freshBounds = Bounds(fresh, panel);
        Require(IsPresented(fresh) && freshBounds.Left >= startBounds.Right && Math.Abs(freshBounds.Top - startBounds.Top) < 1 &&
            freshBounds.Right <= panel.ActualWidth && freshBounds.Height >= 24,
            "Fresh copies must fit beside the main action without adding a row at minimum size.");
    }

    private static void CheckGngListLayoutChoicePresentation(GngUpdateWindow window, GngUpdateFixtureState state)
    {
        var choice = Required<CheckBox>(window, "PreserveListLayout");
        var content = (FrameworkElement)window.Content;
        var bounds = Bounds(choice, content);
        var locked = state is GngUpdateFixtureState.CheckingSavedPackages or GngUpdateFixtureState.Downloading or GngUpdateFixtureState.DownloadingReference or
            GngUpdateFixtureState.PreparingReference or GngUpdateFixtureState.PreparingPackages or
            GngUpdateFixtureState.WaitingForLogin or GngUpdateFixtureState.ReferenceWaitingForLogin or GngUpdateFixtureState.Completion;
        Require(IsPresented(choice) && choice.IsChecked == true && choice.IsEnabled == !locked && choice.Focusable &&
            Equals(choice.Content, "Keep my list positions and visibility"),
            "List-layout preservation must default on, remain visible across states, and lock during active requests, work and completion.");
        Require(bounds.Left >= 0 && bounds.Right <= content.ActualWidth + .5 && bounds.Height >= 16 &&
            bounds.Bottom <= Bounds(Required<Border>(window, "DownloadActivityPanel"), content).Top,
            "List-layout preservation must be reachable before preview and outside failure, progress or browser panels.");
        var tooltip = choice.ToolTip as string;
        Require(tooltip is not null && tooltip.Contains("whole list", StringComparison.Ordinal) &&
            tooltip.Contains("columns, items, sorting", StringComparison.Ordinal) && tooltip.Contains("package list defaults", StringComparison.Ordinal),
            "The preservation option must explain its whole-list scope and how to use package defaults instead.");
    }

    private static void CheckGngBrowserInteraction(GngUpdateWindow window, GngUpdateFixtureState state)
    {
        var blocked = state is GngUpdateFixtureState.Downloading or GngUpdateFixtureState.DownloadingReference or
            GngUpdateFixtureState.PreparingReference or GngUpdateFixtureState.PreparingPackages;
        var surface = Required<Grid>(window, "BrowserSurface");
        var host = Required<ContentControl>(window, "BrowserHost");
        var overlay = Required<Grid>(window, "BrowserDownloadOverlay");
        var toolbar = Required<Grid>(window, "BrowserToolbar");
        Require(surface.Children.Contains(host) && surface.Children.OfType<TextBlock>().Any(),
            "The inert browser placeholder must use the same surface as the production browser host.");
        Require(surface.IsEnabled == !blocked && surface.IsHitTestVisible == !blocked &&
            host.IsEnabled == !blocked && host.IsHitTestVisible == !blocked,
            "Browser keyboard and pointer interaction must be disabled only during download or preparation.");
        Require(overlay.Visibility == (blocked ? Visibility.Visible : Visibility.Collapsed) &&
            surface.Visibility == (blocked ? Visibility.Hidden : Visibility.Visible) && toolbar.Opacity == (blocked ? .55 : 1),
            "The native browser surface must hide behind its replacement panel and restore afterward.");
        if (!blocked) return;

        var content = (FrameworkElement)window.Content;
        var panel = Required<Border>(window, "DownloadActivityPanel");
        var overlayBounds = Bounds(overlay, content);
        var surfaceBounds = Bounds(surface, content);
        Require(overlayBounds == surfaceBounds && overlayBounds.Height >= 150 &&
            Bounds(panel, content).Bottom < overlayBounds.Top && Bounds(toolbar, content).Bottom <= overlayBounds.Top,
            "The blocking overlay must cover only the browser content, leaving progress and cancellation outside it.");
        var title = Required<TextBlock>(window, "BrowserOverlayTitle");
        Require(title.Text == Required<TextBlock>(window, "DownloadStageText").Text &&
            title.TextWrapping == TextWrapping.Wrap && Bounds(title, overlay).Bottom <= overlay.ActualHeight,
            "The browser overlay must show the current stage without clipping at minimum size.");
        var action = Required<Button>(window, state == GngUpdateFixtureState.PreparingReference ? "StopRequestButton" : "CancelDownloadButton");
        var hint = Required<TextBlock>(window, "BrowserOverlayHint");
        if (state == GngUpdateFixtureState.PreparingPackages)
        {
            var status = Required<TextBox>(window, "StatusText");
            Require(!IsPresented(action) && !IsPresented(Required<Button>(window, "StopRequestButton")) &&
                !hint.Text.Contains("cancel", StringComparison.Ordinal) && !hint.Text.Contains("stop", StringComparison.Ordinal) &&
                status.IsEnabled && status.IsHitTestVisible && status.Focusable,
                "Package validation must leave its status accessible without promising unavailable cancellation.");
            return;
        }
        Require(hint.Text.Contains(state == GngUpdateFixtureState.PreparingReference ? "stop" : "cancel", StringComparison.Ordinal),
            "The covered browser should point to the currently available Stop or Cancel action.");
        Require(action.IsEnabled && action.IsHitTestVisible && action.Focusable && panel.IsEnabled && panel.IsHitTestVisible &&
            !overlayBounds.IntersectsWith(Bounds(action, content)),
            "Stop or Cancel must remain reachable by pointer and keyboard outside the blocked browser surface.");
    }

    private static void CheckGngActivityPanel(GngUpdateWindow window, GngUpdateFixtureState state)
    {
        var content = (FrameworkElement)window.Content;
        var panel = Required<Border>(window, "DownloadActivityPanel");
        var status = Required<TextBox>(window, "StatusText");
        var panelBounds = Bounds(panel, content);
        Require(IsPresented(panel) && panelBounds.Top >= 0 && panelBounds.Right <= content.ActualWidth + .5,
            "GNG operation status must remain visible inside the window.");
        foreach (var name in new[] { "BrowserPanel", "ReviewPanel", "CompletePanel" })
        {
            var body = Required<FrameworkElement>(window, name);
            if (IsPresented(body)) Require(panelBounds.Bottom <= Bounds(body, content).Top + .5,
                "GNG progress and diagnostics must stay above the " + name + " content.");
        }
        var active = state is GngUpdateFixtureState.CheckingSavedPackages or GngUpdateFixtureState.WaitingForLogin or GngUpdateFixtureState.ReferenceWaitingForLogin or GngUpdateFixtureState.Downloading or
            GngUpdateFixtureState.PreparingReference or GngUpdateFixtureState.DownloadingReference or GngUpdateFixtureState.PreparingPackages;
        var stage = Required<TextBlock>(window, "DownloadStageText");
        var failed = state is GngUpdateFixtureState.DownloadFailed or GngUpdateFixtureState.ReferenceFailed or GngUpdateFixtureState.ListLayoutFailed;
        Require(IsPresented(stage) == (active || failed), "The prominent stage heading must match the active download or error state.");
        if (active || failed)
        {
            Require(stage.FontSize >= 14 && stage.FontWeight.ToOpenTypeWeight() >= FontWeights.SemiBold.ToOpenTypeWeight(),
                "The active GNG stage needs a clearly readable heading.");
        }
        if (active && state is not (GngUpdateFixtureState.PreparingPackages or GngUpdateFixtureState.CheckingSavedPackages))
        {
            var controlName = state is GngUpdateFixtureState.Downloading or GngUpdateFixtureState.DownloadingReference
                ? "CancelDownloadButton" : "StopRequestButton";
            var action = Required<Button>(window, controlName);
            var actionBounds = Bounds(action, panel);
            Require(IsPresented(action) && action.IsEnabled && actionBounds.Left >= 0 && actionBounds.Right <= panel.ActualWidth + .5 &&
                actionBounds.Bottom <= panel.ActualHeight + .5, "The active GNG action must be reachable beside its status: " + controlName);
        }
        var progress = Required<ProgressBar>(window, "Activity");
        if (IsPresented(progress))
            Require(progress.ActualHeight >= 7 && Bounds(progress, panel).Bottom <= panel.ActualHeight + .5,
                "GNG transfer activity must be clearly visible inside the status panel.");
        if (state is not (GngUpdateFixtureState.DownloadFailed or GngUpdateFixtureState.ReferenceFailed)) return;
        Require(status.TextWrapping == TextWrapping.Wrap && status.LineCount >= 3 && status.IsReadOnlyCaretVisible,
            "Long download errors must wrap and remain keyboard-readable.");
        var scroll = LayoutValidation.Descendants(status).OfType<ScrollViewer>().Single();
        Require(scroll.ScrollableHeight > 0 && scroll.ViewportHeight >= 30 && scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible,
            "Long GNG diagnostics need a visible scrollbar and readable initial lines.");
        status.ScrollToHome(); DrainBindings(); content.UpdateLayout();
        Require(status.GetFirstVisibleLineIndex() == 0, "The failure summary must be visible before scrolling.");
        status.ScrollToEnd(); DrainBindings(); content.UpdateLayout();
        Require(scroll.VerticalOffset >= scroll.ScrollableHeight - 1,
            "The final download error details cannot be reached by scrolling.");
        status.Select(status.Text.Length - 1, 1);
        Require(status.SelectedText == ".", "The final diagnostic text must remain selectable.");
        status.Select(0, 0); status.ScrollToHome(); DrainBindings(); content.UpdateLayout();
        CheckFooter(window);
    }

    private static void CheckGngBrowserLayout(GngUpdateWindow window, GngUpdateFixtureState state)
    {
        var content = (FrameworkElement)window.Content;
        var toolbar = Required<Grid>(window, "BrowserToolbar");
        var retry = Required<Button>(window, "RetryDownloadButton");
        var stop = Required<Button>(window, "StopRequestButton");
        var origin = Required<TextBlock>(window, "OriginText");
        Require(IsPresented(toolbar) && !IsPresented(Required<FrameworkElement>(window, "StartPanel")),
            "The browser state must show navigation and request controls without the start overlay.");
        foreach (var element in new FrameworkElement[] { retry, origin })
        {
            var bounds = Bounds(element, content);
            Require(IsPresented(element) && bounds.Width > 20 && bounds.Height > 12 &&
                bounds.Left >= -.5 && bounds.Right <= content.ActualWidth + .5 && bounds.Bottom <= content.ActualHeight + .5,
                "The GNG browser toolbar clips " + element.Name + ".");
        }
        Require(origin.ActualWidth > 250 && origin.Text == "https://files.aero-nav.com",
            "The official origin needs a readable row without credentials or callback parameters.");
        Require(!IsPresented(Required<Button>(window, "InstallButton")), "Browsing or downloading must not expose installation before review.");
        if (state is GngUpdateFixtureState.WaitingForLogin or GngUpdateFixtureState.ReferenceWaitingForLogin)
        {
            Require(!retry.IsEnabled && stop.IsEnabled, "Waiting for sign-in must prevent duplicate requests and retain Stop.");
            stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(window.SyntheticState == (state == GngUpdateFixtureState.ReferenceWaitingForLogin
                ? GngUpdateFixtureState.ReferenceCancelled : GngUpdateFixtureState.DownloadCancelled) && retry.IsEnabled,
                "Stopping the inert request must expose an explicit retry.");
        }
        else if (state == GngUpdateFixtureState.PreparingPackages)
        {
            Require(Required<ProgressBar>(window, "Activity").IsIndeterminate && !retry.IsEnabled && !stop.IsEnabled,
                "Final package validation must retain activity without allowing duplicate requests.");
            foreach (var name in new[] { "ImportButton", "ExternalBrowserButton", "RestoreButton", "CloseButton" })
                Require(!Required<Button>(window, name).IsEnabled, "Package validation leaves a competing action enabled: " + name);
        }
        else if (state == GngUpdateFixtureState.PreparingReference)
        {
            var progress = Required<ProgressBar>(window, "Activity");
            Require(IsPresented(progress) && progress.IsIndeterminate && stop.IsEnabled && !retry.IsEnabled &&
                !IsPresented(Required<Button>(window, "CancelDownloadButton")),
                "Validation between downloads needs visible activity and an enabled Stop request action.");
            foreach (var name in new[] { "ImportButton", "ExternalBrowserButton", "RestoreButton", "CloseButton" })
                Require(!Required<Button>(window, name).IsEnabled, "Reference preparation leaves a competing action enabled: " + name);
            stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(window.SyntheticState == GngUpdateFixtureState.ReferenceCancelled && retry.IsEnabled,
                "Stopping between downloads must require an explicit reference retry.");
        }
        else if (state is GngUpdateFixtureState.Downloading or GngUpdateFixtureState.DownloadingReference)
        {
            var progress = Required<ProgressBar>(window, "Activity");
            var cancel = Required<Button>(window, "CancelDownloadButton");
            Require(IsPresented(progress) && !progress.IsIndeterminate && progress.Value == 42,
                "The active download must show its determinate synthetic progress.");
            if (state == GngUpdateFixtureState.DownloadingReference)
                Require(Required<TextBox>(window, "StatusText").Text.Contains("cleanup reference", StringComparison.Ordinal),
                    "The second download must explain that Full is a reference, while Update Only remains the install package.");
            Require(IsPresented(cancel) && cancel.IsEnabled && !retry.IsEnabled && !IsPresented(stop),
                "An active transfer needs one cancellation action and no duplicate request action.");
            foreach (var name in new[] { "ImportButton", "ExternalBrowserButton", "RestoreButton", "CloseButton" })
                Require(!Required<Button>(window, name).IsEnabled, "An active download leaves a competing action enabled: " + name);
            var cancelBounds = Bounds(cancel, content);
            Require(cancelBounds.Right <= content.ActualWidth + .5 && cancelBounds.Bottom <= content.ActualHeight + .5,
                "Download cancellation is clipped by the minimum-size footer.");
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(window.SyntheticState == (state == GngUpdateFixtureState.DownloadingReference
                    ? GngUpdateFixtureState.ReferenceCancelled : GngUpdateFixtureState.DownloadCancelled) && retry.IsEnabled,
                "Cancelling the inert transfer must return to an explicit retry state.");
        }
        else
        {
            Require(retry.IsEnabled && !stop.IsEnabled && !IsPresented(Required<ProgressBar>(window, "Activity")),
                "Cancelled download must remain idle with explicit retry available.");
            if (state is GngUpdateFixtureState.ReferenceCancelled or GngUpdateFixtureState.ReferenceFailed)
            {
                var startOver = Required<Button>(window, "StartOverButton");
                var startOverBounds = Bounds(startOver, content);
                Require(IsPresented(startOver) && startOver.IsEnabled && startOverBounds.Right <= content.ActualWidth + .5 &&
                    startOverBounds.Bottom <= content.ActualHeight + .5,
                    "A retained Update Only package needs a reachable Start over action.");
                Require(Equals(retry.Content, "Retry reference") && Equals(Required<Button>(window, "ImportButton").Content, "Import reference ZIP…"),
                    "Retained Update Only must clearly request its Full reference without changing the install package.");
                startOver.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(window.SyntheticState == GngUpdateFixtureState.Start, "Start over must reset the inert retained-package state.");
                window.SetSyntheticState(state);
            }
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(window.SyntheticState == (state is GngUpdateFixtureState.ReferenceCancelled or GngUpdateFixtureState.ReferenceFailed
                    ? GngUpdateFixtureState.DownloadingReference : GngUpdateFixtureState.WaitingForLogin),
                "The retry action must restart only the inert request state.");
        }
        LayoutValidation.CheckWindowContent(window);
        DrainBindings();
        CheckGngBrowserInteraction(window, window.SyntheticState);
        Require(Required<ContentControl>(window, "BrowserHost").Content is null,
            "Synthetic browser controls must never initialize browser content.");
    }

    private static void CheckGngEuroScopeGuard(GngUpdateWindow window)
    {
        var originalState = window.SyntheticState;
        var confirm = Required<CheckBox>(window, "ConfirmInstall");
        var install = Required<Button>(window, "InstallButton");
        var restore = Required<Button>(window, "RestoreButton");
        var notice = Required<TextBlock>(window, "EuroScopeNotice");
        confirm.IsChecked = true;
        Require(install.IsEnabled, "The synthetic closed state must allow an explicitly confirmed review.");
        foreach (var reason in new[]
        {
            "EuroScope is running. Close all EuroScope instances before changing GNG or VatEFS files.",
            "Launchpad could not check whether EuroScope is running. Close all EuroScope instances and try again before changing GNG or VatEFS files."
        })
        {
            window.SetSyntheticEuroScopeState(reason);
            LayoutValidation.CheckWindowContent(window);
            Require(IsPresented(notice) && notice.Text == reason && notice.TextWrapping == TextWrapping.Wrap,
                "A running or unreadable EuroScope state needs a visible, wrapped explanation.");
            Require(!confirm.IsEnabled && confirm.IsChecked != true && !install.IsEnabled && !restore.IsEnabled,
                "EuroScope must block installation and restoration and discard earlier confirmation.");
            install.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(window.SyntheticState == originalState, "Blocked actions must not enter installation or restore.");
            Require(Required<Button>(window, "ImportButton").IsEnabled && Required<Button>(window, "ChooseAnotherButton").IsEnabled,
                "The EuroScope guard must retain package import and review.");
            CheckFooter(window);
            window.SetSyntheticEuroScopeState(null);
            Require(!IsPresented(notice) && confirm.IsEnabled && restore.IsEnabled && !install.IsEnabled,
                "Closing EuroScope should reenable review controls without automatically approving installation.");
            confirm.IsChecked = true;
            Require(install.IsEnabled, "A new explicit confirmation should be accepted once EuroScope is closed.");
        }
        confirm.IsChecked = false;
    }

    private static void CheckGngReview(GngUpdateWindow window)
    {
        var grid = Required<DataGrid>(window, "FilesGrid");
        var warnings = Required<TextBox>(window, "WarningsText");
        var confirm = Required<CheckBox>(window, "ConfirmInstall");
        var install = Required<Button>(window, "InstallButton");
        CheckGngReviewFilter(window);
        var preserve = Required<CheckBox>(window, "PreserveListLayout");
        confirm.IsChecked = true;
        preserve.IsChecked = false;
        Require(confirm.IsChecked != true && !install.IsEnabled && grid.Items.Cast<GngUpdateFile>().All(file => file.Action != "Merge list layout"),
            "Changing the list-preservation choice must discard confirmation and refresh the synthetic preview.");
        preserve.IsChecked = true;
        LayoutValidation.CheckWindowContent(window);
        Require(confirm.IsChecked != true && !install.IsEnabled && grid.Items.Cast<GngUpdateFile>().Any(file => file.Action == "Merge list layout"),
            "Re-enabling preservation must restore a merged preview without approving it automatically.");
        Require(grid.IsReadOnly && !grid.CanUserAddRows && !grid.CanUserDeleteRows && grid.Items.Count > 0,
            "GNG file review must show synthetic changes without editable or removable entries.");
        Require(grid.ActualHeight >= 90 && grid.ActualWidth > 450, "GNG review has too little room for readable file rows.");
        Require(warnings.IsReadOnly && warnings.Focusable && warnings.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
            "GNG warnings and backup location need a selectable, scrollable review area.");
        Require(confirm.IsChecked != true && IsPresented(install) && !install.IsEnabled,
            "GNG review must start unchecked with installation unavailable.");
        var content = (FrameworkElement)window.Content;
        var confirmationBounds = Bounds(confirm, content);
        Require(confirmationBounds.Left >= -.5 && confirmationBounds.Right <= content.ActualWidth + .5 &&
            confirmationBounds.Bottom <= content.ActualHeight + .5, "GNG installation confirmation is clipped.");
        window.SetSyntheticReviewProgress();
        LayoutValidation.CheckWindowContent(window);
        Require(!preserve.IsEnabled, "Checking a reviewed package must lock its list-preservation choice.");
        Require(grid.ActualHeight >= 90, $"Long GNG warnings/status must leave the file review available (actual height: {grid.ActualHeight:0.0}px).");
        var installBounds = Bounds(install, content);
        Require(installBounds.Left >= -.5 && installBounds.Top >= -.5 && installBounds.Right <= content.ActualWidth + .5 &&
            installBounds.Bottom <= content.ActualHeight + .5, "The reviewed GNG install action is clipped by the footer.");
        CheckFooter(window);
        confirm.IsChecked = true;
        Require(install.IsEnabled, "Explicit GNG confirmation did not enable the inert install action.");
        Required<Button>(window, "ChooseAnotherButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(confirm.IsChecked != true && !install.IsEnabled, "Changing package must discard prior installation confirmation.");
    }

    private static void CheckGngReviewFilter(GngUpdateWindow window)
    {
        var grid = Required<DataGrid>(window, "FilesGrid");
        var toggle = Required<CheckBox>(window, "ShowUnchangedFiles");
        var summary = Required<TextBlock>(window, "FileSummaryText");
        var empty = Required<TextBlock>(window, "NoFileChangesText");
        var expectedChanges = new[] { "ESAA APP ACC.prf", "ESAA/Plugins/TopSky.dll", "ESAA/Settings/Lists.txt" };
        Require(toggle.IsChecked != true && toggle.IsEnabled && IsPresented(toggle),
            "GNG review must default to changes only, with the optional unchanged-file toggle available.");
        Require(grid.Items.Cast<GngUpdateFile>().Select(row => row.RelativePath).SequenceEqual(expectedChanges),
            "The production review filter must show exactly the changed profile, plugin and merged list layout.");
        Require(summary.Text == "3 files to change · 1 unchanged · 2 personal files kept",
            "Review counts must distinguish changed, identical and preserved personal files.");
        Require(IsPresented(grid) && !IsPresented(empty), "A changed-files review must display the table instead of the empty message.");
        var viewButton = LayoutValidation.Descendants(grid).OfType<Button>().FirstOrDefault(button => Equals(button.Content, "View…"));
        Require(viewButton is not null && viewButton.DataContext is GngUpdateFile,
            "Changed file rows need a direct, per-file View action.");
        var viewBounds = Bounds(viewButton!, grid);
        Require(viewBounds.Width >= 40 && viewBounds.Left >= 0 && viewBounds.Right <= grid.ActualWidth + .5,
            "The per-file View action must remain readable and inside the grid at minimum width.");
        viewButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(Required<TextBox>(window, "StatusText").Text.Contains("Synthetic selected-file preview request", StringComparison.Ordinal),
            "The file row View action must route to an inert fixture handler.");
        CheckGngListLayoutRow(window);
        toggle.IsChecked = true;
        LayoutValidation.CheckWindowContent(window);
        Require(grid.Items.Count == 6 && grid.Items.Cast<GngUpdateFile>().Count(row => row.Action == "Keep personal file") == 2 &&
            grid.Items.Cast<GngUpdateFile>().Any(row => row.Action == "Unchanged"),
            "Show unchanged files must expose every entry, including preserved personal files.");
        Require(summary.Text == "3 files to change · 1 unchanged · 2 personal files kept",
            "Showing unchanged entries must not change the underlying plan counts.");
        window.SetSyntheticReviewRows(GngUpdateWindow.UnchangedRows());
        LayoutValidation.CheckWindowContent(window);
        Require(toggle.IsChecked != true && grid.Items.Count == 0 && !IsPresented(grid) && IsPresented(empty),
            "A new zero-change plan must reset the filter and explain why its table is empty.");
        Require(summary.Text == "0 files to change · 1 unchanged · 2 personal files kept" && !string.IsNullOrWhiteSpace(empty.Text),
            "Zero-change review must keep accurate counts and a visible explanation.");
        var panel = Required<FrameworkElement>(window, "ReviewPanel");
        var emptyBounds = Bounds(empty, panel);
        Require(emptyBounds.Width > 250 && emptyBounds.Left >= -.5 && emptyBounds.Right <= panel.ActualWidth + .5 &&
            emptyBounds.Bottom <= panel.ActualHeight + .5, "The zero-change explanation is clipped at minimum size.");
        toggle.IsChecked = true;
        LayoutValidation.CheckWindowContent(window);
        Require(grid.Items.Count == 3 && IsPresented(grid) && !IsPresented(empty),
            "A zero-change plan must still let the user inspect its unchanged/preserved entries.");
        window.SetSyntheticReviewRows([GngUpdateWindow.ExampleRows()[0]]);
        Require(toggle.IsChecked != true && !toggle.IsEnabled && grid.Items.Count == 1,
            "A new all-changing plan must reset the toggle and disable an empty unchanged option.");
        window.SetSyntheticReviewRows(GngUpdateWindow.ExampleRows());
        LayoutValidation.CheckWindowContent(window);
        Require(toggle.IsChecked != true && grid.Items.Count == 3, "A fresh mixed plan must restore the changes-only default.");
        foreach (var element in new FrameworkElement[] { toggle, summary })
        {
            var bounds = Bounds(element, panel);
            Require(IsPresented(element) && bounds.Width > 20 && bounds.Left >= -.5 && bounds.Right <= panel.ActualWidth + .5 &&
                bounds.Bottom <= panel.ActualHeight + .5, "The review filter/count control is clipped: " + element.Name);
        }
    }

    private static void CheckGngListLayoutRow(GngUpdateWindow window)
    {
        var grid = Required<DataGrid>(window, "FilesGrid");
        var file = grid.Items.Cast<GngUpdateFile>().Single(row => row.Action == "Merge list layout");
        Require(file.WritesFile && file.Detail == "Keep list positions and visibility; refresh package columns, items and plugin settings.",
            "List layout preservation must remain a changed file with an accurate review explanation.");
        grid.ScrollIntoView(file);
        DrainBindings(); LayoutValidation.CheckWindowContent(window);
        var row = grid.ItemContainerGenerator.ContainerFromItem(file) as DataGridRow;
        Require(row is not null, "The merged list-layout row must be reachable by scrolling.");
        var descendants = LayoutValidation.Descendants(row!).ToArray();
        foreach (var value in new[] { file.Action, file.RelativePath, file.Detail })
        {
            var text = descendants.OfType<TextBlock>().Single(element => element.Text == value);
            Require(text.TextWrapping == TextWrapping.Wrap && text.ActualWidth > 50 && text.ActualHeight >= text.FontSize,
                "Merged list-layout labels and explanation must wrap and stay readable: " + value);
        }
        var action = descendants.OfType<Button>().Single(button => Equals(button.Content, "View…"));
        action.BringIntoView(); DrainBindings(); ((FrameworkElement)window.Content).UpdateLayout();
        var bounds = Bounds(action, grid);
        Require(action.IsEnabled && action.Focusable && bounds.Width >= 40 && bounds.Left >= 0 &&
            bounds.Right <= grid.ActualWidth + .5 && bounds.Top >= 0 && bounds.Bottom <= grid.ActualHeight + .5,
            "The merged list-layout View action must be fully reachable at minimum size.");
        action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(Required<TextBox>(window, "StatusText").Text.Contains("Synthetic selected-file preview request", StringComparison.Ordinal),
            "The merged list-layout View action must route to the inert fixture handler.");
    }

    private static void CheckWizardGngChoice(SetupWizardWindow window)
    {
        var choice = Required<CheckBox>(window, "GngAfterFinishChoice");
        Require(IsPresented(choice) && choice.IsEnabled && choice.IsChecked != true,
            "Wizard GNG follow-up must be visible, optional and unchecked on the final page.");
        choice.BringIntoView(); DrainBindings(); ((FrameworkElement)window.Content).UpdateLayout();
        var scroll = Required<ScrollViewer>(window, "ContentScroll");
        Require(new Rect(0, 0, scroll.ViewportWidth, scroll.ViewportHeight).IntersectsWith(Bounds(choice, scroll)),
            "The optional GNG setup choice cannot be reached by scrolling.");
        window.SyntheticNext();
        Require(!window.SyntheticGngSetupRequested, "Finishing setup without opt-in must not request GNG setup.");
        choice.IsChecked = true;
        window.SetSyntheticRestart(true);
        Require(!choice.IsEnabled && choice.IsChecked != true, "A restart requirement must disable and clear the GNG follow-up choice.");
        window.SyntheticNext();
        Require(!window.SyntheticGngSetupRequested, "A restart requirement must suppress GNG setup after Finish.");
        window.SetSyntheticRestart(false);
        Require(choice.IsEnabled && choice.IsChecked != true, "Releasing the restart guard must not opt into GNG automatically.");
        choice.IsChecked = true;
        window.SyntheticNext();
        Require(window.SyntheticGngSetupRequested, "Finish should record an explicit GNG follow-up request in memory.");
        choice.IsChecked = false;
        Required<TextBlock>(window, "StatusText").Text = "";
    }
}
