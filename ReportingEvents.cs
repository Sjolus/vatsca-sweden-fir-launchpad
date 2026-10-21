using System.ComponentModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

internal static class ReportingEvents
{
    // Enum pairs bound this map; a reporting identity change ends correlation.
    private static readonly ReportingAttemptTracker Attempts = new(() => ReportingService.State.SupportId);

    internal static ReportingApplication ForSoftware(SoftwareApp app) => app switch
    {
        SoftwareApp.TrackAudio => ReportingApplication.Trackaudio, SoftwareApp.Vacs => ReportingApplication.Vacs,
        SoftwareApp.Vatis => ReportingApplication.Vatis, SoftwareApp.VatEfs => ReportingApplication.Vatefs,
        _ => ReportingApplication.Launchpad
    };

    internal static void Start(ReportingApplication app, ReportingOperation operation, ReportingSource? source = null)
    {
        if (!Enum.IsDefined(app) || !Enum.IsDefined(operation)) return;
        ReportingService.RecordUsage(ReportingEventName.OperationStarted, Attempts.Start(app, operation, source));
        if (operation is ReportingOperation.Install or ReportingOperation.Update or ReportingOperation.Replace or ReportingOperation.Repair)
            Feature(ReportingFeature.InstallOrUpdate, app);
    }

    internal static Guid? CurrentOperationId(ReportingApplication app, ReportingOperation operation) =>
        Attempts.CurrentId(app, operation);

    internal static void Feature(ReportingFeature feature, ReportingApplication application = ReportingApplication.Launchpad) =>
        ReportingService.RecordUsage(ReportingEventName.FeatureUsed, new ReportingDetails { Application = application, Feature = feature });

    internal static ReportingDurationBucket? Finish(ReportingApplication app, ReportingOperation operation) =>
        Attempts.Complete(app, operation, ReportingOutcome.Unknown).DurationBucket;

    internal static void Complete(ReportingApplication app, ReportingOperation operation, ReportingOutcome outcome,
        ReportingReason? reason = null, ReportingDetails? details = null) =>
        ReportingService.RecordUsage(ReportingEventName.OperationFinished, Attempts.Complete(app, operation, outcome, reason, details));

    internal static void Stop(Exception exception, ReportingApplication app, ReportingOperation operation, ReportingStage? stage = null)
    {
        RecordFailure(exception, app, operation, stage);
        Complete(app, operation, FailureOutcome(exception), FailureReason(exception));
    }

    internal static void View(ReportingView view) =>
        ReportingService.RecordUsage(ReportingEventName.ViewOpened, new ReportingDetails { View = view });

    internal static ReportingDetails SoftwareUpdateResult(SoftwareUpdateState state, bool pathSaveFailed,
        bool restartRequired, string? previousVersion) => new(
            Application: ForSoftware(state.App), Operation: ReportingOperation.Update,
            Outcome: state.Phase == SoftwareUpdatePhase.Completed ? pathSaveFailed ? ReportingOutcome.Partial : ReportingOutcome.Completed :
                state.Phase == SoftwareUpdatePhase.Cancelled ? ReportingOutcome.Cancelled : ReportingOutcome.Unknown,
            Reason: state.Phase == SoftwareUpdatePhase.Completed && pathSaveFailed ? ReportingReason.SaveFailed : null,
            PackageVersion: PublicVersion(state.LatestVersion), PreviousVersion: PublicVersion(previousVersion),
            RestartRequired: restartRequired, RecoveryAvailable: state.BackupFolder is not null);

    internal static string? PublicVersion(string? value)
    {
        if (value is null || value.Length > 64) return null;
        value = value.TrimStart('v', 'V');
        if (ReportingWire.Version(value)) return value;
        var gng = Regex.Match(value, @"\A([0-9]{4})/([0-9]{2}) {1,2}rev\.([0-9]{1,6})\z", RegexOptions.CultureInvariant);
        return gng.Success ? $"{gng.Groups[1].Value}.{int.Parse(gng.Groups[2].Value)}.{int.Parse(gng.Groups[3].Value)}" : null;
    }

    // Expected guards/cancellation are not defects. Only a fixed category crosses the reporting boundary.
    internal static void RecordFailure(Exception exception, ReportingApplication application, ReportingOperation operation, ReportingStage? stage = null)
    {
        var code = Classify(exception);
        if (code is null) return;
        ReportingService.RecordError(code.Value, FailureDetails(exception, application, operation, stage) with
        { OperationId = CurrentOperationId(application, operation) });
    }

    internal static ReportingDetails FailureDetails(Exception exception, ReportingApplication application, ReportingOperation operation, ReportingStage? stage = null) =>
        new(Application: application, Operation: operation, Outcome: ReportingOutcome.Failed, Stage: stage,
            Reason: FailureReason(exception),
            Service: exception is VendorRequestException vendor ? vendor.Vendor switch
            { VendorService.GitHub => ReportingRemoteService.GitHub, VendorService.AeroNav => ReportingRemoteService.AeroNav, _ => ReportingRemoteService.Vatis } : null,
            HttpStatus: exception is HttpRequestException { StatusCode: { } status } && (int)status is >= 100 and <= 599 ? (int)status : null);

