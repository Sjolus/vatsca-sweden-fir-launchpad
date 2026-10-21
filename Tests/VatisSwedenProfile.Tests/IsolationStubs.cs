// The service's injected environment owns every probe in this harness. Production probes must never run.
namespace VatscaUpdateChecker.Models
{
    public enum SoftwareApp { Vatis }
}
namespace VatscaUpdateChecker.Services
{
    public sealed class ClientSignInOptions
    {
        public Func<Models.SoftwareApp, bool> IsRunning { get; init; } = _ => throw new InvalidOperationException("Unexpected real process probe.");
    }
    public static class ClientSignInService
    {
        public static string ValidateClient(Models.SoftwareApp app, string path, ClientSignInOptions options) =>
            throw new InvalidOperationException("Unexpected real installation probe.");
    }
    public static class MaintenanceLock
    {
        public static bool IsHeldByCurrentProcess => throw new InvalidOperationException("Unexpected real maintenance probe.");
    }
}
