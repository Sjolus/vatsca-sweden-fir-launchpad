using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

public sealed record ControllerClientSetupResult(string Message, IReadOnlyList<ClientSignInOutcome> SignIn,
    VatisSwedenProfileResult? SwedenProfile = null, bool PasswordUnavailable = false, bool Blocked = false)
{
    public bool NeedsAttention => Blocked || PasswordUnavailable ||
        SignIn.Any(result => result.Status is not (ClientSignInStatus.Updated or ClientSignInStatus.Unchanged)) ||
        SwedenProfile is { Success: false };
}

/// <summary>Applies the saved controller during explicit setup; never called by startup or launch.</summary>
public static class ControllerClientSetup
{
    public static string Describe(SoftwareApp app) => app switch
    {
        SoftwareApp.Vatis => "vATIS: uses your saved controller details and adds the Swedish profile if needed.",
        SoftwareApp.TrackAudio => "TrackAudio: uses your saved CID and password. Audio devices and PTT stay as configured.",
        _ => ""
    };

    public static bool HasConfiguredClients(AppSettings settings) =>
        IsIncluded(settings, SoftwareApp.Vatis) || IsIncluded(settings, SoftwareApp.TrackAudio);

    public static bool IsIncluded(AppSettings settings, SoftwareApp app) => app switch
    {
        SoftwareApp.Vatis => !string.IsNullOrWhiteSpace(settings.VatisExePath) && !ApplicationPreferences.IsNotUsed(settings, "vATIS"),
        SoftwareApp.TrackAudio => !string.IsNullOrWhiteSpace(settings.TrackAudioExePath) && !ApplicationPreferences.IsNotUsed(settings, "TrackAudio"),
        _ => false
    };

    public static async Task<ControllerClientSetupResult> ApplySavedAsync(AppSettings settings, SoftwareApp app)
    {
        if (!IsIncluded(settings, app)) return new("", []);
        string password = "", warning = "";
        try { password = CredentialManagerService.LoadForEditing(CredentialManagerService.TargetVatsim) ?? ""; }
        catch { warning = "Saved password could not be read; existing client passwords were kept. Save Controller profile again to retry. "; }
        var result = await ApplyAsync(settings, password, app, passwordUnavailable: warning.Length > 0);
        if (warning.Length == 0) return result;
        ReportingService.RecordError(ReportingErrorCode.SettingsSaveFailed, new ReportingDetails
        { Application = ReportingEvents.ForSoftware(app), Operation = ReportingOperation.ConfigureSignIn, Stage = ReportingStage.Prepare, Reason = ReportingReason.SaveFailed });
        return result with { Message = warning + result.Message, PasswordUnavailable = true };
    }

