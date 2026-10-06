using VatscaUpdateChecker.Models;

// Model-only checks. Synthetic paths/files only; no application or installer is started.
var temporary = Path.Combine(Path.GetTempPath(), "launchpad-presentation-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
var fakeExe = Path.Combine(temporary, "synthetic-app.exe");
File.WriteAllText(fakeExe, "Synthetic existence fixture; never executed.");
var passed = 0;
try
{
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
    Run("web service and detected plugin do not claim version checks", () =>
    {
        var web = new CheckResult { AppName = "VATIRIS", IsWebApp = true, InstalledVersion = "N/A", LatestVersion = "N/A", Status = CheckStatus.WebApp };
        Equal("Not applicable", web.DisplayInstalledVersion);
        Equal("Not applicable", web.DisplayLatestVersion);
        var plugin = new CheckResult { AppName = "VatEFS", IsWebApp = true, IsLocalUrl = true, InstalledVersion = "Installed", Status = CheckStatus.Installed };
        Equal("Not reported", plugin.DisplayInstalledVersion);
        Equal("No update source", plugin.LatestVersionCaption);
        Equal("Plugin detected", plugin.RowStatusText);
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
    Run("GNG remains a manual package download", () =>
    {
        var gng = new CheckResult { AppName = "EuroScope (GNG Pack)", HasFontsCheck = true, IsFolder = true, Status = CheckStatus.UpdateAvailable, InstalledVersion = "2605/01  rev.1", LatestVersion = "2610/01  rev.3" };
        Equal("Swedish GNG package", gng.DisplayName);
        Equal("Latest package", gng.LatestVersionCaption);
        Equal("2605/01  rev.1", gng.DisplayInstalledVersion);
        Equal("Download from AeroNav", gng.RowExplanation);
        True(gng.DetailText.Contains("manually"));
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
        True(row.DetailText.Contains("install it manually"));
        row.FontsState = VatscaUpdateChecker.Services.FontsState.AllOk;
        Equal("Download from AeroNav", row.RowExplanation);
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
    Run("force-stop label warns about unsaved work", () =>
    {
        var row = Software(SoftwareApp.TrackAudio, SoftwareUpdatePhase.Current);
        Equal("Launch", row.LaunchActionText);
        row.IsRunning = true;
        Equal("Stop", row.LaunchActionText);
        True(row.LaunchTooltip.Contains("Force-stop") && row.LaunchTooltip.Contains("Unsaved work"));
        var web = new CheckResult { AppName = "VATIRIS", IsWebApp = true };
        Equal("Open", web.LaunchActionText);
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
    AppName = app switch { SoftwareApp.Vacs => "VACS", SoftwareApp.Vatis => "vATIS", _ => "TrackAudio" },
    SoftwareApp = app, LaunchPath = fakeExe,
    SoftwareUpdate = new(app, phase, "Synthetic operation state.")
};

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
