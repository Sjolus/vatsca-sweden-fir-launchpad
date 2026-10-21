using VatscaUpdateChecker;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

var settings = new AppSettings { VatisExePath = "synthetic-vatis", TrackAudioExePath = "synthetic-trackaudio", VatsimCid = "1234567" };
int checks = 0, identityCalls = 0, profileCalls = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    checks++; Console.WriteLine("PASS " + description);
}
void Reset()
{
    identityCalls = profileCalls = 0; ReportingEvents.Starts.Clear(); ReportingEvents.Finishes.Clear(); ReportingService.Errors.Clear();
    MaintenanceLock.Available = true; MaintenanceLock.IsHeldByCurrentProcess = false;
    MaintenanceLock.Acquired = MaintenanceLock.Disposed = 0;
    ClientSignInService.Run = (_, password, app) =>
    {
        identityCalls++;
        Check(MaintenanceLock.IsHeldByCurrentProcess, "identity configuration holds the maintenance lease");
        Check(password == "synthetic-password", "saved controller password reaches only the adapter");
        return [new(SoftwareApp.Vatis, ClientSignInStatus.Updated, "vATIS updated."), new(SoftwareApp.TrackAudio, ClientSignInStatus.Updated, "TrackAudio updated.")];
    };
    VatisSwedenProfileService.Run = () =>
    {
        profileCalls++;
        Check(MaintenanceLock.IsHeldByCurrentProcess, "profile preparation holds the maintenance lease");
        return Task.FromResult(new VatisSwedenProfileResult(VatisSwedenProfileStatus.Installed, "Swedish profile ready."));
    };
    CredentialManagerService.Read = () => "synthetic-password";
}

Reset();
var result = await ControllerClientSetup.ApplyAsync(new AppSettings(), "synthetic-password");
Check(result.Message == "" && identityCalls == 0 && profileCalls == 0 && MaintenanceLock.Acquired == 0,
    "no configured clients means no credential/file operations or lock");
Check(ReportingEvents.Starts.Count == 0 && ReportingEvents.Finishes.Count == 0, "unconfigured clients produce no phantom attempts");

Reset();
result = await ControllerClientSetup.ApplySavedAsync(settings, SoftwareApp.TrackAudio);
Check(identityCalls == 1 && profileCalls == 0 && !result.Message.Contains("vATIS") && result.Message.Contains("TrackAudio"),
    "TrackAudio fresh setup does not prepare vATIS or report its outcome");
Check(MaintenanceLock.Acquired == 1 && MaintenanceLock.Disposed == 1, "standalone setup releases its lease");
Check(ReportingEvents.Starts.Count == 1 && ReportingEvents.Finishes.Count == 1 &&
    ReportingEvents.Finishes[0].App == ReportingApplication.Trackaudio, "single-client setup reports only the selected client");

Reset();
MaintenanceLock.IsHeldByCurrentProcess = true;
result = await ControllerClientSetup.ApplyAsync(settings, "synthetic-password");
Check(identityCalls == 1 && profileCalls == 1 && MaintenanceLock.Acquired == 0 && MaintenanceLock.Disposed == 0,
    "owner-held lease survives setup without nested acquisition or disposal");

Reset();
var excluded = settings.Copy();
ApplicationPreferences.SetNotUsed(excluded, "vATIS", true);
result = await ControllerClientSetup.ApplyAsync(excluded, "synthetic-password");
Check(profileCalls == 0 && !result.Message.Contains("vATIS"), "not-used vATIS never downloads a profile");

Reset();
MaintenanceLock.Available = false;
result = await ControllerClientSetup.ApplyAsync(settings, "synthetic-password");
Check(identityCalls == 0 && profileCalls == 0 && result.Message.Contains("another Launchpad operation"), "busy maintenance prevents all setup work");

Reset();
VatisSwedenProfileService.Run = () => throw new Exception("synthetic-password private client contents");
result = await ControllerClientSetup.ApplyAsync(settings, "synthetic-password");
Check(result.Message.Contains("vATIS updated") && result.Message.Contains("could not be prepared") && !result.Message.Contains("synthetic-password"),
    "profile failure retains successful sign-in results without exposing exception contents");
Check(MaintenanceLock.Disposed == 1, "failure releases a coordinator-owned lease");

