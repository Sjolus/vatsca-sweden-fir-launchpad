using System.Security.Cryptography;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

// Only the production orchestration and data models are linked. Every service, dialog,
// timer and window below is inert; the only files accessed belong to a synthetic fixture.
namespace SaamanSetupFlow.Tests
{
    internal sealed record Completion(ReportingOperation Operation, ReportingOutcome Outcome,
        ReportingReason? Reason, ReportingDetails? Details);
    internal sealed record Status(string Message, bool Warning, bool Busy, bool CanCancel, bool Cancelled);

    internal sealed class Fixture : IDisposable
    {
        internal static Fixture Current { get; private set; } = null!;
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "launchpad-saaman-flow-" + Guid.NewGuid().ToString("N"));
        internal string Profile => Path.Combine(Root, SaamanProfileService.DefaultProfileName);
        internal string Dll => Path.Combine(Root, "Plugins", "SAAMAN.dll");
        internal readonly List<string> Calls = [];
        internal readonly List<ReportingOperation> Starts = [];
        internal readonly List<Completion> Completions = [];
        internal readonly List<ActivityEntry> Activities = [];
        internal readonly List<AppSettings> Saved = [];
        internal readonly List<Status> Statuses = [];
        internal readonly List<SaamanProfileChange> Applied = [];
        internal readonly Queue<bool> SaveResults = [];
        internal bool RuntimeAvailable = true, Running, PendingGng, LeaseAvailable = true, LeaseHeld;
        internal bool ForbidNetwork, ForbidRuntime, ForbidAdopt, FailProfile, BadPlanHash, ChangedSecondIdentity;
        internal bool RuntimeRestart, ProfileChanges = true;
        internal string? SelectedDll;
        internal string InstalledVersion = "1.2.3";
        internal Action? OnLatest, OnDownload, AfterInstall, BeforeProfile;
        internal int AdoptCount, FailureCount, SaveErrorCount;
        internal VatscaUpdateChecker.MainWindow Window { get; }
        internal SaamanRelease Release { get; }
        internal static readonly byte[] PackageBytes = [0x53, 0x41, 0x41, 0x4d, 0x41, 0x4e];

        internal Fixture(bool installed = false, bool enabled = false)
        {
            Current = this;
            Directory.CreateDirectory(Root);
            File.WriteAllText(Profile, "original profile");
            if (installed) WriteDll(Dll);
            Release = new("1.2.3", "v1.2.3", [new("SAAMAN.dll", new("https://example.invalid/SAAMAN.dll"),
                PackageBytes.Length, Convert.ToHexString(SHA256.HashData(PackageBytes)))]);
            Window = new(new AppSettings { EuroscopeDataPath = Root, SaamanProfilePath = Profile,
                SaamanDllPath = installed ? Dll : "", PatchSaaman = enabled, SaamanProfileManaged = enabled,
                TrackAudioExePath = "synthetic-other-client", VatsimName = "Synthetic Controller", TextScale = 1.25 });
        }
        internal string WriteDll(string path)
        {
            RequireOwned(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, PackageBytes);
            return path;
        }
        internal void RequireOwned(string path)
        {
            if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The fixture refused a path outside its temporary directory.");
        }
        internal SaamanDllInfo Identity(string path)
        {
            RequireOwned(path);
            if (!File.Exists(path)) throw new FileNotFoundException("Synthetic DLL is missing.");
            var bytes = File.ReadAllBytes(path);
            return new(path, InstalledVersion, bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)));
        }
        internal Completion End(ReportingOperation operation) => Completions.Last(item => item.Operation == operation);
        internal int Count(string call) => Calls.Count(value => value == call);
        public void Dispose()
        {
            if (LeaseHeld) throw new InvalidOperationException("A maintenance lease escaped the operation.");
            string full = Path.GetFullPath(Root);
            string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("launchpad-saaman-flow-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe fixture cleanup path.");
            Directory.Delete(full, recursive: true);
        }
    }
}

