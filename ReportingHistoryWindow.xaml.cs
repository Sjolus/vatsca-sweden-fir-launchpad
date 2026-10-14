using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class ReportingHistoryWindow : Window
{
    private sealed record StreamOption(ReportingStream Value, string Label) { public override string ToString() => Label; }
    private readonly Func<ReportingHistoryState> _readHistory;
    private readonly Action<string> _copyText;
    private ReportingHistoryState? _snapshot;
    private ReportingDelivery? _selectedDelivery;
    private bool _ready;

    public ReportingHistoryWindow() : this(() => ReportingService.History) { }

    // The reader returns local state only. Tests supply synthetic snapshots, never a live reporting client.
    internal ReportingHistoryWindow(Func<ReportingHistoryState> readHistory, Action<string>? copyText = null)
    {
        ArgumentNullException.ThrowIfNull(readHistory);
        _readHistory = readHistory;
        _copyText = copyText ?? Clipboard.SetText;
        InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, Height);
        MinWidth = Math.Min(MinWidth, Width);
        StreamChoice.ItemsSource = new[] { ReportingStream.Usage, ReportingStream.Inventory, ReportingStream.Errors, ReportingStream.ManualReport }
            .Select(stream => new StreamOption(stream, ReportingHistoryPresentation.StreamName(stream))).ToArray();
        StreamChoice.SelectedIndex = 0;
        _ready = true;
        RefreshSnapshot();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshSnapshot();

    private void RefreshSnapshot()
    {
        // Do not update a payload while it is being read or copied. Only this explicit refresh replaces the snapshot.
        _snapshot = null;
        try
        {
            var state = _readHistory();
            if (state.StorageAvailable) _snapshot = state with { LastSent = state.LastSent.ToArray() };
        }
        catch { /* Optional local history must not interfere with the rest of Launchpad. */ }
        ActionStatus.Text = "Showing a local snapshot. Refresh does not contact the reporting service.";
        ShowSelectedStream();
    }

    private void Stream_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        ActionStatus.Text = "Showing a local snapshot. Refresh does not contact the reporting service.";
        ShowSelectedStream();
    }

    private void ShowSelectedStream()
    {
        _selectedDelivery = null;
        DeliveryPanel.Visibility = Visibility.Collapsed;
        ReadableReportText.Clear();
        JsonText.Clear();
        JsonDetails.IsExpanded = false;
        CopyJsonButton.IsEnabled = false;
        EmptyStatus.Text = string.Empty;
        EmptyStatus.Visibility = Visibility.Collapsed;
        PendingStatus.Text = ConsentStatus.Text = string.Empty;
        ContentScroll.ScrollToTop();

        if (_snapshot is null)
        {
            DeliveryStatus.Text = "Saved report copies are unavailable.";
            EmptyStatus.Text = "Reporting storage could not be read safely. No older snapshot is shown. You can try Refresh; other Launchpad features remain available.";
            EmptyStatus.Visibility = Visibility.Visible;
            return;
        }
        if (StreamChoice.SelectedItem is not StreamOption option) return;

        var stream = option.Value;
        int pending = stream switch
        {
            ReportingStream.Usage => _snapshot.UsagePending,
            ReportingStream.Inventory => _snapshot.InventoryPending,
            ReportingStream.Errors => _snapshot.ErrorPending,
            _ => 0
        };
        PendingStatus.Text = stream == ReportingStream.ManualReport
            ? "Manual reports are sent only when you review and choose Send report. They are separate from automatic reporting choices."
            : $"Awaiting confirmation: {pending} automatic record{(pending == 1 ? "" : "s")}. The saved copy below contains only confirmed sends.";
        if (stream != ReportingStream.ManualReport)
        {
            bool enabled = stream switch
            {
                ReportingStream.Usage => _snapshot.Preferences.Usage,
                ReportingStream.Inventory => _snapshot.Preferences.Inventory,
                _ => _snapshot.Preferences.Errors
            };
            ConsentStatus.Text = !_snapshot.Preferences.Confirmed ? "Automatic reporting is off until you save your choices."
                : enabled ? "Sharing for this type is on." : "Sharing for this type is off. A saved copy may remain from when it was on.";
        }
        var delivery = _snapshot.LastSent.FirstOrDefault(item => item.Stream == stream);
        if (delivery is null)
        {
            DeliveryStatus.Text = "No sent copy recorded yet.";
            EmptyStatus.Text = "This view starts recording copies with this version.";
            EmptyStatus.Visibility = Visibility.Visible;
            return;
        }
        try
        {
            var readable = ReportingHistoryPresentation.Describe(delivery);
            var formattedJson = ReportingHistoryPresentation.FormatJson(delivery);
            int count = RecordCount(delivery);
            string unit = stream == ReportingStream.ManualReport ? "report" : stream == ReportingStream.Inventory ? "snapshot" : "event";
            DeliveryStatus.Text = $"Last confirmed: {delivery.ConfirmedAt.ToLocalTime():g} (local time) · {count} {unit}{(count == 1 ? "" : "s")}";
            ReadableReportText.Text = readable;
            JsonText.Text = formattedJson;
            _selectedDelivery = delivery;
            CopyJsonButton.IsEnabled = true;
            DeliveryPanel.Visibility = Visibility.Visible;
        }
        catch
        {
            DeliveryStatus.Text = "This saved copy could not be displayed safely.";
            EmptyStatus.Text = "Try Refresh to read the saved copy again. No data has been sent or changed.";
            EmptyStatus.Visibility = Visibility.Visible;
        }
    }

    private static int RecordCount(ReportingDelivery delivery)
    {
        using var document = JsonDocument.Parse(delivery.Json);
        if (delivery.Stream == ReportingStream.ManualReport) return 1;
        return document.RootElement.GetProperty("events").GetArrayLength();
    }

    private void CopyJson_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDelivery is null) return;
        try { _copyText(_selectedDelivery.Json); ActionStatus.Text = "The original JSON request was copied."; }
        catch { ActionStatus.Text = "The clipboard is busy. You can select and copy the formatted JSON above."; }
    }
}
