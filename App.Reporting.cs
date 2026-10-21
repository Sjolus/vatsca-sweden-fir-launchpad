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
        // SAAMAN's owner records typed terminal results, including guards and partial saves.
        if (entry.Application == ActivityApplication.Saaman || entry.Operation == ActivityOperation.Configure) return;
        // Managed updates have one owner that can distinguish installed files from a failed path save.
        if (entry.Operation == ActivityOperation.Update && entry.Application is ActivityApplication.TrackAudio
            or ActivityApplication.Vacs or ActivityApplication.Vatis or ActivityApplication.VatEfs) return;
        var application = Enum.TryParse<ReportingApplication>(entry.Application.ToString(), true, out var app) ? app : ReportingApplication.Launchpad;
        var operation = entry.Operation == ActivityOperation.RestoreCleanup ? ReportingOperation.RestoreCleanup
            : Enum.TryParse<ReportingOperation>(entry.Operation.ToString(), true, out var op) ? op : ReportingOperation.Check;
        var details = new ReportingDetails
        {
            Application = application,
            Operation = operation,
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
        ReportingEvents.Complete(application, operation, details.Outcome!.Value, details: details);
        // Activity's Stopped/NeedsAttention includes expected guards. Do not infer an exception from it.
    }
}
