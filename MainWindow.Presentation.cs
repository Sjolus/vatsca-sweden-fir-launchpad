using System.Windows;
using System.Windows.Controls.Primitives;
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
        SizeChanged += (_, _) => { if (_placementReady) { WindowPlacementService.Capture(this, _settings); ApplyDetailsHeight(); } };
        StateChanged += (_, _) => { if (_placementReady) WindowPlacementService.Capture(this, _settings); };
        Closing += (_, e) =>
        {
            if (e.Cancel) return;
            WindowPlacementService.Capture(this, _settings);
            _settings.DetailPanelHeight = ApplicationDetails.Height;
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
        PinButton.Content = Topmost ? "Unpin" : "Pin";
        PinButton.ToolTip = Topmost ? "Let other windows cover Launchpad" : "Keep Launchpad above other windows";
        ApplyDetailsHeight();
        if (_results != null) ApplyApplicationUsage();
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        _settings.AlwaysOnTop = !_settings.AlwaysOnTop;
        ApplyPresentationPreferences();
        SaveSessionSettings(Topmost ? "Launchpad will stay on top." : "Launchpad is no longer pinned.");
    }

    private void ApplyDetailsHeight()
    {
        double wanted = double.IsFinite(_settings.DetailPanelHeight) ? _settings.DetailPanelHeight : 100;
        ApplicationDetails.Height = Math.Clamp(wanted, 100, Math.Max(100, Math.Min(360, ActualHeight > 0 ? ActualHeight * .48 : 220)));
        DetailsSizeButton.Content = ApplicationDetails.Height > 150 ? "Less space" : "More space";
    }
    private void DetailsSize_Click(object sender, RoutedEventArgs e)
    {
        _settings.DetailPanelHeight = ApplicationDetails.Height > 150 ? 100 : 240;
        ApplyDetailsHeight();
        SettingsService.TrySave(_settings);
    }
    private void DetailsResize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        _settings.DetailPanelHeight = ApplicationDetails.Height - e.VerticalChange;
        ApplyDetailsHeight();
    }

    private void ApplyApplicationUsage()
    {
        foreach (var row in _results) row.IsNotUsed = ApplicationPreferences.IsNotUsed(_settings, row.AppName);
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
