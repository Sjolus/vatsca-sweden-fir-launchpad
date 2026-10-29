using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

// Model-only checks. Synthetic paths/files only; no application or installer is started.
var temporary = Path.Combine(Path.GetTempPath(), "launchpad-presentation-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
var fakeExe = Path.Combine(temporary, "synthetic-app.exe");
File.WriteAllText(fakeExe, "Synthetic existence fixture; never executed.");
var passed = 0;
try
{
    Run("GNG preparation retains progress and cancellation while other actions are blocked", () =>
    {
        var row = new CheckResult { AppName = "EuroScope (GNG Pack)", HasFontsCheck = true,
            SoftwareActionsAllowed = false, ApplicationActionsAllowed = false,
            GngOperation = new("Downloading package 1 of 2", "Downloading…", Busy: true, CanCancel: true, Percent: 42) };
        Equal(CheckRowAction.GngCancel, row.RowAction);
        Equal(true, row.RowActionEnabled);
        Equal(true, row.RowProgressVisible);
        Equal(false, row.RowProgressIndeterminate);
        Equal(42d, row.RowProgressValue);
        Equal("Downloading…", row.RowStatusText);
        row.GngOperation = new("Rechecking files", "Installing…", Busy: true);
        Equal(CheckRowAction.None, row.RowAction);
        Equal(true, row.RowProgressIndeterminate);
    });
    Run("GNG review remains reachable and clearing it restores the real update state", () =>
    {
        var row = new CheckResult { AppName = "EuroScope (GNG Pack)", HasFontsCheck = true,
            LaunchPath = temporary, Status = CheckStatus.UpdateAvailable, SoftwareActionsAllowed = false,
            GngOperation = new("Close EuroScope to install", "Ready to review", NeedsAttention: true) };
        Equal(CheckRowAction.GngContinue, row.RowAction);
        Equal(true, row.RowActionEnabled);
        Equal(false, row.RowProgressVisible);
        row.GngOperation = null;
        Equal(CheckRowAction.GngUpdate, row.RowAction);
        Equal(false, row.RowActionEnabled);
        row.SoftwareActionsAllowed = true;
        Equal(true, row.RowActionEnabled);
    });
    Run("a managed GNG testing package is not labelled as a published release", () =>
    {
        var row = new CheckResult { AppName = "EuroScope (GNG Pack)", HasFontsCheck = true,
            LaunchPath = temporary, GngIsTesting = true, Status = CheckStatus.UpToDate };
        Equal("Testing package", row.RowStatusText);
        Equal("Neutral", row.RowStatusKind);
        Equal(CheckRowAction.None, row.RowAction);
        row.Status = CheckStatus.UpdateAvailable;
        Equal("Update available", row.RowStatusText);
        Equal(CheckRowAction.GngUpdate, row.RowAction);
        row.GngIsTesting = false;
        row.Status = CheckStatus.Installed;
        Equal("Installed", row.RowStatusText);
    });
    Run("Apps groups EuroScope components without changing operation indices", () =>
    {
        var source = AppRows();
        var displayed = ApplicationRows.Visible(source);
        Equal("EuroScope|EuroScope (GNG Pack)|VatEFS|SAAMAN|TrackAudio|VACS|vATIS|VATIRIS|Sweden FIR Launchpad",
            string.Join('|', displayed.Select(row => row.AppName)));
        Equal("TrackAudio", source[2].AppName);
        Equal("VatEFS", source[5].AppName);
        foreach (var row in displayed) True(ReferenceEquals(source.Single(candidate => candidate.AppName == row.AppName), row));
        True(displayed.Skip(1).Take(3).All(row => row.IsEuroScopeChild));
        True(!source[0].IsEuroScopeChild && !source[2].IsEuroScopeChild);
    });
    Run("unused applications disappear and return with their saved state intact", () =>
    {
        var source = AppRows();
        foreach (var name in new[] { "TrackAudio", "VatEFS", "SAAMAN", "EuroScope (GNG Pack)" })
        {
            var row = source.Single(candidate => candidate.AppName == name);
            row.LaunchPath = name == "VatEFS" ? "http://localhost:17770" : fakeExe;
            row.InstalledVersion = "v1.2.3";
            row.IsRunning = true;
            string path = row.LaunchPath;
            row.IsNotUsed = true;
            True(!ApplicationRows.Visible(source).Contains(row));
            Equal(path, row.LaunchPath);
            Equal("v1.2.3", row.InstalledVersion);
            True(row.IsRunning);
            row.IsNotUsed = false;
            True(ApplicationRows.Visible(source).Contains(row));
        }
        Equal(9, ApplicationRows.Visible(source).Length);
    });
    Run("hiding EuroScope leaves separately chosen components visible", () =>
    {
        var source = AppRows();
        source[0].IsNotUsed = true;
        var visible = ApplicationRows.Visible(source);
        Equal(8, visible.Length);
        Equal("EuroScope (GNG Pack)|VatEFS|SAAMAN", string.Join('|', visible.Take(3).Select(row => row.AppName)));
        True(visible.Take(3).All(row => row.IsEuroScopeChild));
    });
    Run("Apps retains Launchpad when all optional applications are hidden", () =>
    {
        var source = AppRows();
        foreach (var row in source) row.IsNotUsed = true;
        True(ReferenceEquals(source[^1], ApplicationRows.Visible(source).Single()));
    });
    Run("verified fresh installation remains visible before another online check", () =>
    {
        var row = new CheckResult { AppName = "TrackAudio", SoftwareApp = SoftwareApp.TrackAudio, LaunchPath = fakeExe,
            Status = CheckStatus.Installed, InstalledVersion = "v1.4.0" };
        Equal("Installed", row.RowStatusText);
        Equal("Success", row.RowStatusKind);
        Equal("Installation verified", row.RowExplanation);
        Equal("v1.4.0", row.DisplayInstalledVersion);
        Equal("Not checked", row.DisplayLatestVersion);
        row.SoftwareUpdate = new(SoftwareApp.TrackAudio, SoftwareUpdatePhase.Error, "Network unavailable");
        Equal("Needs attention", row.RowStatusText);
    });
    Run("SAAMAN is managed as a plugin and never exposes standalone launch or stop", () =>
    {
        var row = new CheckResult { AppName = "SAAMAN", HasSaamanManagement = true, LaunchPath = fakeExe,
            InstalledVersion = "v1.3.36", LatestVersion = "v1.3.37", Status = CheckStatus.UpdateAvailable,
            DownloadUrl = "https://github.com/Kaskobiii/SAAMAN/releases" };
        True(!row.ShowLaunch && !row.ShowSimpleLaunch && !row.ShowSplitLaunch && !row.ShowDownload);
        Equal("Latest stable", row.LatestVersionCaption);
        Equal("Update available", row.RowStatusText);
        row.IsRunning = true;
        True(!row.ShowRunningActions);
        row.IsRunning = false;
        row.IsNotUsed = true;
        Equal("Not used", row.RowStatusText);
        row.IsNotUsed = false;
        row.Status = CheckStatus.NotConfigured;
        True(row.RowExplanation.Contains("SAAMAN.dll"));
    });
    Run("SAAMAN row shows progress and keeps Cancel reachable while other actions are blocked", () =>
    {
        var row = new CheckResult { HasSaamanManagement = true, Status = CheckStatus.UpdateAvailable,
            SoftwareActionsAllowed = false, ApplicationActionsAllowed = false,
            SaamanOperation = new("Downloading the verified DLL…", Busy: true, Percent: 42, CanCancel: true) };
        Equal("Updating 42%", row.RowStatusText);
        True(row.RowProgressVisible && !row.RowProgressIndeterminate);
        Equal(42d, row.RowProgressValue);
        RowAction(row, CheckRowAction.SaamanCancel, "Cancel", true);
        True(row.DetailText.Contains("Downloading the verified DLL"));
        row.SaamanOperation = row.SaamanOperation with { Message = "Applying the update…", Percent = null, CanCancel = false };
        Equal("Updating…", row.RowStatusText);
        Equal("Keep Launchpad open", row.RowExplanation);
        True(row.RowProgressVisible && row.RowProgressIndeterminate);
        RowAction(row, CheckRowAction.None, "", false);
    });
    Run("SAAMAN update failure survives a successful local DLL refresh", () =>
    {
        var row = new CheckResult { HasSaamanManagement = true, Status = CheckStatus.UpdateAvailable,
            SaamanOperation = new("Download failed. The installed DLL is unchanged.", Warning: true) };
        row.Status = CheckStatus.UpToDate;
        row.StatusMessage = "Installed SAAMAN 1.3.37.";
        Equal("Needs attention", row.RowStatusText);
        Equal("Warning", row.RowStatusKind);
        True(row.DetailText.StartsWith("Download failed.", StringComparison.Ordinal));
        True(row.DetailText.Contains("Installed SAAMAN"));
        True(!row.RowProgressVisible);
        RowAction(row, CheckRowAction.SaamanManage, "Retry", true);
        row.ApplicationActionsAllowed = false;
        RowAction(row, CheckRowAction.SaamanManage, "Retry", false);
        row.ApplicationActionsAllowed = true;
        row.Status = CheckStatus.NotConfigured;
        RowAction(row, CheckRowAction.Setup, "Set up", true);
        row.Status = CheckStatus.Error;
        RowAction(row, CheckRowAction.Setup, "Set up", true);
    });
    Run("SAAMAN cancelled and successful results stay visible until the next check clears them", () =>
    {
        var row = new CheckResult { HasSaamanManagement = true, Status = CheckStatus.UpdateAvailable,
            SaamanOperation = new("Preparation cancelled. No files changed.", Cancelled: true) };
        Equal("Update cancelled", row.RowStatusText);
        RowAction(row, CheckRowAction.SaamanManage, "Retry", true);
        row.SaamanOperation = new("SAAMAN 1.3.37 installed.");
        row.Status = CheckStatus.UpToDate;
        Equal("Updated", row.RowStatusText);
        Equal("Success", row.RowStatusKind);
        True(!row.RowProgressVisible);
        RowAction(row, CheckRowAction.None, "", false);
        row.SaamanOperation = null;
        row.Status = CheckStatus.Checking;
        Equal("Checking…", row.RowStatusText);
        True(!row.DetailText.Contains("SAAMAN 1.3.37 installed."));
        RowAction(row, CheckRowAction.None, "", false);
    });
    Run("SAAMAN persistence and runtime problems offer explicit remedies without another update", () =>
    {
        var row = new CheckResult { HasSaamanManagement = true, Status = CheckStatus.UpToDate,
            SaamanOperation = new("Updated; saving the location failed.", Warning: true, CanRetrySave: true) };
        Equal("Needs attention", row.RowStatusText);
        RowAction(row, CheckRowAction.SaamanRetrySave, "Retry save", true);
        True(row.RowActionTooltip.Contains("without downloading"));
        row.SoftwareActionsAllowed = false;
        RowAction(row, CheckRowAction.SaamanRetrySave, "Retry save", false);
        row.SoftwareActionsAllowed = true;
        row.SaamanOperation = new("SAAMAN needs WebView2.", Warning: true, RuntimeRequired: true);
        RowAction(row, CheckRowAction.Setup, "Set up", true);
        True(row.RowExplanation.Contains("WebView2") && row.RowActionTooltip.Contains("WebView2"));
        row.SaamanOperation = new("Cancelled after installation; some changes remain.", Warning: true, Cancelled: true);
        Equal("Needs attention", row.RowStatusText);
    });
    Run("SAAMAN operation changes notify row actions, diagnostics and progress", () =>
    {
        var row = new CheckResult { HasSaamanManagement = true };
        var notices = new HashSet<string?>();
        row.PropertyChanged += (_, args) => notices.Add(args.PropertyName);
        foreach (var state in new SaamanOperationState?[]
        {
            new("Checking…", Busy: true, CanCancel: true),
            new("Downloading…", Busy: true, Percent: 35, CanCancel: true),
            new("Applying…", Busy: true), new("Failed.", Warning: true), null
        })
        {
            notices.Clear(); row.SaamanOperation = state;
            foreach (var name in new[] { nameof(CheckResult.SaamanOperation), nameof(CheckResult.RowStatusText),
                nameof(CheckResult.RowStatusKind), nameof(CheckResult.RowExplanation), nameof(CheckResult.DetailText),
                nameof(CheckResult.RowAction), nameof(CheckResult.RowActionEnabled), nameof(CheckResult.RowActionText),
                nameof(CheckResult.RowProgressVisible), nameof(CheckResult.RowProgressValue), nameof(CheckResult.RowProgressIndeterminate) })
                True(notices.Contains(name));
        }
    });
    Run("shared row progress preserves vendor updates and ignores SAAMAN state on other rows", () =>
    {
        var row = Software(SoftwareApp.VatEfs, SoftwareUpdatePhase.Downloading);
        row.SoftwareExecutablePath = fakeExe;
        var notices = new HashSet<string?>();
        row.PropertyChanged += (_, args) => notices.Add(args.PropertyName);
        row.SoftwareUpdate = row.SoftwareUpdate! with { ProgressPercent = 25 };
        True(row.RowProgressVisible && !row.RowProgressIndeterminate);
        Equal(25d, row.RowProgressValue);
        foreach (var name in new[] { nameof(CheckResult.RowProgressVisible), nameof(CheckResult.RowProgressValue), nameof(CheckResult.RowProgressIndeterminate) })
            True(notices.Contains(name));
        row.SaamanOperation = new("Unrelated", Busy: true, Percent: 99, CanCancel: true);
        Equal(25d, row.RowProgressValue);
        row.SoftwareUpdate = row.SoftwareUpdate with { Phase = SoftwareUpdatePhase.Installing, ProgressPercent = null };
        True(row.RowProgressVisible && row.RowProgressIndeterminate);
        row.SoftwareUpdate = row.SoftwareUpdate with { Phase = SoftwareUpdatePhase.Completed };
        True(!row.RowProgressVisible && !row.RowProgressIndeterminate);
        Equal("Updated", row.RowStatusText);
    });
    Run("unknown, missing and unreported versions remain distinct", () =>
    {
        var row = new CheckResult { AppName = "EuroScope", HasEuroScopeManagement = true };
        Equal("Not checked", row.DisplayInstalledVersion);
        row.Status = CheckStatus.NotConfigured;
        Equal("Not set up", row.DisplayInstalledVersion);
        row.Status = CheckStatus.Error;
        Equal("Not reported", row.DisplayInstalledVersion);
        row.InstalledVersion = "Executable not found";
        Equal("Not found", row.DisplayInstalledVersion);
        True(row.DetailText.Contains("Executable not found"));
    });
    Run("web service and unknown plugin version remain distinct", () =>
    {
        var web = new CheckResult { AppName = "VATIRIS", IsWebApp = true, InstalledVersion = "N/A", LatestVersion = "N/A", Status = CheckStatus.WebApp };
        Equal("Not applicable", web.DisplayInstalledVersion);
        Equal("Not applicable", web.DisplayLatestVersion);
        var plugin = new CheckResult { AppName = "VatEFS", IsWebApp = true, IsLocalUrl = true, InstalledVersion = "Installed", Status = CheckStatus.Installed };
        Equal("Not reported", plugin.DisplayInstalledVersion);
        Equal("Includes prereleases", plugin.LatestVersionCaption);
        Equal("Plugin detected", plugin.RowStatusText);
    });
    Run("VatEFS prereleases and release links retain browser launch", () =>
    {
        var row = new CheckResult { AppName = "VatEFS", IsWebApp = true, IsLocalUrl = true, IsLocalUrlReachable = true,
            LaunchPath = "http://localhost:17770", Status = CheckStatus.UpdateAvailable, InstalledVersion = "v0.0.14", LatestVersion = "v0.0.15",
            LatestIsPrerelease = true, DownloadUrl = "https://github.com/minsulander/vatefs/releases/tag/v0.0.15" };
        Equal("Latest prerelease", row.LatestVersionCaption);
        Equal("Open releases ↗", row.DownloadActionText);
        True(row.ShowDownload && row.ShowLaunch && row.ShowSimpleLaunch);
        Equal("Open", row.LaunchActionText);
        Equal("Review the VatEFS release", row.RowExplanation);
        row.Status = CheckStatus.Installed;
        row.InstalledVersion = "Installed";
        True(row.ShowDownload && row.ShowLaunch);
        Equal("Not reported", row.DisplayInstalledVersion);
        row.Status = CheckStatus.Error;
        True(row.ShowDownload && row.ShowLaunch);
        row.LatestIsPrerelease = false;
        Equal("Latest release", row.LatestVersionCaption);
        row.Status = CheckStatus.Checking;
        True(!row.ShowDownload && row.ShowLaunch);
    });
    Run("all four clients expose installation when absent", () =>
    {
        foreach (var app in Enum.GetValues<SoftwareApp>())
        {
            var row = Software(app, SoftwareUpdatePhase.Unavailable);
            row.LaunchPath = app == SoftwareApp.VatEfs ? "http://localhost:17770" : "";
            row.SoftwareExecutablePath = "";
            True(row.SoftwareNeedsSetup && row.ShowSoftwareSetup && !row.ShowSoftwareAction);
            Equal("Install…", row.SoftwareSetupText);
            Equal("Not set up", row.RowStatusText);
        }
    });
    Run("VatEFS browser URL is independent of installation and process guards", () =>
    {
        var row = Software(SoftwareApp.VatEfs, SoftwareUpdatePhase.Available);
        row.SoftwareExecutablePath = fakeExe;
        row.LaunchPath = "http://localhost:17770";
        row.IsLocalUrlReachable = true;
        row.IsRunning = true;
        True(!row.SoftwareNeedsSetup && row.ShowSoftwareAction && row.SoftwareActionEnabled);
        True(row.ShowLaunch && row.LaunchEnabled);
        row.SoftwareBlockingReason = "EuroScope is running. Close all instances before changing VatEFS files.";
        True(!row.SoftwareActionEnabled && row.DetailText.Contains(row.SoftwareBlockingReason));
        True(row.ShowLaunch && row.LaunchEnabled);
        row.SoftwareBlockingReason = null;
        True(row.SoftwareActionEnabled);
        row.SoftwareUpdate = row.SoftwareUpdate! with { Phase = SoftwareUpdatePhase.Downloading };
        True(!row.LaunchEnabled && row.ShowLaunch);
        Equal("http://localhost:17770", row.LaunchPath);
    });
    Run("copied VatEFS plugin needs review instead of fresh setup", () =>
    {
        var dll = Path.Combine(temporary, "VatEFS.dll");
        File.WriteAllText(dll, "Synthetic plugin existence only.");
        try
        {
            var row = Software(SoftwareApp.VatEfs, SoftwareUpdatePhase.Unavailable);
            row.SoftwareExecutablePath = Path.Combine(temporary, "missing-efs.exe");
            row.LaunchPath = "http://localhost:17770";
            True(!row.SoftwareNeedsSetup && row.ShowSoftwareAction);
            Equal("Change location…", row.SoftwareSetupText);
            Equal("Open releases ↗", row.SoftwareActionText);
        }
        finally { File.Delete(dll); }
    });
    Run("supported policy and beta-compatible source are labelled separately", () =>
    {
        var euroscope = new CheckResult { AppName = "EuroScope", HasEuroScopeManagement = true, InstalledVersion = "v3.2.9.0", LatestVersion = "v3.2.3.2", Status = CheckStatus.Unsupported };
        Equal("Sweden supported", euroscope.LatestVersionCaption);
        Equal("Sweden supports 3.2.3.2", euroscope.RowExplanation);
        Equal("Unsupported version", euroscope.RowStatusText);
        var beta = Software(SoftwareApp.Vatis, SoftwareUpdatePhase.Current);
        beta.InstalledVersion = beta.LatestVersion = "v4.1.0-beta.19";
        Equal("Compatible release", beta.LatestVersionCaption);
        Equal("v4.1.0-beta.19", beta.DisplayInstalledVersion);
        Equal("v4.1.0-beta.19", beta.DisplayLatestVersion);
    });
    Run("GNG uses its reviewed installation workflow instead of a download-only action", () =>
    {
        var gng = new CheckResult { AppName = "EuroScope (GNG Pack)", HasFontsCheck = true, IsFolder = true, Status = CheckStatus.UpdateAvailable, InstalledVersion = "2605/01  rev.1", LatestVersion = "2610/01  rev.3" };
        Equal("Swedish GNG package", gng.DisplayName);
        Equal("Latest package", gng.LatestVersionCaption);
        Equal("2605/01  rev.1", gng.DisplayInstalledVersion);
        Equal("Review a GNG update", gng.RowExplanation);
        True(gng.DetailText.Contains("review the package before installing"));
        gng.DownloadUrl = "https://files.aero-nav.com/ESAA";
        True(!gng.ShowDownload && !gng.HasSoftwareUpdate);
        Equal("Folder", gng.LaunchActionText);
    });
    Run("unavailable is a review state without an inferred update", () =>
    {
        var row = Software(SoftwareApp.TrackAudio, SoftwareUpdatePhase.Unavailable);
        const string reason = "TrackAudio is version 1.4.0, but Windows lists version 1.3.3. Repair or reinstall TrackAudio using its official installer before updating it in Launchpad.";
        row.SoftwareUpdate = row.SoftwareUpdate! with { Message = reason };
        row.StatusMessage = reason;
        Equal("Needs review", row.RowStatusText);
        Equal("Warning", row.RowStatusKind);
        True(row.DetailText.Contains(reason));
        True(row.DetailText.StartsWith(reason));
        Equal(1, row.DetailText.Split(reason).Length - 1);
        Equal("TrackAudio · Needs review", row.DetailHeading);
        True(!row.SoftwareUpdate.CanUpdate);
        Equal("Open downloads ↗", row.SoftwareActionText);
    });
    Run("details retain complete errors and recovery location", () =>
    {
        var row = Software(SoftwareApp.Vatis, SoftwareUpdatePhase.Error);
        var recovery = Path.Combine(temporary, string.Join('-', Enumerable.Repeat("synthetic-recovery", 10)));
        row.SoftwareUpdate = row.SoftwareUpdate! with { Message = "The vendor operation failed after export.", BackupFolder = recovery };
        row.StatusMessage = "The saved path could not be updated. Review settings.";
        True(row.DetailText.Contains(row.StatusMessage));
        True(row.DetailText.Contains(row.SoftwareUpdate.Message));
        True(row.DetailText.Contains(recovery));
        True(row.DetailText.StartsWith(row.StatusMessage));
        True(!row.DetailText.Contains(row.Description));
        Equal("Needs attention", row.RowStatusText);
    });
    Run("busy phase labels do not hide progress or change cancellation", () =>
    {
        foreach (var phase in new[] { SoftwareUpdatePhase.Checking, SoftwareUpdatePhase.Downloading, SoftwareUpdatePhase.Verifying, SoftwareUpdatePhase.BackingUp, SoftwareUpdatePhase.Installing })
        {
            var row = Software(SoftwareApp.Vacs, phase);
            row.SoftwareUpdate = row.SoftwareUpdate! with { ProgressPercent = phase == SoftwareUpdatePhase.Downloading ? 43 : null };
            True(row.SoftwareUpdateBusy);
            True(row.RowStatusText.Length <= 23);
            Equal(phase != SoftwareUpdatePhase.Installing, row.SoftwareCanCancel);
            True(!row.ShowSoftwareSetup && !row.ShowSoftwareAction);
            if (phase == SoftwareUpdatePhase.Downloading) True(row.RowStatusText.Contains("43%"));
        }
    });
    Run("missing path does not expose an update action", () =>
    {
        var row = Software(SoftwareApp.TrackAudio, SoftwareUpdatePhase.Available);
        row.LaunchPath = string.Empty;
        Equal("Not set up", row.RowStatusText);
        True(row.ShowSoftwareSetup);
        True(!row.ShowSoftwareAction);
        True(row.DetailText.Contains("Find an existing copy"));
    });
    Run("new releases, ready restart and absent feed remain distinct", () =>
    {
        var row = new CheckResult { AppName = "Sweden FIR Launchpad", HasSelfUpdate = true };
        row.SelfUpdateSummary = "Ready to restart";
        row.Status = CheckStatus.UpdateAvailable;
        Equal("Ready to restart", row.RowStatusText);
        Equal("Update", row.RowStatusKind);
        True(row.RowExplanation.Contains("Downloaded"));
        row.SelfUpdateSummary = "Updates not published yet";
        row.SelfUpdateNotice = "Installer updates not published yet";
        row.Status = CheckStatus.Unknown;
        Equal("Updates not published", row.RowStatusText);
        Equal("Not published", row.DisplayLatestVersion);
        Equal("Neutral", row.RowStatusKind);
    });
    Run("portable notices do not claim a release is unpublished", () =>
    {
        var row = new CheckResult
        {
            AppName = "Sweden FIR Launchpad", HasSelfUpdate = true,
            SelfUpdateSummary = "Manual updates", SelfUpdateNotice = "Portable copy: use the installer."
        };
        Equal("Not checked", row.DisplayLatestVersion);
        Equal("Install to enable in-app updates", row.RowExplanation);
        row.SelfUpdateBusy = true;
        Equal("Progress is shown in this row", row.RowExplanation);
        row.SelfUpdateBusy = false;
        row.SelfUpdateSummary = "Updates not published yet";
        Equal("Not published", row.DisplayLatestVersion);
    });
    Run("font attention remains visible even when package is current", () =>
    {
        var row = new CheckResult
        {
            AppName = "EuroScope (GNG Pack)", HasFontsCheck = true, Status = CheckStatus.UpToDate,
            FontsState = VatscaUpdateChecker.Services.FontsState.NeedsAction,
            FontsTooltip = "EuroScope.ttf is older than the package copy."
        };
        Equal("Fonts need attention", row.RowStatusText);
        Equal("Warning", row.RowStatusKind);
        True(row.DetailText.StartsWith(row.FontsTooltip));
        Equal("Review the required GNG fonts", row.RowExplanation);
        row.Status = CheckStatus.UpdateAvailable;
        Equal("Update available", row.RowStatusText);
        True(row.DetailText.Contains("Choose Update, or open Setup → Swedish GNG"));
        row.FontsState = VatscaUpdateChecker.Services.FontsState.AllOk;
        Equal("Review a GNG update", row.RowExplanation);
    });
    Run("software explanations fit the short second line", () =>
    {
        foreach (var phase in Enum.GetValues<SoftwareUpdatePhase>())
        {
            var row = Software(SoftwareApp.TrackAudio, phase);
            True(row.RowExplanation.Length <= 35);
            True(!row.RowExplanation.Contains("Details"));
        }
    });
    Run("running applications replace launch with paired Show and Close controls", () =>
    {
        var row = Software(SoftwareApp.TrackAudio, SoftwareUpdatePhase.Current);
        Equal("Launch", row.LaunchActionText);
        row.IsRunning = true;
        Equal("Show", row.LaunchActionText);
        True(row.LaunchTooltip.Contains("Show") && row.ShowRunningActions && !row.ShowSimpleLaunch && !row.ShowSplitLaunch);
        row.ApplicationActionsAllowed = false;
        True(!row.LaunchEnabled && row.ShowRunningActions);
        row.ApplicationActionsAllowed = true;
        row.IsRunning = false;
        True(!row.ShowRunningActions && row.ShowSimpleLaunch && row.LaunchEnabled);
        var web = new CheckResult { AppName = "VATIRIS", IsWebApp = true };
        Equal("Open", web.LaunchActionText);
    });
    Run("restart-required permits running controls but blocks every new application start", () =>
    {
        var rows = Enum.GetValues<SoftwareApp>().Select(app => Software(app, SoftwareUpdatePhase.Completed)).ToList();
        rows.Add(new CheckResult { AppName = "EuroScope", HasEuroScopeManagement = true, LaunchPath = fakeExe });
        rows.Add(new CheckResult { AppName = "VATIRIS", IsWebApp = true, LaunchPath = "https://example.invalid/" });
        foreach (var row in rows)
        {
            row.ApplicationActionsAllowed = true;
            row.ApplicationStartsAllowed = false;
            row.SoftwareActionsAllowed = false;
            True(!row.LaunchEnabled && !row.ShowRunningActions);
            var notices = new List<string?>();
            row.PropertyChanged += (_, args) => notices.Add(args.PropertyName);
            row.IsRunning = true;
            True(row.LaunchEnabled && row.ShowRunningActions && !row.ShowSimpleLaunch && !row.ShowSplitLaunch);
            True(notices.Contains(nameof(CheckResult.LaunchEnabled)));
            notices.Clear();
            row.IsRunning = false;
            True(!row.LaunchEnabled && !row.ShowRunningActions);
            True(notices.Contains(nameof(CheckResult.LaunchEnabled)));
            row.ApplicationStartsAllowed = true;
            row.SoftwareActionsAllowed = true;
            True(row.LaunchEnabled);
        }
    });
    Run("active work blocks running controls even while restart is required", () =>
    {
        var row = Software(SoftwareApp.TrackAudio, SoftwareUpdatePhase.Completed);
        row.IsRunning = true;
        row.ApplicationStartsAllowed = false;
        row.SoftwareActionsAllowed = false;
        True(row.LaunchEnabled);
        row.ApplicationActionsAllowed = false;
        True(!row.LaunchEnabled && row.ShowRunningActions);
        row.ApplicationActionsAllowed = true;
        foreach (var phase in new[] { SoftwareUpdatePhase.Checking, SoftwareUpdatePhase.Downloading, SoftwareUpdatePhase.Installing })
        {
            row.SoftwareUpdate = row.SoftwareUpdate! with { Phase = phase };
            True(!row.LaunchEnabled && row.ShowRunningActions);
        }
        row.SoftwareUpdate = row.SoftwareUpdate! with { Phase = SoftwareUpdatePhase.Completed };
        True(row.LaunchEnabled);
    });
    Run("not-used applications retain paths and active operation state for Setup", () =>
    {
        var row = Software(SoftwareApp.TrackAudio, SoftwareUpdatePhase.Unavailable);
        var path = row.LaunchPath;
        row.IsNotUsed = true;
        Equal("Not used", row.RowStatusText);
        Equal("Neutral", row.RowStatusKind);
        Equal(path, row.LaunchPath);
        Equal("Use this application", row.UsageActionText);
        row.SoftwareUpdate = row.SoftwareUpdate! with { Phase = SoftwareUpdatePhase.Installing };
        Equal("Installing…", row.RowStatusText);
        row.IsNotUsed = false;
        Equal(path, row.LaunchPath);
    });
    Run("running VatEFS browser stays visible when its local service stops", () =>
    {
        var row = Software(SoftwareApp.VatEfs, SoftwareUpdatePhase.Current);
        row.LaunchPath = "http://localhost:17770";
        row.IsLocalUrlReachable = false;
        row.IsRunning = true;
        True(row.ShowRunningActions && !row.ShowSimpleLaunch);
        Equal("Show", row.LaunchActionText);
    });
    Run("selected launch profile does not claim to change a running EuroScope session", () =>
    {
        var row = new CheckResult { AppName = "EuroScope" };
        row.Profiles.Add(new("Sweden", "C:\\Synthetic\\Sweden.prf"));
        row.SelectedProfile = row.Profiles[0];
        Equal("Launch profile: Sweden", row.SelectedProfileCaption);
        row.IsRunning = true;
        Equal("Next launch: Sweden", row.SelectedProfileCaption);
        True(row.ShowRunningActions && !row.ShowSplitLaunch);
        row.SelectedProfile = new("Observer", null);
        Equal("Next launch: Observer", row.SelectedProfileCaption);
        Equal("Show", row.LaunchActionText);
    });

    Run("each software state selects one compact action without changing update eligibility", () =>
    {
        foreach (var app in Enum.GetValues<SoftwareApp>())
        foreach (var phase in Enum.GetValues<SoftwareUpdatePhase>())
        {
            var row = Software(app, phase);
            row.SoftwareExecutablePath = fakeExe;
            var (action, text) = phase switch
            {
                SoftwareUpdatePhase.Checking or SoftwareUpdatePhase.Downloading or SoftwareUpdatePhase.Verifying or SoftwareUpdatePhase.BackingUp
                    => (CheckRowAction.SoftwareCancel, "Cancel"),
                SoftwareUpdatePhase.Installing or SoftwareUpdatePhase.Current or SoftwareUpdatePhase.Completed
                    => (CheckRowAction.None, ""),
                SoftwareUpdatePhase.Available => (CheckRowAction.SoftwareUpdate, "Update"),
                SoftwareUpdatePhase.Unavailable => (CheckRowAction.Downloads, "Downloads"),
                SoftwareUpdatePhase.Error or SoftwareUpdatePhase.Cancelled => (CheckRowAction.SoftwareCheck, "Retry"),
                _ => (CheckRowAction.SoftwareCheck, "Check")
            };
            RowAction(row, action, text, action != CheckRowAction.None);
            Equal(phase == SoftwareUpdatePhase.Available, row.SoftwareUpdate!.CanUpdate);
        }
    });
    Run("missing software always routes to setup instead of updating or downloading", () =>
    {
        foreach (var app in Enum.GetValues<SoftwareApp>())
        foreach (var phase in new[] { SoftwareUpdatePhase.Available, SoftwareUpdatePhase.Unavailable, SoftwareUpdatePhase.Error, SoftwareUpdatePhase.Current })
        {
            var row = Software(app, phase);
            row.LaunchPath = app == SoftwareApp.VatEfs ? "http://localhost:17770" : Path.Combine(temporary, "missing.exe");
            row.SoftwareExecutablePath = Path.Combine(temporary, "missing-efs.exe");
            RowAction(row, CheckRowAction.Setup, "Set up", true);
        }
    });
    Run("cancel remains available during locked work and takes priority over missing or unused paths", () =>
    {
        var row = Software(SoftwareApp.TrackAudio, SoftwareUpdatePhase.Downloading);
        row.Status = CheckStatus.Checking;
        row.IsNotUsed = true;
        row.LaunchPath = "";
        row.SoftwareActionsAllowed = row.ApplicationActionsAllowed = false;
        RowAction(row, CheckRowAction.SoftwareCancel, "Cancel", true);
        row.SoftwareUpdate = row.SoftwareUpdate! with { Phase = SoftwareUpdatePhase.Installing };
        RowAction(row, CheckRowAction.None, "", false);
    });
    Run("compact update preserves running client and maintenance guards", () =>
    {
        var row = Software(SoftwareApp.TrackAudio, SoftwareUpdatePhase.Available);
        row.IsRunning = true;
        RowAction(row, CheckRowAction.SoftwareUpdate, "Update", false);
        True(row.RowActionTooltip.Contains("Close the app"));
        row.IsRunning = false;
        row.SoftwareActionsAllowed = false;
        RowAction(row, CheckRowAction.SoftwareUpdate, "Update", false);
        row.SoftwareActionsAllowed = true;
        RowAction(row, CheckRowAction.SoftwareUpdate, "Update", true);
        var plugin = Software(SoftwareApp.VatEfs, SoftwareUpdatePhase.Available);
        plugin.SoftwareExecutablePath = fakeExe;
        plugin.IsRunning = true;
        RowAction(plugin, CheckRowAction.SoftwareUpdate, "Update", true);
        plugin.SoftwareBlockingReason = "EuroScope is running.";
        RowAction(plugin, CheckRowAction.SoftwareUpdate, "Update", false);
    });
    Run("unused row state preserves setup routing without interrupting a running app", () =>
    {
        var row = Software(SoftwareApp.TrackAudio, SoftwareUpdatePhase.Current);
        row.IsNotUsed = true;
        RowAction(row, CheckRowAction.Setup, "Set up", true);
        row.ApplicationActionsAllowed = false;
        RowAction(row, CheckRowAction.Setup, "Set up", false);
        row.ApplicationActionsAllowed = true;
        row.IsRunning = true;
        RowAction(row, CheckRowAction.None, "", false);
        row.IsRunning = false;
        row.IsNotUsed = false;
        RowAction(row, CheckRowAction.None, "", false);
    });
    Run("self-update uses existing routing and permission with compact labels", () =>
    {
        var row = new CheckResult { AppName = "Sweden FIR Launchpad", HasSelfUpdate = true, ShowSelfUpdateAction = true };
        foreach (var (full, compact) in new[] { ("Check for updates", "Check"), ("Download update", "Download"),
            ("Restart to update", "Restart"), ("Open downloads ↗", "Downloads"), ("Retry update check", "Retry"), ("Cancel", "Cancel") })
        {
            row.SelfUpdateActionText = full;
            RowAction(row, CheckRowAction.SelfUpdate, compact, true);
            Equal(full, row.RowActionTooltip);
            row.SelfUpdateActionEnabled = false;
            RowAction(row, CheckRowAction.SelfUpdate, compact, false);
            row.SelfUpdateActionEnabled = true;
        }
        row.ShowSelfUpdateAction = false;
        RowAction(row, CheckRowAction.None, "", false);
        row.ShowSelfUpdateAction = true;
        row.SelfUpdateBusy = true;
        RowAction(row, CheckRowAction.None, "", false);
    });
    Run("GNG prioritizes setup then package update then required fonts", () =>
    {
        var row = new CheckResult { AppName = "EuroScope (GNG Pack)", HasFontsCheck = true, IsFolder = true,
            LaunchPath = temporary, FontsState = FontsState.NeedsAction, Status = CheckStatus.NotConfigured };
        RowAction(row, CheckRowAction.Setup, "Set up", true);
        row.Status = CheckStatus.UpdateAvailable;
        RowAction(row, CheckRowAction.GngUpdate, "Update", true);
        row.Status = CheckStatus.UpToDate;
        RowAction(row, CheckRowAction.Fonts, "Fonts", true);
        row.Status = CheckStatus.Checking;
        RowAction(row, CheckRowAction.None, "", false);
        row.Status = CheckStatus.UpToDate;
        row.FontsState = FontsState.Unknown;
        RowAction(row, CheckRowAction.None, "", false);
    });
    Run("SAAMAN offers local setup for invalid DLLs and update only when available", () =>
    {
        var row = new CheckResult { AppName = "SAAMAN", HasSaamanManagement = true, Status = CheckStatus.NotConfigured };
        RowAction(row, CheckRowAction.Setup, "Set up", true);
        row.Status = CheckStatus.Error;
        RowAction(row, CheckRowAction.Setup, "Set up", true);
        row.Status = CheckStatus.UpdateAvailable;
        RowAction(row, CheckRowAction.SaamanManage, "Update", true);
        row.SoftwareActionsAllowed = false;
        RowAction(row, CheckRowAction.SaamanManage, "Update", false);
        row.SoftwareActionsAllowed = true;
        foreach (var status in new[] { CheckStatus.Unknown, CheckStatus.Installed, CheckStatus.UpToDate, CheckStatus.Checking })
        {
            row.Status = status;
            RowAction(row, CheckRowAction.None, "", false);
        }
    });
    Run("EuroScope offers supported-installation management and web apps need no maintenance shortcut", () =>
    {
        var row = new CheckResult { AppName = "EuroScope", HasEuroScopeManagement = true, Status = CheckStatus.NotConfigured };
        RowAction(row, CheckRowAction.Setup, "Set up", true);
        row.LaunchPath = fakeExe;
        row.Status = CheckStatus.Unsupported;
        RowAction(row, CheckRowAction.EuroScopeManage, "Manage", true);
        row.Status = CheckStatus.UpToDate;
        RowAction(row, CheckRowAction.None, "", false);
        var web = new CheckResult { AppName = "VATIRIS", IsWebApp = true, Status = CheckStatus.WebApp };
        True(!web.CanOpenSetup);
        RowAction(web, CheckRowAction.None, "", false);
        web.IsNotUsed = true;
        RowAction(web, CheckRowAction.None, "", false);
    });
    Run("setup routes exist only for applications supported by the setup workspace", () =>
    {
        foreach (var app in Enum.GetValues<SoftwareApp>()) True(Software(app, SoftwareUpdatePhase.Idle).CanOpenSetup);
        True(new CheckResult { HasEuroScopeManagement = true }.CanOpenSetup);
        True(new CheckResult { HasFontsCheck = true }.CanOpenSetup);
        True(new CheckResult { HasSaamanManagement = true }.CanOpenSetup);
        True(!new CheckResult { HasSelfUpdate = true }.CanOpenSetup);
        True(!new CheckResult { IsWebApp = true, IsLocalUrl = true }.CanOpenSetup);
    });
    Run("missing-location help names the current setup workspace", () =>
    {
        var gng = new CheckResult { HasFontsCheck = true, Status = CheckStatus.NotConfigured };
        True(gng.ConfigureTooltip.Contains("Setup → Swedish GNG"));
        True(gng.RowActionTooltip.Contains("Setup → Swedish GNG"));
        var plugin = new CheckResult { IsLocalUrl = true };
        True(plugin.ConfigureTooltip.Contains("Setup → Applications"));
        True(plugin.ConfigureTooltip.Contains("Use in EuroScope"));
        var app = Software(SoftwareApp.TrackAudio, SoftwareUpdatePhase.Unavailable);
        app.LaunchPath = "";
        True(app.SoftwareDetail.Contains("Setup → Applications"));
        True(app.RowActionTooltip.Contains("Setup → Applications"));
        var saaman = new CheckResult { HasSaamanManagement = true, Status = CheckStatus.NotConfigured };
        True(saaman.RowActionTooltip.Contains("Setup → Applications"));
    });
    Run("all action inputs notify the compact bindings", () =>
    {
        var row = Software(SoftwareApp.TrackAudio, SoftwareUpdatePhase.Available);
        var notices = new HashSet<string?>();
        row.PropertyChanged += (_, args) => notices.Add(args.PropertyName);
        foreach (var change in new Action[] { () => row.Status = CheckStatus.Checking, () => row.IsNotUsed = true,
            () => row.IsRunning = true, () => row.ApplicationActionsAllowed = false, () => row.SoftwareActionsAllowed = false,
            () => row.SoftwareBlockingReason = "Synthetic guard", () => row.LaunchPath = "", () => row.SoftwareExecutablePath = fakeExe,
            () => row.SoftwareUpdate = row.SoftwareUpdate! with { Phase = SoftwareUpdatePhase.Error },
            () => row.FontsState = FontsState.NeedsAction, () => row.SelfUpdateActionText = "Download update",
            () => row.ShowSelfUpdateAction = true, () => row.SelfUpdateActionEnabled = false, () => row.SelfUpdateBusy = true })
        {
            notices.Clear();
            change();
            foreach (var property in new[] { nameof(CheckResult.RowAction), nameof(CheckResult.RowActionText),
                nameof(CheckResult.ShowRowAction), nameof(CheckResult.RowActionEnabled), nameof(CheckResult.RowActionTooltip) })
                True(notices.Contains(property));
        }
    });
    Run("status, progress and error changes refresh the selected detail bindings", () =>
    {
        var row = Software(SoftwareApp.TrackAudio, SoftwareUpdatePhase.Current);
        var notices = new List<string?>();
        row.PropertyChanged += (_, args) => notices.Add(args.PropertyName);
        row.SoftwareUpdate = row.SoftwareUpdate! with { Phase = SoftwareUpdatePhase.Downloading, ProgressPercent = 20 };
        foreach (var property in new[] { nameof(CheckResult.RowStatusText), nameof(CheckResult.RowStatusKind), nameof(CheckResult.RowExplanation), nameof(CheckResult.DetailText) }) True(notices.Contains(property));
        True(notices.Count < 60);
        notices.Clear();
        row.StatusMessage = "A new complete error.";
        True(notices.Contains(nameof(CheckResult.DetailText)));
        notices.Clear();
        row.IsSelected = true;
        Equal(1, notices.Count);
        Equal(nameof(CheckResult.IsSelected), notices[0]);
    });
    Console.WriteLine($"{passed} presentation checks passed.");
    return 0;
}
finally
{
    File.Delete(fakeExe);
    Directory.Delete(temporary);
}

CheckResult Software(SoftwareApp app, SoftwareUpdatePhase phase) => new()
{
    AppName = app switch { SoftwareApp.Vacs => "VACS", SoftwareApp.Vatis => "vATIS", SoftwareApp.VatEfs => "VatEFS", _ => "TrackAudio" },
    SoftwareApp = app, LaunchPath = fakeExe,
    IsWebApp = app == SoftwareApp.VatEfs, IsLocalUrl = app == SoftwareApp.VatEfs,
    SoftwareUpdate = new(app, phase, "Synthetic operation state.")
};

static CheckResult[] AppRows() =>
[
    new() { AppName = "EuroScope", HasEuroScopeManagement = true },
    new() { AppName = "EuroScope (GNG Pack)", IsFolder = true, HasFontsCheck = true },
    new() { AppName = "TrackAudio", SoftwareApp = SoftwareApp.TrackAudio },
    new() { AppName = "VACS", SoftwareApp = SoftwareApp.Vacs },
    new() { AppName = "vATIS", SoftwareApp = SoftwareApp.Vatis },
    new() { AppName = "VatEFS", SoftwareApp = SoftwareApp.VatEfs, IsWebApp = true, IsLocalUrl = true },
    new() { AppName = "SAAMAN", HasSaamanManagement = true },
    new() { AppName = "VATIRIS", IsWebApp = true },
    new() { AppName = "Sweden FIR Launchpad", HasSelfUpdate = true }
];

void Run(string name, Action action)
{
    action();
    passed++;
    Console.WriteLine("PASS " + name);
}

static void True(bool value)
{
    if (!value) throw new InvalidOperationException("Presentation assertion failed.");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

static void RowAction(CheckResult row, CheckRowAction action, string text, bool enabled)
{
    Equal(action, row.RowAction);
    Equal(text, row.RowActionText);
    Equal(action != CheckRowAction.None, row.ShowRowAction);
    Equal(enabled, row.RowActionEnabled);
    True(row.RowActionText.Length <= 10);
    if (row.ShowRowAction) True(!string.IsNullOrWhiteSpace(row.RowActionTooltip));
}
