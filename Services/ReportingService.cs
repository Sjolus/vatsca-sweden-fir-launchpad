using System.IO;
using System.Net;
using System.Net.Http;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

public static class ReportingService
{
    public const string Endpoint = "https://launchpad.space.sjolus.org/api/v1/";
    private static readonly object Gate = new();
    private static ReportingClient? _client;
    private static HttpClient? _http;
    public static ReportingState State => _client?.State ?? new(Guid.Empty, new(), false, 0);

    // Called only after Velopack/helper dispatch, on a normal application launch.
    public static void Initialize(string? launchpadVersion = null)
    {
        lock (Gate)
        {
            if (_client is not null) return;
            try
            {
                // Candidate informational versions can contain local labels.
                // Only the numeric assembly version is a public reporting field.
                launchpadVersion ??= typeof(ReportingService).Assembly.GetName().Version?.ToString() ?? "0.0.0";
                _http = new(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, MaxResponseHeadersLength = 16 })
                { Timeout = Timeout.InfiniteTimeSpan };
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VatscaUpdateChecker", "Reporting");
                _client = new(directory, launchpadVersion, _http, new Uri(Endpoint));
                _client.StartBackground();
            }
            catch (Exception error) when (ReportingStore.IsStorageError(error)) { _http?.Dispose(); _http = null; }
        }
    }

    public static bool TrySavePreferences(bool usage, bool errors) => _client?.TrySavePreferences(usage, errors) == true;
    public static bool TrySavePreferences(bool usage, bool inventory, bool errors) => _client?.TrySavePreferences(usage, inventory, errors) == true;
    public static ReportingInventoryCapture? BeginInventoryCapture() => _client?.BeginInventoryCapture();
    public static bool RecordInventory(ReportingInventoryCapture capture, IReadOnlyList<ReportingInventoryEntry> items) => _client?.RecordInventory(capture, items) == true;
    public static bool TryRegenerateId() => _client?.TryRegenerateId() == true;
    public static bool ClearPending() => _client?.ClearPending() == true;
    public static bool RecordUsage(ReportingEventName name, ReportingDetails? details = null) => _client?.RecordUsage(name, details) == true;
    public static bool RecordError(ReportingErrorCode code, ReportingDetails? details = null, bool fatal = false) => _client?.RecordError(code, details, fatal) == true;
    public static PreparedReport PrepareReport(ReportingApplication application, ReportingCategory category, bool includeContext) =>
        _client?.PrepareReport(application, category, includeContext) ?? throw new InvalidOperationException("Reporting storage is unavailable.");
    public static Task<ReportSendResult> SendReportAsync(PreparedReport report, CancellationToken cancellationToken = default) =>
        _client?.SendReportAsync(report, cancellationToken) ?? Task.FromResult(new ReportSendResult(false, null, "Reporting is unavailable."));

    public static void Shutdown()
    {
        lock (Gate) { _client?.Dispose(); _client = null; _http?.Dispose(); _http = null; }
    }
}
