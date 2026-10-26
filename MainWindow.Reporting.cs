using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Diagnostics;
using System.IO;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    private ReportingDetails? _lastPresentationReport;
    private Guid _lastPresentationSupportId;
    private string? _lastInventorySignature;
    private long _lastInventoryCaptureTime;
    private readonly ReportingViewTracker _reportedViews = new();

    private void ReportView(ReportingView view)
    {
        var state = ReportingService.State;
        if (IsLoaded && state.Preferences.Confirmed && state.Preferences.Usage &&
            _reportedViews.Transition(view, state.SupportId)) ReportingEvents.View(view);
    }

    private void ReportWorkspaceView() => ReportView(_workspace switch
    {
        "Setup" => SetupWorkspace.SelectedSection switch
        {
            "Gng" => ReportingView.SetupGng, "Profile" => ReportingView.ControllerProfile,
            "Preferences" => ReportingView.Preferences, "Activity" => ReportingView.ActivityRecovery,
            _ => ReportingView.SetupApplications
        },
        _ => ReportingView.Apps
    });

    // Reuse the normal version check; reporting never starts another network check.
    private async Task ReportCheckedInventoryAsync()
    {
        var capture = ReportingService.BeginInventoryCapture();
        if (capture is null || _windowClosed) return;
        try
        {
            var components = new[] { ReportingInventoryComponent.Euroscope, ReportingInventoryComponent.Gng,
                ReportingInventoryComponent.Trackaudio, ReportingInventoryComponent.Vacs, ReportingInventoryComponent.Vatis,
                ReportingInventoryComponent.Vatefs };
            var observations = components.Select((component, index) =>
            {
                var row = _results[index];
                var raw = index < 2 ? row.ObservedLocalVersion : row.SoftwareUpdate?.InstalledVersion;
                // A missing configuration or an unsuccessful inspection cannot establish absence.
                if (index >= 2 && string.IsNullOrWhiteSpace(raw)) raw = null;
                var version = ReportingEvents.PublicVersion(raw);
                var status = version is not null ? ReportingInventoryStatus.Known :
                    raw is not null ? ReportingInventoryStatus.Unknown : ReportingInventoryStatus.Unavailable;
                return new ReportingAppObservation(component, status, version);
            }).ToArray();
            var dataFolder = _settings.EuroscopeDataPath;
            var executableFolder = string.IsNullOrWhiteSpace(_settings.EuroscopeExePath) ? null : Path.GetDirectoryName(_settings.EuroscopeExePath);
            var vatEfsFolder = _settings.VatEfsPath;
            var saamanDll = _settings.SaamanDllPath;
            // The comparison key stays in memory. Paths are never passed to the reporting transport.
            var signature = capture.SupportId + "|" + capture.Generation + "|" + dataFolder + "|" + executableFolder + "|" + vatEfsFolder + "|" + saamanDll + "|" +
                string.Join("|", observations.Select(item => $"{item.Component}:{item.Status}:{item.Version}"));
            if (signature == _lastInventorySignature && Stopwatch.GetElapsedTime(_lastInventoryCaptureTime) < TimeSpan.FromMinutes(10)) return;
            var inventory = await Task.Run(() =>
            {
                var items = ReportingInventoryCollector.Collect(observations, dataFolder, executableFolder, vatEfsFolder);
                var dlls = capture.IncludeDllInventory
                    ? ReportingInventoryCollector.CollectDllInventory(dataFolder, executableFolder, vatEfsFolder, saamanDll) : null;
                return (Items: items, Dlls: dlls);
            });
            if (_windowClosed) return;
            if (ReportingService.RecordInventory(capture, inventory.Items, inventory.Dlls))
            { _lastInventorySignature = signature; _lastInventoryCaptureTime = Stopwatch.GetTimestamp(); }
        }
        catch (Exception)
        {
            // Optional reporting must not affect checks or surface local paths through diagnostics.
        }
    }

    private void ReportPresentationPreferences()
    {
        var state = ReportingService.State;
        if (!state.Preferences.Confirmed || !state.Preferences.Usage) return;
        var details = new ReportingDetails
        {
            Application = ReportingApplication.Launchpad,
            CompactLayout = _settings.CompactLayout,
            TextScale = _settings.TextScale >= 1.5 ? ReportingTextScale.Largest :
                _settings.TextScale >= 1.25 ? ReportingTextScale.Larger : ReportingTextScale.Standard,
            AlwaysOnTop = _settings.AlwaysOnTop,
            Theme = _settings.IsDarkMode ? ReportingTheme.Dark : ReportingTheme.Light
        };
        if (_lastPresentationSupportId == state.SupportId && details == _lastPresentationReport) return;
        if (ReportingService.RecordUsage(ReportingEventName.PreferencesSnapshot, details))
        { _lastPresentationReport = details; _lastPresentationSupportId = state.SupportId; }
    }

    private void ReportSoftwareFailure(SoftwareApp application, SoftwareUpdatePhase phase, Exception exception) => ReportingEvents.RecordFailure(exception,
        ReportingEvents.ForSoftware(application), phase == SoftwareUpdatePhase.Checking ? ReportingOperation.Check : ReportingOperation.Update,
        phase switch { SoftwareUpdatePhase.Checking => ReportingStage.Check, SoftwareUpdatePhase.Downloading => ReportingStage.Download,
            SoftwareUpdatePhase.Verifying => ReportingStage.Verify, SoftwareUpdatePhase.Installing => ReportingStage.Apply, _ => ReportingStage.Prepare });

    private void ReportSelfUpdateFailure(LaunchpadUpdateStatus phase, Exception exception) => ReportingEvents.RecordFailure(exception, ReportingApplication.Launchpad,
        phase == LaunchpadUpdateStatus.Checking ? ReportingOperation.Check : phase == LaunchpadUpdateStatus.Downloading ? ReportingOperation.Download : ReportingOperation.Update,
        phase == LaunchpadUpdateStatus.Checking ? ReportingStage.Check : phase == LaunchpadUpdateStatus.Downloading ? ReportingStage.Download : ReportingStage.Apply);

    private void ReportPublicCheckFailure(PublicUpdateCheck check, Exception exception) => ReportingEvents.RecordFailure(exception,
        check == PublicUpdateCheck.Gng ? ReportingApplication.Gng : ReportingApplication.Launchpad, ReportingOperation.Check, ReportingStage.Check);

    private bool CanOpenReporting() => !_windowClosed && !_isChecking && !_softwareActionBusy && !_softwareUpdates.IsBusy &&
        !_selfUpdateActionBusy && _openDialogCount == 0 && IsEnabled;

    private void OfferReportingChoices()
    {
        if (!ReportingService.State.Preferences.Confirmed && ReportingService.State.StorageAvailable)
            ShowOwnedDialog(new ReportingPreferencesWindow(firstRun: true) { Owner = this });
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        if (!CanOpenReporting() || sender is not Button button) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        var report = new MenuItem { Header = "Report a problem…" };
        report.Click += (_, _) => OpenProblemReport(ReportingApplication.Launchpad);
        var privacy = new MenuItem { Header = "Privacy and support ID…" };
        privacy.Click += (_, _) =>
        {
            if (CanOpenReporting()) ShowOwnedDialog(new ReportingPreferencesWindow { Owner = this });
        };
        var history = new MenuItem { Header = "Last sent reports…" };
        history.Click += (_, _) =>
        {
            if (CanOpenReporting()) ShowOwnedDialog(new ReportingHistoryWindow { Owner = this });
        };
        menu.Items.Add(report); menu.Items.Add(privacy); menu.Items.Add(history); menu.IsOpen = true;
    }

    private void ReportProblem_Click(object sender, RoutedEventArgs e)
    {
        var name = (sender as FrameworkElement)?.DataContext is CheckResult result ? result.AppName : "";
        OpenProblemReport(ReportingApplicationFor(name));
    }

    private void OpenProblemReport(ReportingApplication application, ReportingCategory category = ReportingCategory.Other)
    {
        if (CanOpenReporting()) ShowOwnedDialog(new ReportProblemWindow(application, category) { Owner = this });
    }

    internal static ReportingApplication ReportingApplicationFor(string? name) => name switch
    {
        "EuroScope" => ReportingApplication.Euroscope,
        "EuroScope (GNG Pack)" or "Swedish GNG" => ReportingApplication.Gng,
        "TrackAudio" => ReportingApplication.Trackaudio,
        "VACS" => ReportingApplication.Vacs,
        "vATIS" => ReportingApplication.Vatis,
        "VatEFS" => ReportingApplication.Vatefs,
        "SAAMAN" => ReportingApplication.Saaman,
        "VATIRIS" => ReportingApplication.Vatiris,
        "Selected applications and data" => ReportingApplication.Maintenance,
        _ => ReportingApplication.Launchpad
    };
}
