using VatscaUpdateChecker.Services;

internal static class NavigationStateTests
{
    public static int Run()
    {
        var count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            count++;
        }

        var state = new GngBrowserNavigationState();
        Check(state.ReadyNavigationId is null && !state.TryMarkReady(10) && !state.CanInspect(10), "An event without an accepted navigation cannot authorize inspection.");
        Check(!state.Start(10, 1, isRedirected: true), "An unknown redirect cannot establish a document.");
        Check(!state.CanReportFailure(10, 1), "An unknown navigation cannot report a current failure.");
        Check(state.Start(10, 1) && state.ReadyNavigationId is null, "Navigation starts with no document ready.");
        Check(state.TryMarkReady(10) && state.CanInspect(10), "The current DOM can be inspected after it becomes ready.");
        var firstRevision = state.Revision;
        Check(state.TryMarkReady(10) && state.Revision == firstRevision && state.IsCurrent(10, firstRevision), "DOMContentLoaded and NavigationCompleted for one document do not invalidate inspection.");

        Check(state.Start(20, 1) && state.ReadyNavigationId is null && !state.IsCurrent(10, firstRevision), "A newer navigation immediately invalidates an in-flight inspection of the old document.");
        var secondRevision = state.Revision;
        Check(!state.TryMarkReady(10) && state.ReadyNavigationId is null && state.Revision == secondRevision, "A late successful completion from the old document cannot mark it ready again.");
        Check(!state.Start(10, 1, isRedirected: true) && state.Revision == secondRevision, "A redirect from the old navigation cannot replace the newer navigation.");
        Check(!state.CanReportFailure(10, 1) && state.CanReportFailure(20, 1), "Only the current navigation may report its request's page failure.");
        Check(state.TryMarkReady(20) && state.IsCurrent(20, secondRevision), "The new DOM can be inspected after the older event is ignored.");
        Check(!state.TryMarkReady(10) && state.ReadyNavigationId == 20 && state.IsCurrent(20, secondRevision), "An older completion cannot overwrite an already-ready new document.");

        Check(state.Start(20, 2, isRedirected: true) && state.ReadyNavigationId is null && !state.IsCurrent(20, secondRevision), "A current redirect invalidates inspection even when the navigation ID is unchanged.");
        Check(state.CanReportFailure(20, 1) && !state.CanReportFailure(20, 2), "Redirects retain their original request generation.");
        Check(state.TryMarkReady(20), "The redirected document can become ready.");

        var beforeExpected = state.Revision;
        state.ExpectNavigation();
        Check(state.Revision != beforeExpected && state.ReadyNavigationId is null && !state.IsCurrent(20, beforeExpected), "Programmatic navigation invalidates readiness before NavigationStarting arrives.");
        Check(!state.TryMarkReady(20) && !state.Start(20, 2, isRedirected: true) && !state.Start(20, 2), "Old completion and redirect events are ignored while awaiting a new navigation start.");
        Check(!state.CanReportFailure(20, 1) && !state.CanReportFailure(20, 2), "The old download navigation cannot report an error during reference navigation handoff.");
        Check(state.Start(30, 2) && state.ReadyNavigationId is null, "The expected new start establishes its own request generation.");
        Check(!state.TryMarkReady(20) && state.TryMarkReady(30) && state.ReadyNavigationId == 30, "Only the new DOM can end the expected-navigation gap.");
        Check(!state.CanReportFailure(20, 2) && !state.CanReportFailure(30, 1) && state.CanReportFailure(30, 2), "Reference errors are isolated from old download and old request generations.");

        Check(state.Start(5, 3) && state.TryMarkReady(5), "Navigation IDs are equality tokens, not assumed to be numerically ordered.");
        var revision = state.Revision;
        Check(!state.Start(10, 3) && state.IsCurrent(5, revision), "A previously replaced navigation cannot become current again through a duplicate start event.");
        Check(!state.CanReportFailure(5, 4), "A cancelled or rearmed request cannot inherit an older page failure.");
        state.ExpectNavigation();
        state.ExpectNavigation();
        Check(!state.TryMarkReady(5) && state.Start(6, 4) && state.TryMarkReady(6), "Repeated programmatic navigation invalidation still accepts the next new document.");

        var otherBrowser = new GngBrowserNavigationState();
        Check(otherBrowser.Start(6, 4) && otherBrowser.TryMarkReady(6), "Browser instances can independently use the same navigation ID.");
        otherBrowser.ExpectNavigation();
        Check(state.CanInspect(6) && !otherBrowser.CanInspect(6), "Popup navigation does not invalidate the main browser's document.");
        return count;
    }
}
