using System.Net;
using System.Text.Json;
using VatscaUpdateChecker;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

internal static class ReportingInstrumentationTests
{
    internal static int Run()
    {
        int checks = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
        Guid identity = Guid.NewGuid();
        var attempts = new ReportingAttemptTracker(() => identity);
        var first = attempts.Start(ReportingApplication.Gng, ReportingOperation.Adopt, ReportingSource.Browse);
        var second = attempts.Start(ReportingApplication.Saaman, ReportingOperation.ConfigurePluginProfile);
        var finish = attempts.Complete(ReportingApplication.Gng, ReportingOperation.Adopt, ReportingOutcome.Completed);
        Check(finish.OperationId == first.OperationId && finish.DurationBucket is not null && finish.Source == ReportingSource.Browse,
            "adoption completion retains its start ID, duration and source");
        Check(attempts.CurrentId(ReportingApplication.Saaman, ReportingOperation.ConfigurePluginProfile) == second.OperationId,
            "finishing one application leaves another application's attempt intact");
        Check(attempts.Complete(ReportingApplication.Gng, ReportingOperation.Adopt, ReportingOutcome.Completed).OperationId is null,
            "a second completion cannot reuse the finished attempt");
        identity = Guid.NewGuid();
        Check(attempts.CurrentId(ReportingApplication.Saaman, ReportingOperation.ConfigurePluginProfile) is null &&
            attempts.Complete(ReportingApplication.Saaman, ReportingOperation.ConfigurePluginProfile, ReportingOutcome.Completed).OperationId is null,
            "regenerating the support ID ends previous operation correlation");
        var views = new ReportingViewTracker();
        Check(views.Transition(ReportingView.Apps, identity) && !views.Transition(ReportingView.Apps, identity) &&
            views.Transition(ReportingView.SetupApplications, identity) && views.Transition(ReportingView.Apps, identity),
            "view reporting follows transitions, not repeated refreshes");
        Check(views.Transition(ReportingView.Apps, Guid.NewGuid()), "a new support ID can report the current view");

        var limited = new VendorRequestException(VendorService.GitHub, VendorFailureKind.RateLimited, HttpStatusCode.Forbidden,
            message: "private path and token https://example.invalid/?secret=hidden");
        var denied = new VendorRequestException(VendorService.GitHub, VendorFailureKind.Forbidden, HttpStatusCode.Forbidden);
        Check(ReportingEvents.Classify(limited) == ReportingErrorCode.RateLimited && ReportingEvents.FailureOutcome(limited) == ReportingOutcome.Blocked &&
            ReportingEvents.FailureReason(limited) == ReportingReason.RateLimited,
            "typed rate limits are blocked attempts with their own error category");
        Check(ReportingEvents.Classify(denied) == ReportingErrorCode.AccessDenied && ReportingEvents.FailureReason(denied) == ReportingReason.AccessDenied,
            "an ordinary 403 remains access denied, not a rate limit");
        var details = ReportingEvents.FailureDetails(limited, ReportingApplication.Saaman, ReportingOperation.Check, ReportingStage.Check);
        string json = JsonSerializer.Serialize(details);
        Check(details.Service == ReportingRemoteService.GitHub && details.HttpStatus == 403 && details.Operation == ReportingOperation.Check &&
            !json.Contains("private") && !json.Contains("secret") && !json.Contains("example.invalid"),
            "network reporting contains fixed dimensions and excludes the exception message and URL");
        var timeout = new VendorRequestException(VendorService.AeroNav, VendorFailureKind.Timeout);
        Check(ReportingEvents.Classify(timeout) == ReportingErrorCode.DownloadTimeout && ReportingEvents.FailureReason(timeout) == ReportingReason.Timeout,
            "typed vendor timeout stays distinct from caller cancellation");
        Check(ReportingEvents.Classify(new OperationCanceledException()) is null &&
            ReportingEvents.FailureOutcome(new OperationCanceledException()) == ReportingOutcome.Cancelled &&
            ReportingEvents.Classify(new InvalidOperationException("expected guard")) is null,
            "cancellation and generic guards do not become automatic defect reports");
        Check(ReportingEvents.Classify(new SoftwareInstallBehaviorException("failed verification")) == ReportingErrorCode.InstallationFailed &&
            ReportingEvents.FailureOutcome(new SoftwareInstallBehaviorException("failed verification")) == ReportingOutcome.Failed,
            "installer behavior failures remain failed rather than being mistaken for an expected guard");
        var update = new SoftwareUpdateState(SoftwareApp.TrackAudio, SoftwareUpdatePhase.Completed, "private location");
        var partial = ReportingEvents.SoftwareUpdateResult(update, true, false, "1.3.3");
        Check(partial.Outcome == ReportingOutcome.Partial && partial.Reason == ReportingReason.SaveFailed &&
            ReportingEvents.SoftwareUpdateResult(update, false, false, "1.3.3").Outcome == ReportingOutcome.Completed,
            "an installed update with an unsaved path reports partial success");
        Check(ReportingEvents.SoftwareUpdateResult(update with { Phase = SoftwareUpdatePhase.Error }, false, false, null).Outcome == ReportingOutcome.Unknown &&
            ReportingEvents.SoftwareUpdateResult(update with { Phase = SoftwareUpdatePhase.Cancelled }, false, false, null).Outcome == ReportingOutcome.Cancelled,
            "legacy ambiguous update stops remain unknown and cancellation stays separate");
        return checks;
    }
}
