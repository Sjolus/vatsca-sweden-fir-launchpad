using System.Windows;
using System.Windows.Controls;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

internal enum RunningFixtureState { Stopped, Mixed, AllRunning, Busy, BrowserWithoutBackend, RestartRequired }

internal static partial class UiFixture
{
    internal static BulkApplicationActionWindow CreateBulkReview(ApplicationBatchAction action, bool minimum)
    {
        bool starting = action == ApplicationBatchAction.Start;
        var rows = new[]
        {
            new ApplicationActionChoice(new("euroscope", "EuroScope", "synthetic", IsEuroScope: true),
                starting ? "Start with profile ESAA TOPSKY — Stockholm approach. Existing settings are kept." : "Ask 2 verified EuroScope windows to close normally. Check each window for save or disconnect prompts.", true),
            new ApplicationActionChoice(new("trackaudio", "TrackAudio", "synthetic"),
                starting ? "Start the configured application." : "Ask the verified application window to close normally.", true),
            new ApplicationActionChoice(new("vacs", "VACS", "synthetic", IsNotUsed: true),
                starting ? "Not used. Excluded from starting applications." : "No verified running application window.", false),
            new ApplicationActionChoice(new("vatis", "vATIS", "synthetic"),
                starting ? "Already running. This copy stays open." : "No verified running application window.", false),
            new ApplicationActionChoice(new("vatefs", "VatEFS", "http://localhost:17770", IsBrowser: true, IsLocalBrowser: true),
                starting ? "The local VatEFS backend is unavailable. Start it before opening the browser." : "Close the tracked browser window only. The local VatEFS backend stays running.", !starting),
            new ApplicationActionChoice(new("vatiris", "VATIRIS", "https://vatiris.se", IsBrowser: true),
                starting ? "Open the configured website in its dedicated browser window." : "Ask the verified browser window to close. Other Edge windows stay open.", true)
        };
        var window = new BulkApplicationActionWindow(action, rows);
        window.Title += " — synthetic";
        if (minimum) { window.Width = window.MinWidth; window.Height = window.MinHeight; }
        return window;
    }

    internal static void SetRunningScenario(MainWindow window, RunningFixtureState state)
    {
        window.SetSyntheticRestart(state == RunningFixtureState.RestartRequired);
        var rows = Find<ItemsControl>(window, "AppList")!.Items.Cast<CheckResult>().ToArray();
        foreach (var row in rows)
        {
            row.IsRunning = false;
            row.IsNotUsed = false;
            row.ApplicationActionsAllowed = state != RunningFixtureState.Busy;
            row.ApplicationStartsAllowed = state != RunningFixtureState.RestartRequired;
            row.SoftwareActionsAllowed = state != RunningFixtureState.RestartRequired;
            if (row.SoftwareApp is { } software)
                row.SoftwareUpdate = new(software, SoftwareUpdatePhase.Current, "Synthetic installed copy; no application is running.");
            if (row.IsLocalUrl) row.IsLocalUrlReachable = state != RunningFixtureState.BrowserWithoutBackend;
        }
        foreach (var row in rows.Where(row => !row.IsFolder && !row.HasSelfUpdate))
            row.IsRunning = state switch
            {
                RunningFixtureState.AllRunning or RunningFixtureState.Busy => true,
                RunningFixtureState.Mixed or RunningFixtureState.RestartRequired => row.HasEuroScopeManagement || row.SoftwareApp == SoftwareApp.TrackAudio || row.AppName == "VATIRIS",
                RunningFixtureState.BrowserWithoutBackend => row.IsLocalUrl,
                _ => false
            };
        if (state == RunningFixtureState.Mixed)
        {
            var optional = rows.Single(row => row.SoftwareApp == SoftwareApp.Vatis);
            optional.IsRunning = optional.IsNotUsed = true;
        }
        RefreshSyntheticBulkAvailability(window);
    }

    internal static void RefreshSyntheticBulkAvailability(MainWindow window)
    {
        var rows = Find<ItemsControl>(window, "AppList")!.Items.Cast<CheckResult>().ToArray();
        Find<Button>(window, "StartAllButton")!.IsEnabled = rows.Any(row => !row.IsFolder && !row.HasSelfUpdate &&
            !row.IsRunning && !row.IsNotUsed && row.ShowLaunch && row.LaunchEnabled);
        Find<Button>(window, "CloseAllButton")!.IsEnabled = rows.Any(row => row.ShowRunningActions && row.LaunchEnabled);
    }
}
