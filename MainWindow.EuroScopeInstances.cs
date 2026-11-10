using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    // A single-profile launch is tracked under this fixed entry so Show/Close can still name it.
    private static readonly Guid SingleLaunchEntryId = new("5f0c6d1e-6a63-4c8e-9a51-3d4c2f1b7e01");

    // Lazy: static initializers in other partial files (AppDataDir) have no guaranteed order.
    private static EuroScopeInstanceTracker? _euroScopeInstances;
    private static EuroScopeInstanceTracker EuroScopeInstances =>
        _euroScopeInstances ??= new(Path.Combine(AppDataDir, "EuroScope.instances.json"));

    private CancellationTokenSource? _launchSequenceCts;
    private bool _launchSequenceBusy;

    private CheckResult EuroScopeRow => _results[0];

    private IReadOnlyList<EuroScopeLaunchSetDefinition> LaunchSets => _settings.EuroScopeLaunchSets ?? [];

    private EuroScopeLaunchSetDefinition? ActiveLaunchSet =>
        LaunchSets.FirstOrDefault(set => set.Id == _settings.ActiveEuroScopeLaunchSetId);

    private static IReadOnlyList<EuroScopeLaunchEntry> EnabledEntries(EuroScopeLaunchSetDefinition? set) =>
        set is null ? [] : EuroScopeLaunchPlanner.Normalize(set.Entries).Where(entry => entry.Enabled).ToArray();

    private IReadOnlyList<EuroScopeLaunchEntry> EnabledLaunchEntries => EnabledEntries(ActiveLaunchSet);

    private bool LaunchSetActive => EnabledLaunchEntries.Count > 0;

    private void RefreshEuroScopeLaunchSetPresentation()
    {
        var row = EuroScopeRow;
        row.LaunchSetName = ActiveLaunchSet?.Name ?? string.Empty;
        row.LaunchSetCount = EnabledLaunchEntries.Count;
        row.LaunchSetActive = LaunchSetActive;
    }

    private void UpdateEuroScopeInstanceSummary()
    {
        var row = EuroScopeRow;
        if (!row.IsRunning) { row.RunningInstanceSummary = string.Empty; return; }

        var live = EuroScopeInstances.Live();
        if (LaunchSetActive)
        {
            var enabled = EnabledLaunchEntries;
            int running = enabled.Count(entry => live.Any(record => record.EntryId == entry.Id));
            row.RunningInstanceSummary = $"{running} of {enabled.Count} running";
            return;
        }

        int all = ApplicationProcessControl.Find(row.LaunchPath).Count;
        row.RunningInstanceSummary = all > 1 ? $"{all} instances running" : string.Empty;
    }

    private static ApplicationActionTarget LaunchEntryTarget(ApplicationActionTarget euroScope, EuroScopeLaunchEntry entry) =>
        euroScope with
        {
            ProfilePath = string.IsNullOrWhiteSpace(entry.ProfilePath) ? null : entry.ProfilePath,
            ProfileName = EuroScopeLaunchPlanner.ProfileName(entry.ProfilePath)
        };

    /// <summary>
    /// Starts each enabled launch-set entry in order. Each later entry waits for the previously started
    /// instance's main window, then its optional extra delay. Holds the maintenance lease throughout.
    /// </summary>
    private async Task LaunchEuroScopeSetAsync(CheckResult row)
    {
        if (_launchSequenceBusy || !CanRunSoftwareAction() || !TryAcquireMaintenanceGuard(out var lease)) return;
        using var guard = lease;

        var baseTarget = ApplicationTarget(row);
        IReadOnlyList<EuroScopeLaunchStep> steps;
        try
        {
            steps = EuroScopeLaunchPlanner.Build(ActiveLaunchSet?.Entries ?? [], EuroScopeInstances.Live(),
                entry => StartBlockReason(LaunchEntryTarget(baseTarget, entry)) is { } reason
                    ? reason.Replace("Skipped · ", "") : null);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, "The launch set could not be prepared:\n\n" + error.Message, "EuroScope launch set",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var setName = ActiveLaunchSet?.Name ?? "Launch set";
        var starts = steps.Where(step => step.Kind == EuroScopeLaunchStepKind.Start).ToArray();
        if (starts.Length == 0)
        {
            row.StatusMessage = $"No instance in {setName} can start. " +
                string.Join(" ", steps.Select(step => $"{step.Order}. {step.ProfileName}: {step.Message}"));
            FocusApplicationRow(row);
            return;
        }

        var notes = steps.Where(step => step.Kind != EuroScopeLaunchStepKind.Start || step.Message != null).ToArray();
        if (notes.Length > 0)
        {
            var lines = steps.Select(step => $"{step.Order}. {step.ProfileName} — " + (step.Kind == EuroScopeLaunchStepKind.Start
                ? (step == starts[0] ? "start" : "start when the previous window opens")
                    + (step.DelaySeconds > 0 ? $", then wait {step.DelaySeconds} s" : "") + (step.Message is { } note ? " (" + note + ")" : "")
                : step.Message));
            if (MessageBox.Show(this, $"Start {starts.Length} EuroScope instance{(starts.Length == 1 ? "" : "s")} from {setName}?\n\n" + string.Join("\n", lines),
                    setName, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes) return;
        }

        _launchSequenceBusy = true;
        _launchSequenceCts = CancellationTokenSource.CreateLinkedTokenSource(_windowLifetime.Token);
        var token = _launchSequenceCts.Token;
        row.LaunchSequenceCancellable = true;
        RefreshBulkActionAvailability();

        int started = 0;
        bool cancelled = false;
        var problems = new List<string>();
        EuroScopeLaunchStep? previous = null;

        try
        {
            for (int i = 0; i < starts.Length; i++)
            {
                var step = starts[i];
                if (previous is { } waitFor)
                {
                    var failure = await WaitForEuroScopeWindowAsync(row, waitFor, $"{i + 1} of {starts.Length} ({step.ProfileName})", token);
                    if (failure != null)
                    {
                        problems.Add(failure);
                        break;
                    }
                }

                for (int remaining = step.DelaySeconds; remaining > 0; remaining--)
                {
                    row.LaunchSequenceStatus = $"Starting {i + 1} of {starts.Length} ({step.ProfileName}) in {remaining} s";
                    await Task.Delay(1000, token);
                }

                token.ThrowIfCancellationRequested();
                if (_setupRestartRequired)
                {
                    problems.Add("Stopped · Windows restart required before starting more applications.");
                    break;
                }

                row.LaunchSequenceStatus = $"Starting {i + 1} of {starts.Length}: {step.ProfileName}";
                if (EuroScopeInstances.Live().Any(record => record.EntryId == step.Entry.Id))
                {
                    problems.Add($"{step.ProfileName}: already running · left open.");
                    continue;
                }

                try
                {
                    if (StartApplicationCore(LaunchEntryTarget(baseTarget, step.Entry), step.Entry.Id, step.Order))
                    {
                        started++;
                        previous = step;
                    }
                    else problems.Add($"{step.ProfileName}: EuroScope did not accept the start request.");
                }
                catch (Exception error) { problems.Add($"{step.ProfileName}: {error.Message}"); }

                UpdateRunningStates();
            }
        }
        catch (OperationCanceledException) { cancelled = true; }
        finally
        {
            _launchSequenceBusy = false;
            _launchSequenceCts.Dispose();
            _launchSequenceCts = null;
            row.LaunchSequenceCancellable = false;
            row.LaunchSequenceStatus = string.Empty;
        }

        if (_windowClosed) return;

        row.StatusMessage = $"{setName}: started {started} of {starts.Length} EuroScope instance{(starts.Length == 1 ? "" : "s")}." +
            (cancelled ? " The remaining starts were cancelled; started instances stay open." : "") +
            (problems.Count > 0 ? " " + string.Join(" ", problems) : "");
        Logger.Log("LAUNCH", $"EuroScope launch set: {started}/{starts.Length} started{(cancelled ? ", cancelled" : "")}");
        UpdateRunningStates();
        FocusApplicationRow(row);
    }

    private static readonly TimeSpan EuroScopeWindowTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Waits until the instance this sequence just started shows its main window, so the next
    /// instance does not start while the previous one is still initializing.
    /// </summary>
    /// <returns>Null when the window appeared; otherwise the reason the remaining starts stop.</returns>
    private async Task<string?> WaitForEuroScopeWindowAsync(CheckResult row, EuroScopeLaunchStep previous, string next, CancellationToken token)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var record = EuroScopeInstances.Live().FirstOrDefault(item => item.EntryId == previous.Entry.Id);
            if (record is null)
                return $"Stopped · {previous.ProfileName} closed before its window appeared; the remaining instances were not started.";
            if (ApplicationProcessControl.HasWindow(record.Process)) return null;
            if (watch.Elapsed >= EuroScopeWindowTimeout)
                return $"Stopped · {previous.ProfileName} showed no window within {EuroScopeWindowTimeout.TotalSeconds:0} s; " +
                    "the remaining instances were not started. Use Start missing instances once it is open.";

            row.LaunchSequenceStatus = $"Starting {next} after {previous.ProfileName} opens…";
            await Task.Delay(500, token);
        }
    }

    private void CancelLaunchSequence() => _launchSequenceCts?.Cancel();

    private sealed record EuroScopeWindow(string Label, int Order, ApplicationProcessIdentity Process);

    private List<EuroScopeWindow> EuroScopeWindows(CheckResult row)
    {
        var live = EuroScopeInstances.Live();
        var windows = live.Where(record => ApplicationProcessControl.HasWindow(record.Process))
            .Select(record => new EuroScopeWindow(record.ProfileName, record.Order, record.Process)).ToList();

        foreach (var other in EuroScopeInstanceTracker.Untracked(ApplicationProcessControl.Find(row.LaunchPath), live)
                     .Where(ApplicationProcessControl.HasWindow))
            windows.Add(new("Other EuroScope window", int.MaxValue, other));

        return windows.OrderBy(window => window.Order).ToList();
    }

    private IReadOnlyList<EuroScopeLaunchEntry> MissingLaunchEntries()
    {
        if (!LaunchSetActive) return [];
        var live = EuroScopeInstances.Live();
        return EnabledLaunchEntries.Where(entry => !live.Any(record => record.EntryId == entry.Id)).ToArray();
    }

    /// <returns>True when the EuroScope-specific menu handled the request.</returns>
    private bool ShowEuroScopeInstanceMenu(CheckResult row, FrameworkElement anchor)
    {
        if (!row.HasEuroScopeManagement || !CanControlRunningApplication()) return false;
        var windows = EuroScopeWindows(row);
        var missing = MissingLaunchEntries();
        if (windows.Count <= 1 && missing.Count == 0) return false;

        var menu = new ContextMenu();
        foreach (var window in windows)
        {
            var item = new MenuItem { Header = "Show · " + window.Label };
            var process = window.Process;
            item.Click += (_, _) =>
            {
                if (!ApplicationProcessControl.TryAct(process, RunningApplicationAction.Show))
                {
                    row.StatusMessage = "That EuroScope window could not be brought forward. Use the Windows taskbar or Alt+Tab.";
                    FocusApplicationRow(row);
                }
            };
            menu.Items.Add(item);
        }

        if (missing.Count > 0)
        {
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            var start = new MenuItem
            {
                Header = $"Start missing instances ({missing.Count})…",
                IsEnabled = CanRunSoftwareAction()
            };
            start.Click += async (_, _) => await LaunchEuroScopeSetAsync(row);
            menu.Items.Add(start);
        }

        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        return true;
    }

    /// <returns>True when the per-instance close review handled the request.</returns>
    private bool ReviewEuroScopeClose(CheckResult row)
    {
        if (!row.HasEuroScopeManagement) return false;
        if (!CanControlRunningApplication() || !TryAcquireMaintenanceGuard(out var lease)) return true;
        using var guard = lease;

        var windows = EuroScopeWindows(row);
        if (windows.Count <= 1) return false;

        var baseTarget = ApplicationTarget(row);
        // Newest first, so dependent later instances close before the first one.
        var choices = windows.OrderByDescending(window => window.Order == int.MaxValue ? -1 : window.Order)
            .Select(window => new ApplicationActionChoice(baseTarget with { Name = "EuroScope · " + window.Label },
                "Ask this instance to close normally. It may ask you to save or confirm.", true, [window.Process])).ToArray();

        var review = new BulkApplicationActionWindow(ApplicationBatchAction.Close, choices) { Owner = this };
        if (ShowOwnedDialog(review) != true) return true;

        int requested = 0, selected = 0;
        foreach (var choice in review.Choices.Where(choice => choice.CanSelect && choice.IsSelected))
        {
            selected++;
            if (choice.Processes.Any(process => ApplicationProcessControl.TryAct(process, RunningApplicationAction.CloseNormally))) requested++;
        }

        row.StatusMessage = requested == 0 ? "No instance accepted the close request. Use EuroScope's own window."
            : $"Close requested for {requested} of {selected} EuroScope instance{(selected == 1 ? "" : "s")}. Check for save or confirmation prompts.";
        FocusApplicationRow(row);
        UpdateRunningStates();
        return true;
    }

    private void EditLaunchSet(CheckResult row)
    {
        if (!CanRunSoftwareAction() || !TryAcquireMaintenanceGuard(out var lease)) return;
        using var guard = lease;

        var editor = new EuroScopeLaunchSetWindow(row.Profiles.ToArray(), LaunchSets, _settings.ActiveEuroScopeLaunchSetId, row.SelectedProfile);
        if (ShowOwnedDialog(editor) != true) return;

        _settings.EuroScopeLaunchSets = [.. editor.Sets];
        _settings.ActiveEuroScopeLaunchSetId = editor.ActiveSetId;
        RefreshEuroScopeLaunchSetPresentation();
        SaveSessionSettings("EuroScope launch sets saved.");
        UpdateRunningStates();
    }
}