Reset();
ClientSignInService.Run = (_, _, _) => throw new Exception("synthetic-password private client contents");
result = await ControllerClientSetup.ApplyAsync(settings, "synthetic-password");
Check(profileCalls == 1 && result.Message.Contains("Swedish profile ready") && !result.Message.Contains("synthetic-password"),
    "sign-in failure does not block independent profile setup or disclose secrets");

Reset();
CredentialManagerService.Read = () => throw new System.ComponentModel.Win32Exception(5, "synthetic-password");
ClientSignInService.Run = (_, password, _) =>
{
    identityCalls++;
    Check(password == "", "unreadable stored password is left out of the identity patch");
    return [new(SoftwareApp.Vatis, ClientSignInStatus.Updated, "vATIS identity updated.")];
};
result = await ControllerClientSetup.ApplySavedAsync(settings, SoftwareApp.Vatis);
Check(identityCalls == 1 && profileCalls == 1 && !result.Message.Contains("synthetic-password") && result.Message.Contains("could not be read"),
    "credential-store failure preserves passwords and still allows Swedish profile setup");
Check(result.PasswordUnavailable && result.NeedsAttention && ReportingEvents.Finishes.Any(item =>
    item.Operation == ReportingOperation.ConfigureSignIn && item.Outcome == ReportingOutcome.Partial && item.Reason == ReportingReason.SaveFailed),
    "unreadable credentials produce a partial sign-in outcome rather than claiming full success");

Reset();
ClientSignInService.Run = (_, _, _) => [new(SoftwareApp.Vatis, ClientSignInStatus.Running, "Close vATIS."), new(SoftwareApp.TrackAudio, ClientSignInStatus.Unchanged, "Already configured.")];
VatisSwedenProfileService.Run = () => Task.FromResult(new VatisSwedenProfileResult(VatisSwedenProfileStatus.AlreadyConfigured, "Already configured."));
result = await ControllerClientSetup.ApplyAsync(settings, "synthetic-password");
Check(result.NeedsAttention && ReportingService.Errors.Count == 0 && ReportingEvents.Finishes.Any(item => item.Reason == ReportingReason.ClientRunning) &&
    ReportingEvents.Finishes.Count(item => item.Reason == ReportingReason.AlreadyConfigured) == 2,
    "running and already-configured clients retain honest outcomes without automatic error reports");
Check(ReportingEvents.Starts.Count == ReportingEvents.Finishes.Count, "every attempted client/profile operation has one terminal outcome");

var presentation = ControllerProfileResultPresentation.ClientLines(new("Unused combined summary",
    [new(SoftwareApp.Vatis, ClientSignInStatus.Unchanged, "Verbose success text."),
     new(SoftwareApp.TrackAudio, ClientSignInStatus.Unsupported, "TrackAudio: A different version was found. Its settings were kept.")],
    new(VatisSwedenProfileStatus.AlreadyConfigured, "Verbose package text.")));
Check(presentation.Count == 3 && presentation.Count(line => line.NeedsAttention) == 1 &&
    presentation[1].Text == "TrackAudio: A different version was found. Its settings were kept.",
    "partial result isolates the unsupported client and retains its specific explanation");
Check(presentation[0].Text == "vATIS: Sign-in details already match." &&
    presentation[2].Text == "vATIS Swedish profile: Already configured." &&
    !presentation.Any(line => line.Text.Contains("Verbose")),
    "successful client and profile steps stay separate and concise");
presentation = ControllerProfileResultPresentation.ClientLines(new("", [], Blocked: true));
Check(presentation.Count == 1 && presentation[0].NeedsAttention && presentation[0].Text.Contains("another Launchpad operation"),
    "blocked setup remains visible even when no per-client outcomes exist");
presentation = ControllerProfileResultPresentation.ClientLines(new("", [],
    new(VatisSwedenProfileStatus.Conflict, "Keep the existing custom profile."), PasswordUnavailable: true));
Check(presentation.Count == 2 && presentation.All(line => line.NeedsAttention) &&
    presentation[0].Text.Contains("password") && presentation[1].Text.Contains("Keep the existing custom profile"),
    "password and profile failures are preserved as independent warnings");

Console.WriteLine($"{checks} setup-flow checks passed. Inert adapters only; no settings, credentials, files, clients or network accessed.");
