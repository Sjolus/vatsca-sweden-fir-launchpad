using VatscaUpdateChecker.Services;

internal static class DownloadRequestTests
{
    public static int Run()
    {
        int count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            count++;
        }
        var state = new GngDownloadRequestState();
        Check(!state.IsPending && !state.TryStartDownload() && !state.TryBeginSelection(out _), "Opening a window cannot request or accept a download.");
        state.Arm();
        Check(state.CanReportNavigationFailure(state.Generation), "A page failure before selection remains visible.");
        Check(state.IsPending && !state.TryStartDownload(), "Sign-in alone cannot supply an unselected download.");
        Check(state.TryBeginSelection(out var first), "Explicit request can select a package.");
        Check(!state.TryBeginSelection(out _), "Root and popup events cannot select concurrently.");
        Check(state.CompleteSelection(first, "waiting-for-login") && state.IsPending, "Request remains armed while the user signs in.");
        Check(state.TryBeginSelection(out var second), "A later authenticated document can select.");
        Check(!state.TryStartDownload(), "No download is accepted before read-only inspection records an identity.");
        Check(state.CompleteSelection(second, "ready", "20261001120000-261001-0003"), "Read-only inspection records identity before the download click.");
        Check(!state.CanReportNavigationFailure(second), "Download navigation errors cannot replace transfer status.");
        Check(state.TryStartDownload(), "Download may start before the click callback completes.");
        Check(!state.CompleteClick(second, "ambiguous") && state.Phase == GngDownloadRequestPhase.Downloading, "A missing click result after navigation cannot overwrite an accepted download.");
        Check(!state.ExpireDownloadWait(second), "The start timeout cannot stop an active transfer.");
        Check(state.MatchesSelectedFilename("ESAA-Update-Only_20261001120000-261001-0003.zip"), "Late selector metadata still verifies the completed download.");
        Check(state.MatchesSelectedFilename("ESAA-Update-Only_20261001120000-261001-0003 (2).zip"), "Browser duplicate suffix retains selected identity.");
        Check(!state.MatchesSelectedFilename("ESAA-Update-Only_20261001120000-261001-0002.zip"), "Different revision cannot replace the selected download.");
        Check(!state.MatchesSelectedFilename("ESAA-Update-Only_20261001120001-261001-0003.zip"), "A different primary release timestamp must be reviewed through its own selection.");
        Check(!state.TryStartDownload() && !state.TryBeginSelection(out _), "One request permits only one download.");
        state.Disarm();
        Check(!state.CanReportNavigationFailure(second), "Delayed page errors cannot overwrite the stopped request's diagnostic.");
        Check(!state.MatchesSelectedFilename("ESAA-Update-Only_20261001120000-261001-0003.zip"), "Disarming clears the selected identity.");
        Check(!state.CompleteSelection(second, "ready", "20261001120000-261001-0003") && !state.CompleteClick(second, "clicked") && !state.TryStartDownload(), "Cancel/import/close invalidate outstanding callbacks and downloads.");
        state.Arm();
        Check(state.TryBeginSelection(out var third) && state.CompleteSelection(third, "ready", "20261001120000-261001-0003") && state.CompleteClick(third, "clicked"), "Explicit retry can inspect and click afresh.");
        Check(!state.ExpireDownloadWait(second), "A timer from an older request cannot expire the retry.");
        Check(state.ExpireDownloadWait(third) && !state.IsPending && !state.TryStartDownload(), "Missing download stops instead of retrying indefinitely.");
        foreach (var result in new[] { "no-package", "ambiguous", "unsupported-link", "already-attempted", "malformed" })
        {
            state.Arm(); state.TryBeginSelection(out var generation);
            Check(state.CompleteSelection(generation, result) && !state.IsPending && !state.TryBeginSelection(out _), "Unsuccessful selection requires explicit retry: " + result);
        }
        state.Arm(); state.TryBeginSelection(out var navigating);
        Check(state.CompleteSelection(navigating, "wrong-page") && state.IsPending, "Navigation during selection waits for the package page without clicking elsewhere.");
        state.Disarm(); state.Arm();
        Check(!state.CompleteSelection(navigating, "ready", "20261001120000-261001-0003") && state.Phase == GngDownloadRequestPhase.WaitingForPage, "Stale result cannot consume a newly armed request.");
        state.TryBeginSelection(out var missingIdentity);
        Check(state.CompleteSelection(missingIdentity, "ready") && !state.TryStartDownload(), "Ready without identity cannot authorize a download.");
        state.Arm(); state.TryBeginSelection(out var changed);
        state.CompleteSelection(changed, "ready", "20261001120000-261001-0003");
        Check(state.CompleteClick(changed, "selection-changed") && !state.TryStartDownload(), "A changed package between inspection and click cannot be downloaded.");
        state.Disarm(); state.Arm(referenceFollows: true);
        Check(!state.TryBeginReference(), "A reference cannot start before the primary download.");
        state.TryBeginSelection(out var primary); state.CompleteSelection(primary, "ready", "20261001120000-261001-0003"); state.TryStartDownload();
        Check(state.TryBeginReference() && state.IsPending, "One explicit paired request can continue to its reference download.");
        Check(!state.CanReportNavigationFailure(primary) && state.CanReportNavigationFailure(state.Generation), "Old transfer navigation errors are ignored while a genuine reference-page failure remains visible.");
        Check(!state.CompleteSelection(primary, "ready", "20261001120000-261001-0003") && !state.CompleteClick(primary, "clicked"), "A primary callback cannot mutate the reference request.");
        state.TryBeginSelection(out var reference); state.CompleteSelection(reference, "ready", "20261001120000-261001-0003"); state.TryStartDownload();
        Check(!state.TryBeginReference(), "A completed reference cannot start a third download.");
        state.Disarm(); state.Arm(referenceFollows: true); state.TryBeginSelection(out var cancelled); state.CompleteSelection(cancelled, "ready", "20261001120000-261001-0003"); state.TryStartDownload(); state.Disarm();
        Check(!state.TryBeginReference() && !state.IsPending, "Cancellation stops the whole batch, including an unstarted reference.");
        state.Arm(referenceFollows: true);
        for (int i = 0; i < 4; i++)
        {
            Check(state.TryBeginSelection(out var beforeNavigation), "A ready page can be inspected after a normal navigation.");
            Check(state.RetryChangedInspection(beforeNavigation) == GngInspectionRetry.WaitingForPage && state.IsPending && !state.TryStartDownload(),
                "Changing pages discards inspection metadata and preserves the user's request without starting a download.");
        }
        state.TryBeginSelection(out var unstable);
        Check(state.RetryChangedInspection(unstable) == GngInspectionRetry.LimitReached && !state.IsPending, "Repeated unstable inspections eventually require an explicit retry.");
        state.Arm(referenceFollows: true);
        state.TryBeginSelection(out var staleInspection);
        state.Disarm(); state.Arm();
        Check(state.RetryChangedInspection(staleInspection) == GngInspectionRetry.Ignored, "A cancelled inspection cannot rearm a newer request.");
        state.TryBeginSelection(out var resumed);
        Check(state.RetryChangedInspection(resumed) == GngInspectionRetry.WaitingForPage, "An explicit retry resets the instability limit.");
        state.TryBeginSelection(out var stable);
        state.CompleteSelection(stable, "ready", "20261001120000-261001-0003"); state.TryStartDownload();
        Check(state.RetryChangedInspection(stable) == GngInspectionRetry.Ignored, "Post-click navigation cannot restart inspection or authorize a duplicate transfer.");
        var page = new GngBrowserNavigationState();
        var batch = new GngDownloadRequestState();
        batch.Arm(referenceFollows: true);
        page.Start(100, batch.Generation); page.TryMarkReady(100);
        batch.TryBeginSelection(out var inspecting);
        var inspectedRevision = page.Revision;
        page.ExpectNavigation();
        Check(page.ReadyNavigationId is null && !page.TryMarkReady(100), "A queued busy-clear callback cannot inspect the previous page after an app-issued reload.");
        page.Start(101, batch.Generation); page.TryMarkReady(101);
        Check(!page.IsCurrent(100, inspectedRevision) && batch.RetryChangedInspection(inspecting) == GngInspectionRetry.WaitingForPage,
            "A normal redirect while inspection awaits discards only the stale result, not the paired download request.");
        Check(!page.TryMarkReady(100) && page.CanInspect(101) && batch.TryBeginSelection(out _), "A late completion cannot prevent inspecting the authenticated replacement page.");
        batch.CompleteSelection(batch.Generation, "ready", "20261001120000-261001-0003");
        Check(batch.TryStartDownload() && !batch.TryStartDownload(), "The replacement page starts exactly one primary transfer.");
        Check(batch.TryBeginReference(), "Navigation retry retained authorization for the matching reference.");
        page.ExpectNavigation();
        Check(page.ReadyNavigationId is null && !page.CanReportFailure(101, batch.Generation), "Reference handoff waits for its requested page instead of rechecking stale primary content.");
        page.Start(102, batch.Generation); page.TryMarkReady(102);
        Check(!page.TryMarkReady(101) && page.CanInspect(102) && batch.TryBeginSelection(out _), "Reference selection ignores a late primary completion.");
        batch.CompleteSelection(batch.Generation, "ready", "20261001115900-261001-0003");
        Check(batch.TryStartDownload() && !batch.TryStartDownload() && !batch.TryBeginReference(), "The recovered batch starts one reference and no third transfer.");
        return count;
    }
}