    internal static ReportingOutcome FailureOutcome(Exception exception) => exception switch
    {
        SoftwareInstallBehaviorException => ReportingOutcome.Failed,
        OperationCanceledException or Win32Exception { NativeErrorCode: 1223 } => ReportingOutcome.Cancelled,
        InvalidOperationException or VendorRequestException { FailureKind: VendorFailureKind.RateLimited or VendorFailureKind.Forbidden } => ReportingOutcome.Blocked,
        _ => ReportingOutcome.Failed
    };

    internal static ReportingReason? FailureReason(Exception exception) => exception switch
    {
        VendorRequestException { FailureKind: VendorFailureKind.RateLimited } => ReportingReason.RateLimited,
        VendorRequestException { FailureKind: VendorFailureKind.Forbidden } or UnauthorizedAccessException => ReportingReason.AccessDenied,
        VendorRequestException { FailureKind: VendorFailureKind.Timeout } or TimeoutException => ReportingReason.Timeout,
        HttpRequestException => ReportingReason.NetworkUnavailable,
        InvalidDataException => ReportingReason.VerificationFailed,
        OperationCanceledException or InvalidOperationException or Win32Exception { NativeErrorCode: 1223 } => null,
        _ => ReportingReason.Unexpected
    };

    internal static ReportingErrorCode? Classify(Exception exception) => exception switch
    {
        SoftwareInstallBehaviorException => ReportingErrorCode.InstallationFailed,
        VendorRequestException { FailureKind: VendorFailureKind.RateLimited } => ReportingErrorCode.RateLimited,
        VendorRequestException { FailureKind: VendorFailureKind.Forbidden } or UnauthorizedAccessException => ReportingErrorCode.AccessDenied,
        VendorRequestException { FailureKind: VendorFailureKind.Timeout } => ReportingErrorCode.DownloadTimeout,
        OperationCanceledException or InvalidOperationException => null,
        Win32Exception { NativeErrorCode: 1223 } => null,
        TimeoutException => ReportingErrorCode.DownloadTimeout,
        HttpRequestException => ReportingErrorCode.NetworkUnavailable,
        InvalidDataException => ReportingErrorCode.PackageInvalid,
        _ => ReportingErrorCode.UnexpectedError
    };
}

// One bounded entry per enum pair. An identity change cannot correlate a previous identity's action.
internal sealed class ReportingAttemptTracker(Func<Guid> supportId)
{
    private sealed record Attempt(long Started, Guid Id, Guid SupportId, ReportingSource? Source);
    private readonly ConcurrentDictionary<(ReportingApplication, ReportingOperation), Attempt> _attempts = new();
    internal ReportingDetails Start(ReportingApplication app, ReportingOperation operation, ReportingSource? source = null)
    {
        var attempt = new Attempt(Stopwatch.GetTimestamp(), Guid.NewGuid(), supportId(), source);
        _attempts[(app, operation)] = attempt;
        return new(OperationId: attempt.Id, Application: app, Operation: operation, Stage: ReportingStage.Start, Source: source);
    }
    internal Guid? CurrentId(ReportingApplication app, ReportingOperation operation) =>
        _attempts.TryGetValue((app, operation), out var attempt) && attempt.SupportId == supportId() ? attempt.Id : null;
    internal ReportingDetails Complete(ReportingApplication app, ReportingOperation operation, ReportingOutcome outcome,
        ReportingReason? reason = null, ReportingDetails? details = null)
    {
        Guid? id = null; ReportingDurationBucket? duration = null; ReportingSource? source = null;
        if (_attempts.TryRemove((app, operation), out var attempt) && attempt.SupportId == supportId())
        {
            id = attempt.Id; source = attempt.Source;
            var seconds = Stopwatch.GetElapsedTime(attempt.Started).TotalSeconds;
            duration = seconds < 1 ? ReportingDurationBucket.Under1s : seconds < 10 ? ReportingDurationBucket.Under10s :
                seconds < 60 ? ReportingDurationBucket.Under1m : seconds < 300 ? ReportingDurationBucket.Under5m : ReportingDurationBucket.Over5m;
        }
        return (details ?? new()) with { OperationId = id, Application = app, Operation = operation,
            Outcome = outcome, Reason = reason, Stage = ReportingStage.Finish, DurationBucket = duration, Source = source ?? details?.Source };
    }
}

internal sealed class ReportingViewTracker
{
    private ReportingView? _current;
    private Guid _identity;
    internal bool Transition(ReportingView view, Guid identity = default)
    {
        if (_current == view && _identity == identity) return false;
        _identity = identity;
        _current = view;
        return true;
    }
}
