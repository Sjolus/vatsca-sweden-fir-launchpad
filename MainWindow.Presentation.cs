using System.Windows;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    private bool _placementReady;

    private void InitializePresentation()
    {
        ApplyPresentationPreferences();
        foreach (var row in _results)
            row.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(CheckResult.RowStatusKind) or nameof(CheckResult.IsNotUsed)) RefreshApplicationsSummary(); };
        LocationChanged += (_, _) => { if (_placementReady) WindowPlacementService.Capture(this, _settings); };
        SizeChanged += (_, _) => { if (_placementReady) WindowPlacementService.Capture(this, _settings); };
        StateChanged += (_, _) => { if (_placementReady) WindowPlacementService.Capture(this, _settings); };
        Closing += (_, e) =>
        {
            if (e.Cancel) return;
            WindowPlacementService.Capture(this, _settings);
            SettingsService.TrySave(_settings);
        };
    }

    private void RestorePresentationPlacement()
    {
        try { WindowPlacementService.Restore(this, _settings); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException) { }
        _placementReady = true;
    }
    private void ApplyPresentationPreferences()
    {
        App.SetTextScale(_settings.TextScale);
        Topmost = _settings.AlwaysOnTop;
        ApplyDensity();
        if (_results != null) ApplyApplicationUsage();
    }

    private void ApplyApplicationUsage()
    {
        foreach (var row in _results) row.IsNotUsed = ApplicationPreferences.IsNotUsed(_settings, row.AppName);
        PresentApplicationRows(_results);
        RefreshApplicationsSummary();
    }
    private void Usage_Click(object sender, RoutedEventArgs e)
    {
        if (!TryAcquireMaintenanceGuard(out var lease)) return;
        using var guard = lease;
        if (sender is not FrameworkElement { DataContext: CheckResult row } || row.HasSelfUpdate) return;
        ApplicationPreferences.SetNotUsed(_settings, row.AppName, !row.IsNotUsed);
        ApplyApplicationUsage();
        SaveSessionSettings(row.IsNotUsed ? row.DisplayName + " marked as not used. Locations and data kept." : row.DisplayName + " is included again.");
    }
    private void RefreshApplicationsSummary()
    {
        if (_results == null) return;
        int updates = _results.Count(x => !x.IsNotUsed && x.RowStatusKind == "Update");
        int attention = _results.Count(x => !x.IsNotUsed && x.RowStatusKind == "Warning");
        var counts = new List<string>();
        if (updates > 0) counts.Add($"{updates} update{(updates == 1 ? "" : "s")}");
        if (attention > 0) counts.Add($"{attention} to review");
        ApplicationsSummary.Text = "Applications" + (counts.Count == 0 ? "" : " · " + string.Join(" · ", counts));
    }
}
