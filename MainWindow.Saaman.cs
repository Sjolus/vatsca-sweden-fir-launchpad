using System.Diagnostics;
using System.IO;
using System.Windows;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    private readonly SaamanReleaseService _saamanReleases = new();
    private SaamanRelease? _lastSaamanRelease;
    private bool _saamanReleaseChecked;
    private string? _saamanReleaseFailure;
    private const string SaamanGuideUrl = "https://github.com/Kaskobiii/SAAMAN/blob/main/docs/controller-guide.md";

    private async Task CheckSaamanAsync()
    {
        await RefreshSaamanLocalAsync();
        if (_windowClosed) return;
        var row = SaamanRow;
        row.Status = CheckStatus.Checking;
        try
        {
            _lastSaamanRelease = await _saamanReleases.GetLatestAsync(_windowLifetime.Token);
            if (_windowClosed) return;
            _saamanReleaseChecked = true;
            _saamanReleaseFailure = null;
        }
        catch (OperationCanceledException) when (_windowClosed) { }
        catch (Exception ex)
        {
            if (_windowClosed) return;
            ReportingEvents.RecordFailure(ex, ReportingApplication.Saaman, ReportingOperation.Check, ReportingStage.Check);
            _saamanReleaseFailure = ex is SaamanReleaseUnavailableException ? ex.Message :
                "The latest SAAMAN release could not be checked. Your local DLL and profile choices are still available.";
        }
        await RefreshSaamanLocalAsync();
    }

    private async Task RefreshSaamanLocalAsync()
    {
        if (_windowClosed) return;
        var row = SaamanRow;
        string path = _settings.SaamanDllPath;
        row.LatestVersion = _lastSaamanRelease is null ? "—" : "v" + _lastSaamanRelease.Version;
        row.LatestIsPrerelease = false;
        row.DownloadUrl = _lastSaamanRelease?.ReleaseUrl ?? SaamanReleaseService.ReleasesUrl;
        try
        {
            SaamanDllInfo? installed = string.IsNullOrWhiteSpace(path) || !File.Exists(path)
                ? null : await Task.Run(() => SaamanInstallService.InspectDll(path), _windowLifetime.Token);
            if (_windowClosed) return;
            row.ObservedLocalVersion = installed?.Version;
            row.InstalledVersion = installed is null ? "Not installed" : "v" + installed.Version;
            row.Status = installed is null ? CheckStatus.NotConfigured :
                _lastSaamanRelease is null || _saamanReleaseFailure is not null ? CheckStatus.Installed :
                SoftwareVersion.TryParse(installed.Version, out var current) &&
                SoftwareVersion.TryParse(_lastSaamanRelease.Version, out var latest) && current.CompareTo(latest) < 0
                    ? CheckStatus.UpdateAvailable : CheckStatus.UpToDate;
            row.StatusMessage = installed is null
                ? "Install SAAMAN or choose your existing DLL."
                : "Installed SAAMAN " + installed.Version + ". Runs inside EuroScope.";
            row.StatusMessage += _settings.PatchSaaman
                ? "\nProfile option: SAAMAN enabled."
                : "\nProfile option: SAAMAN not enabled.";
            if (_saamanReleaseFailure is not null)
                row.StatusMessage += "\n\n" + _saamanReleaseFailure +
                    (_lastSaamanRelease is null ? "" : "\nThe release shown is from the last successful check.");
            else if (_saamanReleaseChecked && _lastSaamanRelease is null)
                row.StatusMessage += "\n\nNo published stable release is available.";
        }
        catch (OperationCanceledException) when (_windowClosed) { }
        catch (Exception)
        {
            if (_windowClosed) return;
            row.ObservedLocalVersion = null;
            row.InstalledVersion = "—";
            row.Status = CheckStatus.Error;
            row.StatusMessage = "The selected SAAMAN DLL could not be verified. Open Setup → Applications and choose your current SAAMAN DLL.";
        }
    }

    private async void SaamanManage_Click(object sender, RoutedEventArgs e)
    {
        SetupWorkspace.ShowSection("Applications");
        ShowWorkspace("Setup");
        await UpdateSaamanAsync();
    }

    private void SaamanGuide_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(SaamanGuideUrl) { UseShellExecute = true }); }
        catch { SaamanRow.StatusMessage = "The controller guide could not open. It is available in the SAAMAN GitHub repository."; }
    }
}