namespace Microsoft.Win32
{
    public sealed class OpenFileDialog
    {
        public string Title { get; set; } = "";
        public string Filter { get; set; } = "";
        public bool CheckFileExists { get; set; }
        public string FileName { get; private set; } = "";
        public bool? ShowDialog(VatscaUpdateChecker.MainWindow owner)
        {
            var f = SaamanSetupFlow.Tests.Fixture.Current;
            f.Calls.Add("picker");
            FileName = f.SelectedDll ?? "";
            return f.SelectedDll is not null;
        }
    }
}

namespace VatscaUpdateChecker
{
    public partial class MainWindow
    {
        private readonly AppSettings _settings;
        private readonly SaamanReleaseService _saamanReleases = new();
        private readonly FakeTimer _processTimer = new();
        private bool _softwareActionBusy, _setupRestartRequired;
        private bool _windowClosed = false;
        private SaamanRelease? _lastSaamanRelease;
        private bool _saamanReleaseChecked;
        private Exception? _saamanReleaseFailure;
        internal MainWindow(AppSettings settings) => _settings = settings;
        internal AppSettings Settings => _settings;
        internal bool NeedsSave => SaamanNeedsSaveRetry;
        internal bool NeedsRuntime => SaamanNeedsRuntime;
        internal bool Busy => _softwareActionBusy;
        internal bool CanCancel => SaamanCanCancel;
        internal bool RestartRequired => _setupRestartRequired;
        internal bool ReleaseChecked => _saamanReleaseChecked && _lastSaamanRelease is not null && _saamanReleaseFailure is null;
        internal Task Enable(bool enabled) => SetSaamanEnabledAsync(enabled);
        internal Task Update() => UpdateSaamanAsync();
        internal Task Choose() => ChooseSaamanDllAsync();
        internal Task RetrySave() => RetrySaamanSaveAsync();
        internal Task InstallRuntime() => InstallSaamanRuntimeAsync();
        internal void Cancel() => CancelSaamanOperation();
        private bool CanRunSoftwareAction() => !_softwareActionBusy;
        private bool CanRunSelfUpdateAction() => true;
        private void BeginSaamanFeedback(ReportingOperation operation) { }
        private void SetPluginStatus(string key, string message, bool warning = false, double? percent = null, bool busy = false, bool cancelled = false)
            => SaamanSetupFlow.Tests.Fixture.Current.Statuses.Add(new(message, warning, busy, busy && SaamanCanCancel, cancelled));
        private void UpdateSelfUpdateActionEnabled() { }
        private Task RefreshSaamanLocalAsync() { SaamanSetupFlow.Tests.Fixture.Current.Calls.Add("refresh"); return Task.CompletedTask; }
        private void ApplyApplicationUsage() { }
        private void RefreshWorkspace() { }
        private sealed class FakeTimer
        {
            public void Stop() => SaamanSetupFlow.Tests.Fixture.Current.Calls.Add("timer-stop");
            public void Start() => SaamanSetupFlow.Tests.Fixture.Current.Calls.Add("timer-start");
        }
    }
}

