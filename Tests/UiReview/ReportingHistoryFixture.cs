using System.Text;
using System.Windows;
using System.Windows.Controls;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker
{
    internal static class ReportingHistoryFixture
    {
        internal static ReportingHistoryWindow Create(ReportingStream stream = ReportingStream.Usage, bool empty = false, bool unavailable = false)
        {
            var id = Guid.Parse("90b0d09c-3ef9-4d13-8838-ddbe38e25c24");
            var now = new DateTime(2026, 10, 14, 12, 30, 0, DateTimeKind.Utc);
            ReportingEvent Entry(ReportingEventName name) => new(Guid.NewGuid(), id, Guid.NewGuid(), now, "2.3.1", name);
            var entries = stream switch
            {
                ReportingStream.Inventory => new[] { Entry(ReportingEventName.InventorySnapshot) with { Items = Enum.GetValues<ReportingInventoryComponent>()
                    .Select(component => new ReportingInventoryEntry(component, component >= ReportingInventoryComponent.Topsky ? ReportingInventoryStatus.Unknown : ReportingInventoryStatus.Known,
                        ReportingWire.InventorySource(component), component >= ReportingInventoryComponent.Topsky ? null : "1.2.3")).ToArray() } },
                ReportingStream.Errors => new[] { Entry(ReportingEventName.OperationFailed) with { Application = ReportingApplication.Gng,
                    Operation = ReportingOperation.Update, Stage = ReportingStage.Download, ErrorCode = ReportingErrorCode.DownloadTimeout } },
                _ => new[] { Entry(ReportingEventName.ApplicationOpened) with { Application = ReportingApplication.Trackaudio },
                    Entry(ReportingEventName.PreferencesSnapshot) with { CompactLayout = true, AlwaysOnTop = false, TextScale = ReportingTextScale.Larger, Theme = ReportingTheme.Dark } }
            };
            string json = stream == ReportingStream.ManualReport
                ? Encoding.UTF8.GetString(ReportingWire.Bytes(new ReportingReport(1, Guid.NewGuid(), id, "2.3.1", ReportingApplication.Gng, ReportingCategory.Update, entries)))
                : Encoding.UTF8.GetString(ReportingWire.Bytes(new ReportingBatch(1, entries)));
            var receipt = new ReportingDelivery(stream, now, json, stream == ReportingStream.ManualReport ? "LP_SYNTHETIC_0001" : null);
            var state = new ReportingHistoryState(!unavailable, id, new(true, true, true, true), empty ? [] : [receipt], 3, 1, 0);
            var window = new ReportingHistoryWindow(() => state, _ => { });
            UiFixture.Initialize(window);
            window.Tag = state;
            ((ComboBox)window.FindName("StreamChoice")).SelectedIndex = (int)stream;
            return window;
        }
    }
}

namespace VatscaUpdateChecker.Services
{
    // The production reporting facade/transport/store are deliberately excluded from this project.
    internal static class ReportingService
    {
        internal static ReportingHistoryState History => new(false, Guid.Empty, new(), [], 0, 0, 0);
    }
}
