using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

/// <summary>Local presentation of typed setup outcomes; no settings, credentials or telemetry access.</summary>
internal static class ControllerProfileResultPresentation
{
    internal static IReadOnlyList<(string Text, bool NeedsAttention)> ClientLines(ControllerClientSetupResult result)
    {
        var lines = new List<(string, bool)>();
        if (result.Blocked)
            lines.Add(("Client setup: another Launchpad operation is active. Try Save & apply when it finishes.", true));
        if (result.PasswordUnavailable)
            lines.Add(("Saved password could not be read. Existing client passwords were kept.", true));
        foreach (var client in result.SignIn)
        {
            string name = client.App == SoftwareApp.Vatis ? "vATIS" : client.App == SoftwareApp.TrackAudio ? "TrackAudio" : client.App.ToString();
            lines.Add(client.Status switch
            {
                ClientSignInStatus.Updated => (name + ": Sign-in details updated.", false),
                ClientSignInStatus.Unchanged => (name + ": Sign-in details already match.", false),
                _ => (client.Message.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase)
                    ? client.Message : name + ": " + client.Message, true)
            });
        }
        if (result.SwedenProfile is { } sweden)
            lines.Add(sweden.Status switch
            {
                VatisSwedenProfileStatus.Installed => ("vATIS Swedish profile: Added.", false),
                VatisSwedenProfileStatus.AlreadyConfigured => ("vATIS Swedish profile: Already configured.", false),
                _ => ("vATIS Swedish profile: " + sweden.Message, true)
            });
        return lines;
    }
}
