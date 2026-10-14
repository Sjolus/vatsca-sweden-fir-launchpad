using System.IO;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker.Services;

// The caller owns the maintenance lease and explicit confirmation. No process is
// started or closed during review; execution uses only the selected review items.
public static class ApplicationBatchActions
{
    public static IReadOnlyList<ApplicationActionChoice> Prepare(ApplicationBatchAction action,
        IEnumerable<ApplicationActionTarget> targets, Func<ApplicationActionTarget, ApplicationActionObservation> observe)
    {
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        var choices = new List<ApplicationActionChoice>();
        foreach (var target in targets.Where(target => !target.IsFolder && !target.IsLaunchpad))
        {
            if (action == ApplicationBatchAction.Start && target.IsNotUsed)
            { choices.Add(new(target, "Skipped · marked as not used.", false)); continue; }
            ApplicationActionObservation state;
            try { state = observe(target); }
            catch (Exception error) { choices.Add(new(target, "Skipped · " + error.Message, false)); continue; }
            if (action == ApplicationBatchAction.Close)
            {
                var windows = state.Windows.DistinctBy(ProcessKey).ToArray();
                choices.Add(new(target, windows.Length == 0 ? "Skipped · no verified application window is open." :
                    target.IsLocalBrowser ? "Close the VatEFS browser window. Its server is left running." :
                    target.IsBrowser ? "Close only this application's browser window." :
                    windows.Length == 1 ? "Ask the application to close normally." : $"Ask {windows.Length} application windows to close normally.",
                    windows.Length > 0, windows));
            }
            else
            {
                string? blocked = state.Running.Count > 0 ? "Already running · left open." : state.StartBlockReason;
                string detail = target.IsEuroScope ? "Start with profile: " + (target.ProfileName ?? "No profile") + "." :
                    target.IsBrowser ? "Open this application's browser window." : "Start the configured application.";
                choices.Add(new(target, blocked ?? detail, blocked is null));
            }
        }
        return choices;
    }

    public static IReadOnlyList<ApplicationActionResult> Execute(ApplicationBatchAction action,
        IEnumerable<ApplicationActionChoice> choices, Func<ApplicationActionTarget, ApplicationActionObservation> observe,
        Func<ApplicationActionTarget, bool> start, Func<ApplicationProcessIdentity, bool> closeNormally)
    {
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        var results = new List<ApplicationActionResult>();
        var acted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var choice in choices.Where(choice => choice.CanSelect && choice.IsSelected))
        {
            var target = choice.Target;
            try
            {
                if (action == ApplicationBatchAction.Start)
                {
                    var state = observe(target);
                    if (state.Running.Count > 0)
                    { results.Add(new(target.Id, target.Name, false, "Already running · left open.")); continue; }
                    if (state.StartBlockReason is { } blocked)
                    { results.Add(new(target.Id, target.Name, false, blocked)); continue; }
                    string key = target.IsBrowser ? "browser:" + target.Id : "file:" + Path.GetFullPath(target.LaunchPath);
                    if (!acted.Add(key))
                    { results.Add(new(target.Id, target.Name, false, "Skipped · another selected row uses this application.")); continue; }
                    bool requested = start(target);
                    results.Add(new(target.Id, target.Name, requested, requested ? "Start requested." : "The application could not be started."));
                }
                else
                {
                    int accepted = 0;
                    // These are the reviewed process lifetimes, never a new lookup
                    // that could include applications opened during confirmation.
                    foreach (var process in choice.Processes)
                        if (acted.Add(ProcessKey(process)) && closeNormally(process)) accepted++;
                    results.Add(new(target.Id, target.Name, accepted > 0, accepted > 0
                        ? "Close requested. Check the application for save or confirmation prompts."
                        : "The reviewed window has already closed or did not accept the close request."));
                }
            }
            catch (Exception error)
            {
                results.Add(new(target.Id, target.Name, false, "Could not " + (action == ApplicationBatchAction.Start ? "start" : "close") + ": " + error.Message));
            }
        }
        return results;
    }

    private static string ProcessKey(ApplicationProcessIdentity process) =>
        $"{process.Id}:{process.StartedUtc.Ticks}:{process.ExecutablePath.ToUpperInvariant()}";
}
