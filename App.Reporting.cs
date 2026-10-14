using System.Reflection;
using System.Windows.Threading;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class App
{
    private void InitializeReporting()
    {
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0];
        ReportingService.Initialize(ReportingEvents.PublicVersion(version) ?? "0.0.0-dev");
        ActivityHistoryService.Recorded += RecordOperationOutcome;
        DispatcherUnhandledException += ReportDispatcherFailure;
        AppDomain.CurrentDomain.UnhandledException += ReportFatalFailure;
        TaskScheduler.UnobservedTaskException += ReportUnobservedFailure;
    }

    private void ShutdownReporting()
    {
        ActivityHistoryService.Recorded -= RecordOperationOutcome;
        DispatcherUnhandledException -= ReportDispatcherFailure;
        AppDomain.CurrentDomain.UnhandledException -= ReportFatalFailure;
        TaskScheduler.UnobservedTaskException -= ReportUnobservedFailure;
        ReportingService.Shutdown();
    }

    private void ReportDispatcherFailure(object sender, DispatcherUnhandledExceptionEventArgs e) =>
        ReportingService.RecordError(ReportingErrorCode.UnexpectedError, fatal: true);
    private static void ReportFatalFailure(object sender, UnhandledExceptionEventArgs e) =>
        ReportingService.RecordError(ReportingErrorCode.UnexpectedError, fatal: true);
    private static void ReportUnobservedFailure(object? sender, UnobservedTaskExceptionEventArgs e) =>
        ReportingService.RecordError(ReportingErrorCode.UnexpectedError);

    private static void RecordOperationOutcome(ActivityEntry entry)
    {
        var application = Enum.TryParse<ReportingApplication>(entry.Application.ToString(), true, out var app) ? app : ReportingApplication.Launchpad;
        var operation = entry.Operation == ActivityOperation.RestoreCleanup ? ReportingOperation.RestoreCleanup
            : Enum.TryParse<ReportingOperation>(entry.Operation.ToString(), true, out var op) ? op : ReportingOperation.Check;
        var details = new ReportingDetails
        {
            OperationId = ReportingEvents.CurrentOperationId(application, operation),
            Application = application,
            Operation = operation,
            DurationBucket = ReportingEvents.Finish(application, operation),
            Outcome = entry.Outcome switch
            {
                ActivityOutcome.Completed => ReportingOutcome.Completed,
                ActivityOutcome.Cancelled => ReportingOutcome.Cancelled,
                _ => ReportingOutcome.Unknown
            },
            Stage = ReportingStage.Finish,
            PackageVersion = ReportingEvents.PublicVersion(entry.Version),
            PreviousVersion = ReportingEvents.PublicVersion(entry.PreviousVersion),
            RestartRequired = entry.RestartRequired,
            RecoveryAvailable = entry.RecoveryFolder is not null
        };
        ReportingService.RecordUsage(ReportingEventName.OperationFinished, details);
        // Activity's Stopped/NeedsAttention includes expected guards. Do not infer an exception from it.
    }
}
