namespace VatscaUpdateChecker.Services;

/// <summary>Tracks one browser's current navigation without treating late events as a new document.</summary>
internal sealed class GngBrowserNavigationState
{
    private readonly HashSet<ulong> _startedIds = [];
    private ulong? _startedId;
    private long _requestGeneration;
    private bool _expectingStart;

    public ulong? ReadyNavigationId { get; private set; }
    public long Revision { get; private set; }

    // Navigate/Reload/Back can return before NavigationStarting is delivered.
    // Invalidate the old document before making those calls.
    public void ExpectNavigation()
    {
        _expectingStart = true;
        ReadyNavigationId = null;
        Revision++;
    }

    public bool Start(ulong navigationId, long requestGeneration, bool isRedirected = false)
    {
        if (isRedirected)
        {
            if (_expectingStart || _startedId != navigationId) return false;
        }
        else if (_startedId != navigationId)
        {
            if (!_startedIds.Add(navigationId)) return false;
            _startedId = navigationId;
            _requestGeneration = requestGeneration;
        }
        else if (_expectingStart) return false;

        // Redirects belong to their initial request even if a reference request
        // has since been armed. Their old failures cannot stop that new request.
        _expectingStart = false;
        ReadyNavigationId = null;
        Revision++;
        return true;
    }

    public bool TryMarkReady(ulong navigationId)
    {
        if (_expectingStart || _startedId != navigationId) return false;
        ReadyNavigationId = navigationId;
        return true;
    }

    public bool CanInspect(ulong navigationId) => !_expectingStart &&
        _startedId == navigationId && ReadyNavigationId == navigationId;

    public bool IsCurrent(ulong navigationId, long revision) =>
        Revision == revision && CanInspect(navigationId);

    public bool CanReportFailure(ulong navigationId, long requestGeneration) =>
        !_expectingStart && _startedId == navigationId && _requestGeneration == requestGeneration;
}