namespace VatscaUpdateChecker.Services
{
    using SaamanSetupFlow.Tests;
    public sealed class SaamanReleaseService
    {
        public Task<SaamanRelease?> GetLatestAsync(CancellationToken token)
        {
            var f = Fixture.Current;
            f.Calls.Add("release");
            if (f.ForbidNetwork) throw new InvalidOperationException("Unexpected release request.");
            f.OnLatest?.Invoke();
            return Task.FromResult<SaamanRelease?>(f.Release);
        }
    }
    public sealed class SaamanInstallService
    {
        public string? LastRecoveryFolder { get; private set; }
        public static string GetSuggestedPath(AppSettings settings) => string.IsNullOrEmpty(settings.SaamanDllPath)
            ? Fixture.Current.Dll : settings.SaamanDllPath;
        public SaamanDllInfo Adopt(string path, AppSettings settings)
        {
            var f = Fixture.Current; f.Calls.Add("adopt"); f.AdoptCount++;
            if (f.ForbidAdopt) throw new InvalidOperationException("Unexpected DLL adoption.");
            if (!MaintenanceLock.TryAcquire(out var lease)) throw new InvalidOperationException("Synthetic operation is busy.");
            using (lease)
            {
                EuroScopeProcessGuard.RequireClosed();
                var identity = f.Identity(path);
                return f.ChangedSecondIdentity && f.AdoptCount > 1 ? identity with { Sha256 = "changed" } : identity;
            }
        }
        public SaamanInstallPlan Preview(AppSettings settings, SaamanRelease release)
        {
            var f = Fixture.Current; f.Calls.Add("preview");
            string path = GetSuggestedPath(settings);
            f.RequireOwned(path);
            return new(path, release, File.Exists(path) ? f.Identity(path) : null, []);
        }
        public Task<SaamanInstallResult> InstallAsync(SaamanInstallPlan plan, AppSettings settings,
            IProgress<SaamanProgress> progress, CancellationToken token)
        {
            var f = Fixture.Current; f.Calls.Add("install");
            if (f.ForbidNetwork) throw new InvalidOperationException("Unexpected installation request.");
            if (!MaintenanceLock.TryAcquire(out var lease)) throw new InvalidOperationException("Synthetic operation is busy.");
            using (lease)
            {
                EuroScopeProcessGuard.RequireClosed();
                f.OnDownload?.Invoke();
                token.ThrowIfCancellationRequested();
                f.WriteDll(plan.TargetPath);
                f.Calls.Add("installed");
                LastRecoveryFolder = Path.Combine(f.Root, "install-recovery");
                f.AfterInstall?.Invoke();
                return Task.FromResult(new SaamanInstallResult(plan.TargetPath, plan.Release.Version,
                    Path.Combine(LastRecoveryFolder, "receipt.json")));
            }
        }
    }
    public sealed record SaamanProfileChange(string DataFolder, SaamanProfilePolicy Policy, bool HasChanges)
    {
        public string DllHash { get; init; } = "";
        public bool ProfileMissing { get; init; }
    }
    public static class SaamanProfileService
    {
        public const string DefaultProfileName = "ESAA APP ACC.prf";
        public static SaamanProfilePolicy ValidatePolicy(SaamanProfilePolicy policy, string dataFolder)
        { Fixture.Current.RequireOwned(policy.ProfilePath); return policy; }
        public static SaamanProfilePolicy? Capture(AppSettings settings, string dataFolder) => new(
            string.IsNullOrEmpty(settings.SaamanProfilePath) ? Path.Combine(dataFolder, DefaultProfileName) : settings.SaamanProfilePath,
            settings.SaamanDllPath, settings.PatchSaaman);
        public static SaamanProfileChange BuildPlan(SaamanProfilePolicy policy, string dataFolder)
        {
            var f = Fixture.Current; f.Calls.Add("plan-profile");
            return new(dataFolder, ValidatePolicy(policy, dataFolder), f.ProfileChanges)
            {
                ProfileMissing = !File.Exists(policy.ProfilePath),
                DllHash = f.BadPlanHash ? "changed" : policy.Enabled ? f.Identity(policy.DllPath).Sha256 : ""
            };
        }
        public static SaamanProfileChange BuildRelocationPlan(SaamanProfilePolicy policy, string dllPath, string dataFolder)
        { Fixture.Current.Calls.Add("plan-relocate"); return BuildPlan(policy with { DllPath = dllPath }, dataFolder); }
        public static string? Apply(SaamanProfileChange plan)
        {
            var f = Fixture.Current; f.Calls.Add("apply-profile");
            if (!f.LeaseHeld) throw new InvalidOperationException("Profile apply requires its caller's lease.");
            f.BeforeProfile?.Invoke();
            if (f.FailProfile) throw new IOException("Synthetic profile write failed.");
            f.Applied.Add(plan);
            if (!plan.HasChanges || plan.ProfileMissing) return null;
            File.WriteAllText(plan.Policy.ProfilePath, plan.Policy.Enabled ? "SAAMAN:" + plan.Policy.DllPath : "MAESTRO restored");
            return Path.Combine(f.Root, "profile-recovery");
        }
    }
    public static class MaintenanceLock
    {
        public static bool TryAcquire(out IDisposable? lease)
        {
            var f = Fixture.Current; f.Calls.Add("lease");
            if (!f.LeaseAvailable || f.LeaseHeld) { lease = null; return false; }
            f.LeaseHeld = true; lease = new Lease(f); return true;
        }
        private sealed class Lease(Fixture fixture) : IDisposable { public void Dispose() => fixture.LeaseHeld = false; }
    }
    public static class EuroScopeProcessGuard
    {
        public static void RequireClosed()
        {
            var f = Fixture.Current; f.Calls.Add("guard");
            if (f.Running) throw new InvalidOperationException("Synthetic EuroScope is running.");
        }
    }
    public static class GngUpdateService
    {
        public static void RequireNoPendingUpdate(string path)
        {
            var f = Fixture.Current; f.Calls.Add("gng-guard");
            if (f.PendingGng) throw new InvalidOperationException("Synthetic GNG operation is pending.");
        }
    }
    public static class WebViewRuntimeService
    {
        public static Version? GetInstalledVersion()
        {
            var f = Fixture.Current; f.Calls.Add("runtime-check");
            if (f.ForbidRuntime) throw new InvalidOperationException("Unexpected runtime query.");
            return f.RuntimeAvailable ? new Version(1, 0) : null;
        }
        public static Task InstallAsync(IProgress<string> progress, CancellationToken token, Action onRestartRequired)
        {
            var f = Fixture.Current; f.Calls.Add("runtime-install");
            if (!f.LeaseHeld) throw new InvalidOperationException("Runtime install requires its caller's lease.");
            token.ThrowIfCancellationRequested();
            f.RuntimeAvailable = true;
            if (f.RuntimeRestart) onRestartRequired();
            return Task.CompletedTask;
        }
    }
    public static class SettingsService
    {
        public static bool TrySave(AppSettings settings)
        {
            var f = Fixture.Current; f.Calls.Add("save"); f.Saved.Add(settings.Copy());
            return f.SaveResults.Count == 0 || f.SaveResults.Dequeue();
        }
    }
    public static class ReportingEvents
    {
        public static void Start(ReportingApplication app, ReportingOperation operation) => Fixture.Current.Starts.Add(operation);
        public static void Complete(ReportingApplication app, ReportingOperation operation, ReportingOutcome outcome,
            ReportingReason? reason = null, ReportingDetails? details = null) => Fixture.Current.Completions.Add(new(operation, outcome, reason, details));
        public static void RecordFailure(Exception exception, ReportingApplication app, ReportingOperation operation, ReportingStage stage)
            => Fixture.Current.FailureCount++;
        public static ReportingReason FailureReason(Exception exception) => ReportingReason.Unexpected;
        public static void Stop(Exception exception, ReportingApplication app, ReportingOperation operation, ReportingStage stage)
        { RecordFailure(exception, app, operation, stage); Complete(app, operation, ReportingOutcome.Failed, FailureReason(exception)); }
        public static string? PublicVersion(string? version) => version;
    }
    public static class ReportingService
    {
        public static void RecordError(ReportingErrorCode code, ReportingDetails details) => Fixture.Current.SaveErrorCount++;
    }
    public static class ActivityHistoryService
    {
        public static void Record(ActivityApplication app, ActivityOperation operation, ActivityOutcome outcome,
            string? version = null, string? recoveryFolder = null, string? dataFolder = null, bool restartRequired = false,
            string? previousVersion = null) => Fixture.Current.Activities.Add(new(Guid.NewGuid(), DateTimeOffset.UtcNow,
                app, operation, outcome, version, recoveryFolder, dataFolder, restartRequired, previousVersion));
    }
}
