using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

// Injectable store/HTTP/clock; tests never use the production AppData path or endpoint.
internal sealed class ReportingClient : IDisposable
{
    private readonly ReportingStore _store;
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly string _version;
    private readonly Func<DateTime> _now;
    private readonly TimeSpan _timeout;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource _generationCancellation = new();
    private readonly Queue<ReportingEvent> _context = new();
    private readonly Dictionary<(ReportingApplication?, ReportingOperation?, ReportingErrorCode?, ReportingEventName), long> _errorMinutes = new();
    private long _suppressedCount;
    private ReportingDocument? _observed;
    private Guid _session = Guid.NewGuid();
    private bool _storageAvailable, _preferenceWriteFailed, _disposed, _fatal;
    private bool _normalSession, _sessionEventQueued;
    private FileSystemWatcher? _watcher;
    private Task? _background;

    internal ReportingClient(string directory, string launchpadVersion, HttpClient http, Uri endpoint,
        Func<DateTime>? utcNow = null, TimeSpan? requestTimeout = null)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("https" or "http") ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new ArgumentException("A reporting API base address is required.", nameof(endpoint));
        _store = new(directory); _version = ReportingWire.NormalizeAppVersion(launchpadVersion);
        _http = http; _endpoint = endpoint; _now = utcNow ?? (() => DateTime.UtcNow);
        _timeout = requestTimeout ?? TimeSpan.FromSeconds(15);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _storageAvailable = _store.Initialize(out _observed);
    }

    internal ReportingState State
    {
        get
        {
            Observe();
            lock (_gate) return new(_observed?.SupportId ?? Guid.Empty,
                _observed?.Preferences ?? new(), !_disposed && _storageAvailable && !_preferenceWriteFailed,
                _observed?.Pending.Count ?? 0, _suppressedCount);
        }
    }

    internal ReportingHistoryState History
    {
        get
        {
            Observe();
            lock (_gate)
            {
                if (_disposed || !_storageAvailable || _preferenceWriteFailed || _observed is null)
                    return new(false, Guid.Empty, new(), Array.Empty<ReportingDelivery>(), 0, 0, 0);
                var pending = Live(_observed.Pending).ToArray();
                return new(true, _observed.SupportId, _observed.Preferences,
                    Array.AsReadOnly((_observed.LastSent ?? []).OrderBy(receipt => receipt.Stream).ToArray()),
                    pending.Count(entry => ReportingHistoryValidation.Stream(entry.EventName) == ReportingStream.Usage),
                    pending.Count(entry => ReportingHistoryValidation.Stream(entry.EventName) == ReportingStream.Inventory),
                    pending.Count(entry => ReportingHistoryValidation.Stream(entry.EventName) == ReportingStream.Errors));
            }
        }
    }

    internal bool TrySavePreferences(bool usage, bool errors) => TrySavePreferences(usage, State.Preferences.Inventory, errors);

    internal bool TrySavePreferences(bool usage, bool inventory, bool errors) => SavePreferences(usage, inventory, errors, null);

    internal bool TrySavePreferences(bool usage, bool inventory, bool errors, bool detailedPlugins) => SavePreferences(usage, inventory, errors, detailedPlugins);

    private bool SavePreferences(bool usage, bool inventory, bool errors, bool? detailedPlugins)
    {
        CancelRequests();
        bool saved = _store.Change(document =>
        {
            var preferences = new ReportingPreferences(true, usage, errors, inventory,
                inventory && (detailedPlugins ?? document.Preferences.DetailedPlugins));
            return document with
            {
                Generation = Guid.NewGuid(), Preferences = preferences,
                Pending = document.Pending.Where(entry => ReportingWire.Allowed(preferences, entry.EventName))
                    .Where(entry => inventory || !ReportingWire.HasVersions(entry))
                    // An event may already have reached the server. Drop the whole event rather than changing its payload under the same ID.
                    .Where(entry => preferences.DetailedPlugins || entry.DllInventory is null).ToList(),
                UsageNotBefore = usage ? document.UsageNotBefore : null,
                ErrorsNotBefore = errors ? document.ErrorsNotBefore : null,
                UsageAttempts = usage ? document.UsageAttempts : 0, ErrorAttempts = errors ? document.ErrorAttempts : 0,
                InventoryNotBefore = inventory ? document.InventoryNotBefore : null, InventoryAttempts = inventory ? document.InventoryAttempts : 0
            };
        }, out var current);
        lock (_gate) _preferenceWriteFailed = !saved;
        AcceptObservation(saved, current);
        if (saved) EnsureSessionStarted();
        return saved;
    }

    internal bool TryRegenerateId()
    {
        CancelRequests();
        bool saved = _store.Change(document => document with
        {
            SupportId = Guid.NewGuid(), Generation = Guid.NewGuid(), Pending = [], LastSent = [],
            UsageNotBefore = null, ErrorsNotBefore = null, UsageAttempts = 0, ErrorAttempts = 0, InventoryNotBefore = null, InventoryAttempts = 0
        }, out var current);
        if (!saved) { lock (_gate) _preferenceWriteFailed = true; }
        AcceptObservation(saved, current);
        if (saved) EnsureSessionStarted();
        return saved;
    }

    internal bool ClearPending()
    {
        CancelRequests();
        bool saved = _store.Change(document => document with
        {
            Generation = Guid.NewGuid(), Pending = [], UsageNotBefore = null, ErrorsNotBefore = null,
            UsageAttempts = 0, ErrorAttempts = 0, InventoryNotBefore = null, InventoryAttempts = 0
        }, out var current);
        lock (_gate) { _context.Clear(); _errorMinutes.Clear(); _suppressedCount = 0; _session = Guid.NewGuid(); }
        if (!saved) { lock (_gate) _preferenceWriteFailed = true; }
        AcceptObservation(saved, current);
        return saved;
    }

    internal bool RecordUsage(ReportingEventName name, ReportingDetails? details = null) =>
        !ReportingWire.IsError(name) && !ReportingWire.IsInventory(name) && Record(name, details ?? new());

    internal ReportingInventoryCapture? BeginInventoryCapture()
    {
        if (!Observe()) return null;
        lock (_gate) return !_disposed && !_fatal && !_preferenceWriteFailed && _observed is not null &&
            ReportingWire.Allowed(_observed.Preferences, ReportingEventName.InventorySnapshot)
            ? new(_observed.SupportId, _observed.Generation, _now(), _observed.Preferences.DetailedPlugins) : null;
    }

    internal bool RecordInventory(ReportingInventoryCapture capture, IReadOnlyList<ReportingInventoryEntry> items, ReportingDllInventory? dllInventory = null)
    {
        if (capture is null || items is not { Count: 10 } || (dllInventory is not null && !capture.IncludeDllInventory)) return false;
        var snapshot = items.ToArray();
        if (dllInventory is not null)
        {
            if (dllInventory.Entries is not { Count: <= ReportingWire.MaximumDllEntries }) return false;
            dllInventory = dllInventory with { Entries = Array.AsReadOnly(dllInventory.Entries.ToArray()) };
            if (!ReportingWire.ValidDllInventory(dllInventory)) return false;
        }
        if (!ReportingWire.ValidInventory(snapshot)) return false;
        return Record(ReportingEventName.InventorySnapshot, new(), capture, snapshot.OrderBy(item => item.Component).ToArray(), dllInventory);
    }

    internal bool RecordError(ReportingErrorCode code, ReportingDetails? details = null, bool fatal = false)
    {
        if (fatal) { lock (_gate) _fatal = true; CancelRequests(); }
        return Record(fatal ? ReportingEventName.UnhandledError : ReportingEventName.OperationFailed,
            (details ?? new()) with { ErrorCode = code, Outcome = ReportingOutcome.Failed });
    }

    private bool Record(ReportingEventName name, ReportingDetails details, ReportingInventoryCapture? capture = null,
        IReadOnlyList<ReportingInventoryEntry>? items = null, ReportingDllInventory? dllInventory = null)
    {
        if (!Enum.IsDefined(name) || !ReportingWire.ValidDetails(details) ||
            (!ReportingWire.IsError(name) && details.ErrorCode is not null)) return false;
        if (ReportingWire.IsError(name) && SuppressRepeatedError(name, details)) return false;
        if (!Observe()) return false;
        ReportingEvent entry;
        Guid generation;
        lock (_gate)
        {
            if (_disposed || _preferenceWriteFailed || _observed is null) return false;
            generation = _observed.Generation;
            if (capture is not null && (capture.Generation != generation || capture.SupportId != _observed.SupportId ||
                capture.StartedAt < _now().AddMinutes(-5) || capture.StartedAt > _now().AddSeconds(5))) return false;
            if (dllInventory is not null && (!_observed.Preferences.Inventory || !_observed.Preferences.DetailedPlugins)) return false;
            if (!_observed.Preferences.Inventory) details = details with { PackageVersion = null, PreviousVersion = null };
            entry = new(Guid.NewGuid(), _observed.SupportId, _session, capture?.StartedAt ?? _now(), _version, name,
                details.OperationId, details.Application, details.Operation, details.Outcome, details.Stage,
                details.ErrorCode, details.DurationBucket, details.PackageVersion, details.PreviousVersion,
                details.Source, details.RestartRequired, details.RecoveryAvailable, details.Feature, details.CompactLayout,
                details.TextScale, details.AlwaysOnTop, details.Theme, items, dllInventory);
            if (!ReportingWire.ValidEvent(entry, _now())) return false;
            // Session-only context is available for a separately reviewed manual report.
            if (!ReportingWire.IsInventory(name)) _context.Enqueue(entry);
            while (_context.Count > 20) _context.Dequeue();
            if (!ReportingWire.Allowed(_observed.Preferences, name)) return false;
        }
        bool queued = false;
        bool saved = _store.Change(document =>
        {
            if (document.Generation != generation || document.SupportId != entry.SupportId || !ReportingWire.Allowed(document.Preferences, name) ||
                (entry.DllInventory is not null && !document.Preferences.DetailedPlugins)) return document;
            var pending = Live(document.Pending).Append(entry).ToList();
            var next = ReportingStore.FitQueue(document with { Pending = pending });
            queued = next.Pending.Any(item => item.EventId == entry.EventId);
            return next;
        }, out var current);
        AcceptObservation(saved, current);
        return saved && queued;
    }

    private bool SuppressRepeatedError(ReportingEventName name, ReportingDetails details)
    {
        // Enum-only keys bound cardinality, and the check precedes any file access
        // so an error loop cannot repeatedly rewrite/deserialize the queue.
        var key = (details.Application, details.Operation, details.ErrorCode, name);
        long minute = _now().Ticks / TimeSpan.TicksPerMinute;
        lock (_gate)
        {
            if (_disposed) return true;
            if (_errorMinutes.TryGetValue(key, out var previous) && previous == minute)
            {
                if (_suppressedCount < long.MaxValue) _suppressedCount++;
                return true;
            }
            _errorMinutes[key] = minute;
            return false;
        }
    }

    internal PreparedReport PrepareReport(ReportingApplication application, ReportingCategory category, bool includeContext)
    {
        if (!Enum.IsDefined(application) || !Enum.IsDefined(category)) throw new ArgumentException("Choose a report application and category.");
        if (!Observe()) throw new InvalidOperationException("Reporting storage is unavailable. No report was prepared.");
        lock (_gate)
        {
            if (_disposed || _observed is null) throw new InvalidOperationException("Reporting is unavailable.");
            var context = includeContext ? Live(_context).Where(entry => entry.SupportId == _observed.SupportId).TakeLast(20).ToList() : null;
            var report = new ReportingReport(1, Guid.NewGuid(), _observed.SupportId, _version, application, category, context);
            while (ReportingWire.Bytes(report).Length > ReportingWire.MaximumReportBytes && context is { Count: > 0 }) context.RemoveAt(0);
            return new(report.SupportId, report.ReportId, Encoding.UTF8.GetString(ReportingWire.Bytes(report)));
        }
    }

    internal async Task<ReportSendResult> SendReportAsync(PreparedReport report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!Observe() || State.SupportId != report.SupportId)
            return new(false, null, "The support ID changed or reporting storage is unavailable. Prepare and review a new report.");
        if (!ReportingHistoryValidation.Valid(new ReportingDelivery(ReportingStream.ManualReport, _now(), report.Json, "LP_VALIDATION"), report.SupportId))
            return new(false, null, "This report is not valid. Prepare a new report.");
        ReportingReport? document;
        try { document = JsonSerializer.Deserialize<ReportingReport>(report.Json, ReportingWire.Json); }
        catch (JsonException) { return new(false, null, "This report is not valid. Prepare a new report."); }
        var bytes = Encoding.UTF8.GetBytes(report.Json);
        bool hasVersions = document?.Context?.Any(entry => entry?.PackageVersion is not null || entry?.PreviousVersion is not null) == true;
        bool inventoryAllowed = State.Preferences.Inventory;
        if (document is null || document.SchemaVersion != 1 || document.ReportId != report.ReportId ||
            document.SupportId != report.SupportId || !ReportingWire.IsUuid(document.ReportId) ||
            !ReportingWire.Version(document.LaunchpadVersion) || !Enum.IsDefined(document.Application) || !Enum.IsDefined(document.Category) ||
            bytes.Length > ReportingWire.MaximumReportBytes || document.Context is { Count: > 20 } ||
            // A previously received report keeps its idempotency key after its
            // context ages. The server distinguishes that retry from a new report.
            document.Context?.Any(entry => entry is null || entry.SupportId != report.SupportId || ReportingWire.IsInventory(entry.EventName) ||
                (!inventoryAllowed && (entry.PackageVersion is not null || entry.PreviousVersion is not null)) ||
                !ReportingWire.ValidEvent(entry, _now(), checkAge: false)) == true)
            return new(false, null, "This report is no longer valid. Prepare and review a new report.");
        if (cancellationToken.IsCancellationRequested)
            return new(false, null, "The report was not sent.");
        // A zero-time, uncancelled gate avoids throwing when the caller cancels
        // between checking its token and creating the linked HTTP cancellation.
        if (!await _sendGate.WaitAsync(0).ConfigureAwait(false))
            return new(false, null, "Another report is being sent. Try again shortly.");
        Guid generation = Guid.Empty;
        try
        {
            if (!Observe() || State.SupportId != report.SupportId)
                return new(false, null, "The support ID changed. Prepare and review a new report.");
            lock (_gate)
            {
                if (_disposed || _observed?.SupportId != report.SupportId)
                    return new(false, null, "Reporting changed before the report could be sent.");
                generation = _observed.Generation;
            }
            using var linked = CreateRequestCancellation(cancellationToken, generation: generation, supportId: report.SupportId, requireInventory: hasVersions);
            using var response = await PostAsync("reports", bytes, linked.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Created)
            {
                var body = await ReadSmallResponse(response, linked.Token).ConfigureAwait(false);
                using var json = JsonDocument.Parse(body);
                if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.EnumerateObject().Count() == 1 &&
                    json.RootElement.TryGetProperty("reference", out var reference) && reference.ValueKind == JsonValueKind.String &&
                    reference.GetString() is { } value && ReportingHistoryValidation.ValidReference(value))
                {
                    var receipt = new ReportingDelivery(ReportingStream.ManualReport, _now(), report.Json, value);
                    var saved = CompleteManual(generation, report.SupportId, receipt, linked.Token);
                    if (saved == ReceiptResult.Stale)
                        return new(false, null, "The report was received, but reporting choices or the support ID changed before its local receipt could be saved.");
                    return new(true, value, saved == ReceiptResult.Saved ? "Your report was received." :
                        "Your report was received, but its local receipt could not be saved.");
                }
                return new(false, null, "The server did not confirm receipt. Retry this same report to avoid duplicates.");
            }
            return new(false, null, response.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => "Reporting is temporarily busy. Keep this report and try again later.",
                HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.Conflict => "The server could not accept this report. Prepare a new report or save this one locally.",
                _ => "The server could not confirm receipt. Keep this report and retry later."
            });
        }
        catch (Exception error) when (IsTransportError(error))
        {
            lock (_gate)
                if (_disposed || (generation != Guid.Empty && (_observed?.Generation != generation || _observed?.SupportId != report.SupportId)))
                    return new(false, null, "Reporting choices or the support ID changed. This attempt did not save a local receipt.");
            return new(false, null, "Receipt could not be confirmed. The report may have arrived; retry this same report to avoid duplicates.");
        }
        finally { _sendGate.Release(); }
    }

    internal void StartBackground()
    {
        StartNormalSession();
        lock (_gate)
        {
            if (_disposed || _background is not null) return;
            try
            {
                if (Directory.Exists(_store.DirectoryPath))
                {
                    _watcher = new(_store.DirectoryPath, "reporting.json")
                    { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                    _watcher.Changed += (_, _) => Observe();
                    _watcher.Created += (_, _) => Observe();
                    _watcher.Deleted += (_, _) => Observe();
                    _watcher.Renamed += (_, _) => Observe();
                    _watcher.Error += (_, _) => CancelRequests();
                    _watcher.EnableRaisingEvents = true;
                }
            }
            catch (Exception error) when (ReportingStore.IsStorageError(error)) { }
            _background = Task.Run(async () =>
            {
                try
                {
                    while (!_shutdown.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), _shutdown.Token).ConfigureAwait(false);
                        await FlushOnceAsync(_shutdown.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
            });
        }
    }

    // Called by normal startup only. Permission may be confirmed later in the
    // first-run dialog; that records the session at confirmation, never earlier.
    internal void StartNormalSession()
    {
        lock (_gate) _normalSession = true;
        EnsureSessionStarted();
    }

    private void EnsureSessionStarted()
    {
        if (!Observe()) return;
        lock (_gate)
        {
            if (!_normalSession || _sessionEventQueued || _disposed || _fatal || _preferenceWriteFailed ||
                _observed is null || !ReportingWire.Allowed(_observed.Preferences, ReportingEventName.SessionStarted)) return;
            _sessionEventQueued = true;
        }
        if (!RecordUsage(ReportingEventName.SessionStarted))
            lock (_gate) _sessionEventQueued = false;
    }

    internal async Task FlushOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!await _sendGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            if (!Observe()) return;
            ReportingDocument document;
            lock (_gate)
            {
                if (_disposed || _fatal || _preferenceWriteFailed || _observed is null) return;
                document = _observed;
            }
            var now = _now();
            var live = Live(document.Pending).ToArray();
            if (live.Length != document.Pending.Count)
            {
                bool pruned = _store.Change(current => current with { Pending = Live(current.Pending).ToList() }, out var current);
                AcceptObservation(pruned, current);
                if (!pruned) return;
                document = current!;
                live = document.Pending.ToArray();
            }
            string? stream = new[] { "errors", "inventory", "usage" }.FirstOrDefault(candidate =>
                NotBefore(document, candidate).GetValueOrDefault() <= now &&
                live.Any(entry => ReportingWire.Stream(entry.EventName) == candidate && ReportingWire.Allowed(document.Preferences, entry.EventName)));
            if (stream is null) return;
            var batch = live.Where(entry => ReportingWire.Stream(entry.EventName) == stream).Take(25).ToArray();
            if (batch.Length == 0)
            {
                if (live.Length != document.Pending.Count)
                    _store.Change(current => current with { Pending = Live(current.Pending).ToList() }, out _);
                return;
            }
            var bytes = ReportingWire.Bytes(new ReportingBatch(1, batch));
            while (bytes.Length > ReportingWire.MaximumRequestBytes && batch.Length > 1)
            { batch = batch[..^1]; bytes = ReportingWire.Bytes(new ReportingBatch(1, batch)); }
            if (!Observe()) return;
            lock (_gate)
                if (_preferenceWriteFailed || _fatal || _observed?.Generation != document.Generation || !ReportingWire.Allowed(_observed.Preferences, batch[0].EventName)) return;
            using var linked = CreateRequestCancellation(cancellationToken, generation: document.Generation);
            try
            {
                using var response = await PostAsync(stream + "/batches", bytes, linked.Token).ConfigureAwait(false);
                bool acknowledged = false;
                if (response.StatusCode == HttpStatusCode.Accepted)
                {
                    using var json = JsonDocument.Parse(await ReadSmallResponse(response, linked.Token).ConfigureAwait(false));
                    acknowledged = json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.EnumerateObject().Count() == 2 &&
                        json.RootElement.TryGetProperty("accepted", out var accepted) && accepted.TryGetInt32(out int count) && count >= 0 &&
                        json.RootElement.TryGetProperty("duplicates", out var duplicates) && duplicates.TryGetInt32(out int duplicateCount) && duplicateCount >= 0 &&
                        (long)count + duplicateCount == batch.Length;
                }
                bool permanent = response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or
                    HttpStatusCode.Conflict or HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.UnprocessableEntity;
                if (acknowledged || permanent) CompleteBatch(document.Generation, document.SupportId, batch, stream,
                    acknowledged ? bytes : null, linked.Token);
                else ScheduleRetry(document.Generation, stream, response.Headers.RetryAfter,
                    response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable);
            }
            catch (Exception error) when (IsTransportError(error))
            { ScheduleRetry(document.Generation, stream, null); }
        }
        catch (OperationCanceledException) { /* Permission/identity changes cancel this attempt, not the background worker. */ }
        finally { _sendGate.Release(); }
    }

    private void CompleteBatch(Guid generation, Guid supportId, ReportingEvent[] batch, string stream, byte[]? acknowledgedBytes, CancellationToken token)
    {
        var ids = batch.Select(entry => entry.EventId).ToHashSet();
        var receipt = acknowledgedBytes is null ? null : new ReportingDelivery(ReportingHistoryValidation.Stream(batch[0].EventName),
            _now(), Encoding.UTF8.GetString(acknowledgedBytes));
        lock (_gate)
        {
            if (!CurrentAttempt(generation, supportId, token) || !_storageAvailable || _preferenceWriteFailed) return;
            bool saved = _store.Change(document => document.Generation != generation || document.SupportId != supportId || token.IsCancellationRequested ? document : document with
            {
                Pending = Live(document.Pending).Where(entry => !ids.Contains(entry.EventId)).ToList(),
                LastSent = receipt is null ? document.LastSent : WithReceipt(document, receipt),
                ErrorsNotBefore = stream == "errors" ? null : document.ErrorsNotBefore,
                UsageNotBefore = stream == "usage" ? null : document.UsageNotBefore,
                InventoryNotBefore = stream == "inventory" ? null : document.InventoryNotBefore,
                ErrorAttempts = stream == "errors" ? 0 : document.ErrorAttempts,
                UsageAttempts = stream == "usage" ? 0 : document.UsageAttempts,
                InventoryAttempts = stream == "inventory" ? 0 : document.InventoryAttempts
            }, out var current);
            AcceptObservation(saved, current);
        }
    }

    private enum ReceiptResult { Saved, Unavailable, Stale }

    private ReceiptResult CompleteManual(Guid generation, Guid supportId, ReportingDelivery receipt, CancellationToken token)
    {
        lock (_gate)
        {
            if (!CurrentAttempt(generation, supportId, token)) return ReceiptResult.Stale;
            if (!_storageAvailable || _preferenceWriteFailed) return ReceiptResult.Unavailable;
            bool applied = false;
            bool saved = _store.Change(document =>
            {
                if (document.Generation != generation || document.SupportId != supportId || token.IsCancellationRequested) return document;
                applied = true;
                return document with { LastSent = WithReceipt(document, receipt) };
            }, out var current);
            AcceptObservation(saved, current);
            return !saved ? ReceiptResult.Unavailable : applied ? ReceiptResult.Saved : ReceiptResult.Stale;
        }
    }

    // Caller holds _gate through the bounded local write, so a reset cannot race
    // between the final completion check and committing its local receipt.
    private bool CurrentAttempt(Guid generation, Guid supportId, CancellationToken token) =>
        !_disposed && !token.IsCancellationRequested && _observed?.Generation == generation && _observed.SupportId == supportId;

    private static List<ReportingDelivery> WithReceipt(ReportingDocument document, ReportingDelivery receipt) =>
        (document.LastSent ?? []).Where(previous => previous.Stream != receipt.Stream).Append(receipt).OrderBy(item => item.Stream).ToList();

    private void ScheduleRetry(Guid generation, string stream, RetryConditionHeaderValue? retryAfter, bool shared = false)
    {
        bool saved = _store.Change(document =>
        {
            if (document.Generation != generation) return document;
            int attempts = Math.Min(20, (stream == "errors" ? document.ErrorAttempts : stream == "inventory" ? document.InventoryAttempts : document.UsageAttempts) + 1);
            var delay = TimeSpan.FromSeconds(Math.Min(3600, 15 * Math.Pow(2, Math.Min(8, attempts - 1))) + Random.Shared.Next(0, 6));
            DateTime notBefore = _now().Add(delay);
            if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
                notBefore = Max(notBefore, delta.Ticks > DateTime.MaxValue.Ticks - _now().Ticks
                    ? DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc) : _now().Add(delta));
            if (retryAfter?.Date is { } date) notBefore = Max(notBefore, date.UtcDateTime);
            if (shared) return document with
            {
                ErrorsNotBefore = Max(document.ErrorsNotBefore.GetValueOrDefault(), notBefore),
                UsageNotBefore = Max(document.UsageNotBefore.GetValueOrDefault(), notBefore),
                InventoryNotBefore = Max(document.InventoryNotBefore.GetValueOrDefault(), notBefore),
                ErrorAttempts = stream == "errors" ? attempts : document.ErrorAttempts,
                UsageAttempts = stream == "usage" ? attempts : document.UsageAttempts,
                InventoryAttempts = stream == "inventory" ? attempts : document.InventoryAttempts, Pending = Live(document.Pending).ToList()
            };
            return stream switch
            {
                "errors" => document with { ErrorsNotBefore = notBefore, ErrorAttempts = attempts, Pending = Live(document.Pending).ToList() },
                "inventory" => document with { InventoryNotBefore = notBefore, InventoryAttempts = attempts, Pending = Live(document.Pending).ToList() },
                _ => document with { UsageNotBefore = notBefore, UsageAttempts = attempts, Pending = Live(document.Pending).ToList() }
            };
        }, out var current);
        AcceptObservation(saved, current);
    }

    private async Task<HttpResponseMessage> PostAsync(string route, byte[] bytes, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint, route));
        request.Content = new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadSmallResponse(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > 4096) throw new InvalidDataException("Reporting response was too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[1024];
        for (int count; (count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0;)
        {
            if (output.Length + count > 4096) throw new InvalidDataException("Reporting response was too large.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    private CancellationTokenSource CreateRequestCancellation(CancellationToken token, Guid? generation = null, Guid? supportId = null, bool requireInventory = false)
    {
        lock (_gate)
        {
            if (_disposed || !_storageAvailable || (generation is not null && (_preferenceWriteFailed || _fatal || _observed?.Generation != generation)) ||
                (supportId is not null && _observed?.SupportId != supportId) ||
                (requireInventory && (_preferenceWriteFailed || _observed?.Preferences.Inventory != true))) throw new OperationCanceledException();
            var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token, _generationCancellation.Token);
            linked.CancelAfter(_timeout);
            return linked;
        }
    }

    private bool Observe()
    {
        lock (_gate) if (_disposed) return false;
        bool read = _store.Read(out var document);
        AcceptObservation(read, document);
        return read;
    }

    private void AcceptObservation(bool available, ReportingDocument? document)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _storageAvailable = available;
            if (!available) { CancelRequestsLocked(); return; }
            if (_observed is not null && document!.Revision < _observed.Revision) return;
            if (_observed?.Generation != document!.Generation) CancelRequestsLocked();
            if (_observed?.SupportId != document.SupportId)
            { _context.Clear(); _errorMinutes.Clear(); _suppressedCount = 0; _session = Guid.NewGuid(); _sessionEventQueued = false; }
            if (!document.Preferences.Inventory && _context.Count > 0)
            {
                var sanitized = _context.Select(ReportingWire.WithoutVersions).ToArray();
                _context.Clear(); foreach (var entry in sanitized) _context.Enqueue(entry);
            }
            _observed = document;
        }
    }

    private void CancelRequests() { lock (_gate) CancelRequestsLocked(); }
    private void CancelRequestsLocked()
    {
        var old = _generationCancellation;
        _generationCancellation = new();
        old.Cancel(); old.Dispose();
    }

    private IEnumerable<ReportingEvent> Live(IEnumerable<ReportingEvent> entries) =>
        entries.Where(entry => entry.OccurredAt >= _now().AddDays(-7) && entry.OccurredAt <= _now().AddMinutes(5));
    private static DateTime? NotBefore(ReportingDocument document, string stream) => stream switch
    { "errors" => document.ErrorsNotBefore, "inventory" => document.InventoryNotBefore, _ => document.UsageNotBefore };
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    private static bool IsTransportError(Exception error) => error is HttpRequestException or IOException or InvalidDataException or OperationCanceledException or JsonException or InvalidOperationException;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Cancel(); CancelRequestsLocked();
            _watcher?.Dispose(); _watcher = null;
            _context.Clear();
        }
        _store.Close();
        // Never wait for network or callbacks while Launchpad is closing/applying an update.
    }
}
