using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Windows;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    private const string ApplicationBrowserPath = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";

    private ApplicationActionTarget ApplicationTarget(CheckResult row) => new(row.AppName, row.DisplayName, row.LaunchPath,
        row.IsWebApp, row.IsLocalUrl, row.HasEuroScopeManagement, row.IsNotUsed, row.IsFolder, row.HasSelfUpdate,
        row.HasEuroScopeManagement ? _settings.EuroscopeDataPath : null, row.SelectedProfile?.FilePath, row.SelectedProfile?.DisplayName);

    private void RefreshBulkActionAvailability()
    {
        if (_softwareUpdates is null || _launchpadUpdates is null) return;
        bool canControl = CanControlRunningApplication();
        bool canStart = CanRunSoftwareAction();
        foreach (var row in _results)
        {
            row.ApplicationActionsAllowed = canControl;
            row.ApplicationStartsAllowed = canStart;
        }
        StartAllButton.IsEnabled = canStart && _results.Any(row => !row.IsNotUsed && !row.IsFolder && !row.HasSelfUpdate && row.ShowLaunch && !row.IsRunning);
        CloseAllButton.IsEnabled = canControl && _results.Any(row => !row.IsFolder && !row.HasSelfUpdate && row.IsRunning);
    }

    private void StartAll_Click(object sender, RoutedEventArgs e) => ReviewApplicationBatch(ApplicationBatchAction.Start);
    private void CloseAll_Click(object sender, RoutedEventArgs e) => ReviewApplicationBatch(ApplicationBatchAction.Close);

    private void ReviewApplicationBatch(ApplicationBatchAction action)
    {
        bool allowed = action == ApplicationBatchAction.Start ? CanRunSoftwareAction() : CanControlRunningApplication();
        if (!allowed || !TryAcquireMaintenanceGuard(out var lease)) return;
        using var guard = lease;
        try
        {
            var choices = ApplicationBatchActions.Prepare(action, _results.Select(ApplicationTarget),
                target => ObserveApplication(target, validateStart: action == ApplicationBatchAction.Start));
            var review = new BulkApplicationActionWindow(action, choices) { Owner = this };
            if (ShowOwnedDialog(review) != true) return;
            var results = ApplicationBatchActions.Execute(action, review.Choices,
                target => ObserveApplication(target, validateStart: true), StartApplicationCore,
                process => ApplicationProcessControl.TryAct(process, RunningApplicationAction.CloseNormally));
            foreach (var result in results)
                if (_results.FirstOrDefault(row => row.AppName == result.Id) is { } row) row.StatusMessage = result.Message;
            int requested = results.Count(result => result.Requested);
            int skipped = results.Count - requested;
            LastCheckedText.Text = $"{(action == ApplicationBatchAction.Start ? "Start" : "Close")} requested for {requested} application{(requested == 1 ? "" : "s")}." +
                (skipped > 0 ? $" {skipped} skipped or unavailable; see application details." :
                    action == ApplicationBatchAction.Close ? " Check for save or confirmation prompts." : "");
            if (results.FirstOrDefault(result => !result.Requested) is { } first &&
                _results.FirstOrDefault(row => row.AppName == first.Id) is { } selected) SelectApplicationDetails(selected);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, "The application review could not finish:\n\n" + error.Message,
                "Applications", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { if (!_windowClosed) UpdateRunningStates(); }
    }

    private static ApplicationActionObservation ObserveApplication(ApplicationActionTarget target, bool validateStart)
    {
        var running = target.IsBrowser
            ? TrackedBrowser(target.Id) is { } browser ? new[] { browser } : []
            : ApplicationProcessControl.Find(target.LaunchPath);
        var windows = running.Where(ApplicationProcessControl.HasWindow).ToArray();
        string? blocked = validateStart && running.Count == 0 ? StartBlockReason(target) : null;
        return new(running, windows, blocked);
    }

    private static string? StartBlockReason(ApplicationActionTarget target)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(target.LaunchPath)) return "Skipped · choose this application's location in App settings first.";
            if (target.IsBrowser)
            {
                if (!Uri.TryCreate(target.LaunchPath, UriKind.Absolute, out var uri) ||
                    !(uri.Scheme == Uri.UriSchemeHttps || target.IsLocalBrowser && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
                    return "Skipped · this application's browser address is unavailable.";
                if (!File.Exists(ApplicationBrowserPath)) return "Skipped · Microsoft Edge could not be found.";
                if (target.IsLocalBrowser && !LocalBrowserReady(uri))
                    return "Skipped · VatEFS is not ready. Open it after EuroScope and its plugin are running.";
            }
            else
            {
                if (!Path.IsPathFullyQualified(target.LaunchPath) || !File.Exists(target.LaunchPath))
                    return "Skipped · the configured application could not be found.";
                if (target.IsEuroScope)
                {
                    var start = EuroScopeLaunchService.CreateStartInfo(target.LaunchPath, target.DataFolder, target.ProfilePath);
                    GngUpdateService.RequireNoPendingUpdate(start.WorkingDirectory);
                }
            }
            return null;
        }
        catch (Exception error) { return "Skipped · " + error.Message; }
    }

    private static bool LocalBrowserReady(Uri uri)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint => endpoint.Port == uri.Port &&
                (IPAddress.IsLoopback(endpoint.Address) || endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any)));
        }
        catch { return false; }
    }

    // Guard-free core shared by one confirmed bulk operation and a single-row launch.
    // The outer handler owns the maintenance lease; nested acquisition is not reentrant.
    private static bool StartApplicationCore(ApplicationActionTarget target)
    {
        if (StartBlockReason(target) is { } blocked) throw new InvalidOperationException(blocked);
        ProcessStartInfo start;
        if (target.IsBrowser)
        {
            var profile = Path.Combine(AppDataDir, target.Id + "Profile");
            start = new(ApplicationBrowserPath) { UseShellExecute = false };
            start.ArgumentList.Add("--app=" + target.LaunchPath);
            start.ArgumentList.Add("--user-data-dir=" + profile);
        }
        else if (target.IsEuroScope)
        {
            start = EuroScopeLaunchService.CreateStartInfo(target.LaunchPath, target.DataFolder, target.ProfilePath);
            GngUpdateService.RequireNoPendingUpdate(start.WorkingDirectory);
        }
        else
        {
            // Explorer preserves the existing Electron launch route outside inherited job restrictions.
            start = new("explorer.exe") { Arguments = $"\"{target.LaunchPath}\"", UseShellExecute = false };
        }
        using var process = Process.Start(start);
        if (process is null) return false;
        if (target.IsBrowser) RememberBrowser(target.Id, process);
        ReportingService.RecordUsage(ReportingEventName.ApplicationOpened,
            new ReportingDetails { Application = ReportingApplicationFor(target.Id), Operation = ReportingOperation.Launch });
        Logger.Log("LAUNCH", target.Id + ": start requested");
        return true;
    }
}
