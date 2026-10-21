using System.Text;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

internal static class ReportingContractTests
{
    internal static async Task<int> Run()
    {
        var now = DateTime.UtcNow;
        ReportingEvent Event(ReportingEventName name) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), now,
            "2.6.0", name, Application: ReportingApplication.Vatis);
        var error = Event(ReportingEventName.OperationFailed) with
        {
            Operation = ReportingOperation.Check, ErrorCode = ReportingErrorCode.RateLimited,
            Reason = ReportingReason.RateLimited, Service = ReportingRemoteService.GitHub, HttpStatus = 403
        };
        Require(ReportingWire.ValidEvent(error, now));
        foreach (int status in new[] { 99, 600, int.MaxValue })
            Require(!ReportingWire.ValidEvent(error with { HttpStatus = status }, now));
        Require(!ReportingWire.ValidEvent(error with { Service = (ReportingRemoteService)500 }, now));
        Require(!ReportingWire.ValidEvent(error with { Reason = (ReportingReason)500 }, now));
        Require(!ReportingWire.ValidEvent(error with { View = ReportingView.Apps }, now));
        Console.WriteLine("PASS diagnostic categories and HTTP statuses have a bounded wire shape");

        foreach (var name in new[] { ReportingEventName.SessionStarted, ReportingEventName.FeatureUsed, ReportingEventName.OperationStarted })
            Require(!ReportingWire.ValidEvent(Event(name) with { Reason = ReportingReason.ClientRunning }, now));
        Require(!ReportingWire.ValidEvent(Event(ReportingEventName.OperationFinished) with { Service = ReportingRemoteService.GitHub }, now));
        Require(!ReportingWire.ValidEvent(Event(ReportingEventName.OperationFinished) with { HttpStatus = 403 }, now));
        foreach (var view in Enum.GetValues<ReportingView>())
            Require(ReportingWire.ValidEvent(Event(ReportingEventName.ViewOpened) with { View = view }, now));
        Require(!ReportingWire.ValidEvent(Event(ReportingEventName.ViewOpened), now));
        Require(!ReportingWire.ValidEvent(Event(ReportingEventName.ViewOpened) with { View = (ReportingView)500 }, now));
        Require(!ReportingWire.ValidEvent(Event(ReportingEventName.SessionStarted) with { View = ReportingView.Apps }, now));
        Console.WriteLine("PASS navigation and diagnostic fields stay in their intended event types");

        foreach (bool usage in new[] { false, true })
        foreach (bool errors in new[] { false, true })
        {
            using var fixture = new Fixture();
            Require(fixture.Client.TrySavePreferences(usage, false, errors));
            var finish = new ReportingDetails(OperationId: Guid.NewGuid(), Application: ReportingApplication.Vatis,
                Operation: ReportingOperation.ConfigureSwedenProfile, Outcome: ReportingOutcome.Blocked,
                Reason: ReportingReason.ClientRunning);
            Require(fixture.Client.RecordUsage(ReportingEventName.OperationFinished, finish) == usage);
            Require(fixture.Client.RecordUsage(ReportingEventName.ViewOpened, new(View: ReportingView.SetupGng)) == usage);
            Require(fixture.Client.RecordError(ReportingErrorCode.RateLimited,
                new(Application: ReportingApplication.Vatis, Operation: ReportingOperation.Check,
                    Reason: ReportingReason.RateLimited, Service: ReportingRemoteService.GitHub, HttpStatus: 403)) == errors);
            await fixture.Client.FlushOnceAsync();
            await fixture.Client.FlushOnceAsync();
            var history = fixture.Client.History;
            Require(history.LastSent.Any(receipt => receipt.Stream == ReportingStream.Usage) == usage);
            Require(history.LastSent.Any(receipt => receipt.Stream == ReportingStream.Errors) == errors);
            foreach (var receipt in history.LastSent)
                Require(ReportingHistoryValidation.Valid(receipt, history.SupportId));
        }
        Console.WriteLine("PASS new usage and error details respect independent consent and retained receipts");

        var batch = new ReportingBatch(1, [error]);
        var json = Encoding.UTF8.GetString(ReportingWire.Bytes(batch));
        Require(json.Contains("\"service\":\"github\""));
        var description = ReportingHistoryPresentation.Describe(new(ReportingStream.Errors, now, json));
        Require(description.Contains("Online service: GitHub") && description.Contains("HTTP status: 403") &&
            description.Contains("Reason: Rate Limited", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine("PASS sent-report viewer explains service, reason and HTTP status");
        return 4;
    }

    private static void Require(bool condition)
    { if (!condition) throw new Exception("Reporting contract assertion failed."); }
}
