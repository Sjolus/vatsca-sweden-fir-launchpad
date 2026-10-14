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
    private sealed record Attempt(long Started, Guid Id, Guid SupportId);
    private static readonly ConcurrentDictionary<(ReportingApplication, ReportingOperation), Attempt> Started = new();

    internal static ReportingApplication ForSoftware(SoftwareApp app) => app switch
    {
        SoftwareApp.TrackAudio => ReportingApplication.Trackaudio, SoftwareApp.Vacs => ReportingApplication.Vacs,
        SoftwareApp.Vatis => ReportingApplication.Vatis, SoftwareApp.VatEfs => ReportingApplication.Vatefs,
        _ => ReportingApplication.Launchpad
    };

    internal static void Start(ReportingApplication app, ReportingOperation operation)
    {
        if (!Enum.IsDefined(app) || !Enum.IsDefined(operation)) return;
        var attempt = new Attempt(Stopwatch.GetTimestamp(), Guid.NewGuid(), ReportingService.State.SupportId);
        Started[(app, operation)] = attempt;
        ReportingService.RecordUsage(ReportingEventName.OperationStarted, new ReportingDetails
        { OperationId = attempt.Id, Application = app, Operation = operation, Stage = ReportingStage.Start });
        if (operation is ReportingOperation.Install or ReportingOperation.Update or ReportingOperation.Replace or ReportingOperation.Repair)
            Feature(ReportingFeature.InstallOrUpdate, app);
    }

    internal static Guid? CurrentOperationId(ReportingApplication app, ReportingOperation operation) =>
        Started.TryGetValue((app, operation), out var attempt) && attempt.SupportId == ReportingService.State.SupportId
            ? attempt.Id : null;

    internal static void Feature(ReportingFeature feature, ReportingApplication application = ReportingApplication.Launchpad) =>
        ReportingService.RecordUsage(ReportingEventName.FeatureUsed, new ReportingDetails { Application = application, Feature = feature });

    internal static ReportingDurationBucket? Finish(ReportingApplication app, ReportingOperation operation)
    {
        if (!Started.TryRemove((app, operation), out var attempt) || attempt.SupportId != ReportingService.State.SupportId) return null;
        var seconds = Stopwatch.GetElapsedTime(attempt.Started).TotalSeconds;
        return seconds < 1 ? ReportingDurationBucket.Under1s : seconds < 10 ? ReportingDurationBucket.Under10s :
            seconds < 60 ? ReportingDurationBucket.Under1m : seconds < 300 ? ReportingDurationBucket.Under5m : ReportingDurationBucket.Over5m;
    }

    internal static string? PublicVersion(string? value)
    {
        if (value is null || value.Length > 64) return null;
        value = value.TrimStart('v', 'V');
        if (ReportingWire.Version(value)) return value;
        var gng = Regex.Match(value, @"\A([0-9]{4})/([0-9]{2}) {1,2}rev\.([0-9]{1,6})\z", RegexOptions.CultureInvariant);
        return gng.Success ? $"{gng.Groups[1].Value}.{int.Parse(gng.Groups[2].Value)}.{int.Parse(gng.Groups[3].Value)}" : null;
    }

    // Expected guards/cancellation are not defects. Only a fixed category crosses the reporting boundary.
    internal static void RecordFailure(Exception exception, ReportingApplication application, ReportingOperation operation)
    {
        var code = Classify(exception);
        if (code is null) return;
        ReportingService.RecordError(code.Value, new ReportingDetails
        { OperationId = CurrentOperationId(application, operation), Application = application, Operation = operation, Outcome = ReportingOutcome.Failed });
    }

    internal static ReportingErrorCode? Classify(Exception exception) => exception switch
    {
        SoftwareInstallBehaviorException => ReportingErrorCode.InstallationFailed,
        OperationCanceledException or InvalidOperationException => null,
        Win32Exception { NativeErrorCode: 1223 } => null,
        TimeoutException => ReportingErrorCode.DownloadTimeout,
        HttpRequestException => ReportingErrorCode.NetworkUnavailable,
        InvalidDataException => ReportingErrorCode.PackageInvalid,
        _ => ReportingErrorCode.UnexpectedError
    };
}
