using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

// Receipts are a bounded local copy of acknowledged wire payloads, not a log.
internal static class ReportingHistoryValidation
{
    internal static bool ValidStoredReceipts(JsonElement document, IReadOnlyList<ReportingDelivery>? receipts)
    {
        if (!document.TryGetProperty("last_sent", out var stored)) return receipts is null;
        if (receipts is null || document.EnumerateObject().Count(field => field.Name == "last_sent") != 1) return false;
        using var expected = JsonDocument.Parse(ReportingWire.Bytes(receipts));
        return SameShape(stored, expected.RootElement);
    }

    internal static bool Valid(IReadOnlyList<ReportingDelivery>? receipts, Guid supportId) => receipts is null ||
        (receipts.Count <= 4 && receipts.All(receipt => Valid(receipt, supportId)) &&
         receipts.Select(receipt => receipt.Stream).Distinct().Count() == receipts.Count);

    internal static bool Valid(ReportingDelivery? receipt, Guid supportId)
    {
        if (receipt is null || !Enum.IsDefined(receipt.Stream) || !ReportingWire.IsUuid(supportId) ||
            receipt.ConfirmedAt.Kind != DateTimeKind.Utc || receipt.ConfirmedAt < DateTime.UnixEpoch || receipt.Json is null)
            return false;
        bool manual = receipt.Stream == ReportingStream.ManualReport;
        int limit = manual ? ReportingWire.MaximumReportBytes : ReportingWire.MaximumRequestBytes;
        if (receipt.Json.Length > limit || Encoding.UTF8.GetByteCount(receipt.Json) > limit ||
            (manual ? !ValidReference(receipt.Reference) : receipt.Reference is not null)) return false;
        try
        {
            using var original = JsonDocument.Parse(receipt.Json, new JsonDocumentOptions { MaxDepth = 16 });
            byte[] canonical;
            if (manual)
            {
                var report = JsonSerializer.Deserialize<ReportingReport>(receipt.Json, ReportingWire.Json);
                if (report is null || report.SchemaVersion != 1 || report.SupportId != supportId ||
                    !ReportingWire.IsUuid(report.ReportId) || !ReportingWire.Version(report.LaunchpadVersion) ||
                    !Enum.IsDefined(report.Application) || !Enum.IsDefined(report.Category) || report.Context is { Count: > 20 } ||
                    report.Context?.Any(entry => !ValidEvent(entry, supportId, receipt.ConfirmedAt) || ReportingWire.IsInventory(entry.EventName)) == true)
                    return false;
                canonical = ReportingWire.Bytes(report);
            }
            else
            {
                var batch = JsonSerializer.Deserialize<ReportingBatch>(receipt.Json, ReportingWire.Json);
                if (batch is null || batch.SchemaVersion != 1 || batch.Events is not { Count: > 0 and <= 25 } ||
                    batch.Events.Any(entry => !ValidEvent(entry, supportId, receipt.ConfirmedAt) || Stream(entry.EventName) != receipt.Stream) ||
                    batch.Events.Select(entry => entry.EventId).Distinct().Count() != batch.Events.Count) return false;
                canonical = ReportingWire.Bytes(batch);
            }
            using var expected = JsonDocument.Parse(canonical);
            // Deserialization alone accepts duplicate keys and absent enum fields.
            // Compare structure to the typed wire shape, retaining original bytes.
            return SameShape(original.RootElement, expected.RootElement);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        { return false; }
    }

    internal static bool ValidReference(string? value) => value is not null && value.Length is >= 8 and <= 100 &&
        Regex.IsMatch(value, @"\A[A-Za-z0-9_-]{8,100}\z", RegexOptions.CultureInvariant);

    internal static ReportingStream Stream(ReportingEventName name) => ReportingWire.IsInventory(name)
        ? ReportingStream.Inventory : ReportingWire.IsError(name) ? ReportingStream.Errors : ReportingStream.Usage;

    private static bool ValidEvent(ReportingEvent? entry, Guid supportId, DateTime confirmedAt) => entry is not null &&
        entry.SupportId == supportId && ReportingWire.ValidEvent(entry, confirmedAt, checkAge: false) &&
        entry.OccurredAt.Ticks - confirmedAt.Ticks <= TimeSpan.FromMinutes(5).Ticks;

    private static bool SameShape(JsonElement actual, JsonElement expected)
    {
        if (actual.ValueKind != expected.ValueKind) return false;
        switch (actual.ValueKind)
        {
            case JsonValueKind.Object:
                var fields = actual.EnumerateObject().ToArray();
                return fields.Length == expected.EnumerateObject().Count() &&
                    fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() == fields.Length &&
                    fields.All(field => expected.TryGetProperty(field.Name, out var value) && SameShape(field.Value, value));
            case JsonValueKind.Array:
                return actual.GetArrayLength() == expected.GetArrayLength() &&
                    actual.EnumerateArray().Zip(expected.EnumerateArray()).All(pair => SameShape(pair.First, pair.Second));
            case JsonValueKind.String: return actual.GetString() == expected.GetString();
            default: return actual.GetRawText() == expected.GetRawText();
        }
    }
}
