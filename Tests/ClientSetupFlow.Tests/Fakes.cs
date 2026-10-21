namespace VatscaUpdateChecker
{
    using VatscaUpdateChecker.Models;
    internal static class ReportingEvents
    {
        internal static readonly List<(ReportingApplication App, ReportingOperation Operation)> Starts = [];
        internal static readonly List<(ReportingApplication App, ReportingOperation Operation, ReportingOutcome Outcome, ReportingReason? Reason)> Finishes = [];
        internal static ReportingApplication ForSoftware(SoftwareApp app) => app == SoftwareApp.Vatis ? ReportingApplication.Vatis : ReportingApplication.Trackaudio;
        internal static void Start(ReportingApplication app, ReportingOperation operation) => Starts.Add((app, operation));
        internal static Guid? CurrentOperationId(ReportingApplication app, ReportingOperation operation) => null;
        internal static void Complete(ReportingApplication app, ReportingOperation operation, ReportingOutcome outcome, ReportingReason? reason = null) => Finishes.Add((app, operation, outcome, reason));
        internal static void Stop(Exception error, ReportingApplication app, ReportingOperation operation, ReportingStage? stage = null) => Complete(app, operation, ReportingOutcome.Failed);
    }
}

namespace VatscaUpdateChecker.Services
{
    using VatscaUpdateChecker.Models;
    public static class ReportingService
    {
        public static readonly List<ReportingDetails> Errors = [];
        public static void RecordError(ReportingErrorCode code, ReportingDetails details) => Errors.Add(details);
    }
    public static class CredentialManagerService
    {
        public const string TargetVatsim = "synthetic";
        public static Func<string> Read = () => "synthetic-password";
        public static string LoadForEditing(string target) => Read();
    }
    public static class MaintenanceLock
    {
        public static bool IsHeldByCurrentProcess;
        public static bool Available = true;
        public static int Acquired, Disposed;
        public static bool TryAcquire(out IDisposable? lease)
        {
            lease = null;
            if (!Available) return false;
            Acquired++; IsHeldByCurrentProcess = true; lease = new FakeLease(); return true;
        }
        private sealed class FakeLease : IDisposable
        {
            public void Dispose() { Disposed++; IsHeldByCurrentProcess = false; }
        }
    }
    public static class ClientSignInService
    {
        public static Func<AppSettings, string, SoftwareApp?, IReadOnlyList<ClientSignInOutcome>> Run = (_, _, _) => [];
        public static IReadOnlyList<ClientSignInOutcome> Apply(AppSettings settings, string password, SoftwareApp? app) => Run(settings, password, app);
    }
    public sealed class VatisSwedenProfileService
    {
        public static Func<Task<VatisSwedenProfileResult>> Run = () => Task.FromResult(new VatisSwedenProfileResult(VatisSwedenProfileStatus.Installed, "Swedish profile ready."));
        public Task<VatisSwedenProfileResult> EnsureAsync(AppSettings settings, CancellationToken token) => Run();
    }
    public enum VatisSwedenProfileStatus { Installed, AlreadyConfigured, NotConfigured, Blocked, Conflict, Failed }
    public sealed record VatisSwedenProfileResult(VatisSwedenProfileStatus Status, string Message)
    {
        public bool Success => Status is VatisSwedenProfileStatus.Installed or VatisSwedenProfileStatus.AlreadyConfigured;
    }
}