    public static async Task<ControllerClientSetupResult> ApplyAsync(AppSettings settings, string password, SoftwareApp? onlyApp = null,
        bool passwordUnavailable = false)
    {
        var apps = new[] { SoftwareApp.Vatis, SoftwareApp.TrackAudio }
            .Where(app => (!onlyApp.HasValue || app == onlyApp) && IsIncluded(settings, app)).ToArray();
        if (apps.Length == 0) return new("", []);

        // Owners normally hold the maintenance lease through the profile/install dialog.
        // The legacy setup wizard also uses this entry point without an owner lease.
        IDisposable? lease = null;
        if (!MaintenanceLock.IsHeldByCurrentProcess && !MaintenanceLock.TryAcquire(out lease))
            return new("Application configuration is waiting for another Launchpad operation. Save the controller profile again when it finishes.", [], Blocked: true);
        using (lease)
        {
            var messages = new List<string>();
            IReadOnlyList<ClientSignInOutcome> signIn = [];
            VatisSwedenProfileResult? sweden = null;
            foreach (var app in apps) ReportingEvents.Start(ReportingEvents.ForSoftware(app), ReportingOperation.ConfigureSignIn);
            try
            {
                signIn = (await Task.Run(() => ClientSignInService.Apply(settings, password, onlyApp)))
                    .Where(result => apps.Contains(result.App)).ToArray();
                foreach (var app in apps)
                {
                    var result = signIn.FirstOrDefault(item => item.App == app);
                    var outcome = result?.Status switch
                    {
                        ClientSignInStatus.Updated or ClientSignInStatus.Unchanged => ReportingOutcome.Completed,
                        ClientSignInStatus.Failed or null => ReportingOutcome.Failed, _ => ReportingOutcome.Blocked
                    };
                    var reason = result?.Status switch
                    {
                        ClientSignInStatus.Unchanged => ReportingReason.AlreadyConfigured,
                        ClientSignInStatus.Running => ReportingReason.ClientRunning,
                        ClientSignInStatus.Unsupported => ReportingReason.UnsupportedInstallation,
                        ClientSignInStatus.NotConfigured or ClientSignInStatus.NotUsed => ReportingReason.MissingConfiguration,
                        _ => (ReportingReason?)null
                    };
                    if (passwordUnavailable && outcome == ReportingOutcome.Completed)
                    { outcome = ReportingOutcome.Partial; reason = ReportingReason.SaveFailed; }
                    if (outcome == ReportingOutcome.Failed) ReportingService.RecordError(ReportingErrorCode.SettingsSaveFailed,
                        new ReportingDetails { OperationId = ReportingEvents.CurrentOperationId(ReportingEvents.ForSoftware(app), ReportingOperation.ConfigureSignIn),
                            Application = ReportingEvents.ForSoftware(app), Operation = ReportingOperation.ConfigureSignIn, Stage = ReportingStage.Apply });
                    ReportingEvents.Complete(ReportingEvents.ForSoftware(app), ReportingOperation.ConfigureSignIn, outcome, reason);
                }
                messages.AddRange(signIn.Select(result => result.Message));
            }
            catch (Exception ex)
            {
                messages.Add("Application sign-in details could not be applied. Save the controller profile again to retry.");
                signIn = apps.Select(app => new ClientSignInOutcome(app, ClientSignInStatus.Failed, "Sign-in details could not be applied.")).ToArray();
                foreach (var app in apps) ReportingEvents.Stop(ex, ReportingEvents.ForSoftware(app), ReportingOperation.ConfigureSignIn, ReportingStage.Apply);
            }

            if (apps.Contains(SoftwareApp.Vatis))
            {
                ReportingEvents.Start(ReportingApplication.Vatis, ReportingOperation.ConfigureSwedenProfile);
                try
                {
                    sweden = await new VatisSwedenProfileService().EnsureAsync(settings, CancellationToken.None);
                    messages.Add(sweden.Message);
                    var outcome = sweden.Status switch
                    {
                        VatisSwedenProfileStatus.Installed or VatisSwedenProfileStatus.AlreadyConfigured => ReportingOutcome.Completed,
                        VatisSwedenProfileStatus.Failed => ReportingOutcome.Failed, _ => ReportingOutcome.Blocked
                    };
                    var reason = sweden.Status switch
                    {
                        VatisSwedenProfileStatus.AlreadyConfigured => ReportingReason.AlreadyConfigured,
                        VatisSwedenProfileStatus.NotConfigured => ReportingReason.MissingConfiguration,
                        VatisSwedenProfileStatus.Conflict => ReportingReason.Conflict,
                        _ => (ReportingReason?)null
                    };
                    if (outcome == ReportingOutcome.Failed) ReportingService.RecordError(ReportingErrorCode.OperationStopped,
                        new ReportingDetails { OperationId = ReportingEvents.CurrentOperationId(ReportingApplication.Vatis, ReportingOperation.ConfigureSwedenProfile),
                            Application = ReportingApplication.Vatis, Operation = ReportingOperation.ConfigureSwedenProfile });
                    ReportingEvents.Complete(ReportingApplication.Vatis, ReportingOperation.ConfigureSwedenProfile, outcome, reason);
                }
                catch (Exception ex)
                {
                    messages.Add("The Swedish vATIS profile could not be prepared. Save the controller profile again to retry.");
                    sweden = new(VatisSwedenProfileStatus.Failed, "The Swedish vATIS profile could not be prepared.");
                    ReportingEvents.Stop(ex, ReportingApplication.Vatis, ReportingOperation.ConfigureSwedenProfile);
                }
            }
            return new(string.Join(" ", messages.Where(message => !string.IsNullOrWhiteSpace(message))), signIn, sweden);
        }
    }
}
