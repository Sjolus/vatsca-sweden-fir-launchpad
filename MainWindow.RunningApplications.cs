using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    private static string BrowserReceiptPath(string appName) => Path.Combine(AppDataDir, appName + ".process.json");

    private static ApplicationProcessIdentity? TrackedBrowser(string appName)
    {
        if (appName is not ("VatEFS" or "VATIRIS")) return null;
        try
        {
            var path = BrowserReceiptPath(appName);
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return null;
            var identity = JsonSerializer.Deserialize<ApplicationProcessIdentity>(File.ReadAllText(path));
            return identity != null && Path.GetFileName(identity.ExecutablePath).Equals("msedge.exe", StringComparison.OrdinalIgnoreCase)
                && ApplicationProcessControl.IsAlive(identity) ? identity : null;
        }
        catch { return null; }
    }

    private static void RememberBrowser(string appName, Process process)
    {
        try
        {
            var identity = ApplicationProcessControl.Identity(process);
            Directory.CreateDirectory(AppDataDir);
            File.WriteAllText(BrowserReceiptPath(appName), JsonSerializer.Serialize(identity));
        }
        catch { /* Browser launch can succeed after its starter process exits. Do not guess ownership. */ }
    }

    private static IReadOnlyList<ApplicationProcessIdentity> RunningCopies(CheckResult row) => row.IsWebApp
        ? TrackedBrowser(row.AppName) is { } tracked ? [tracked] : []
        : ApplicationProcessControl.Find(row.LaunchPath);

    private void ShowRunningApplication(CheckResult row)
    {
        bool shown = RunningCopies(row).Any(process => ApplicationProcessControl.TryAct(process, RunningApplicationAction.Show));
        if (!shown)
        {
            row.StatusMessage = "The application is running, but its window could not be brought forward. Use the Windows taskbar or Alt+Tab. No application was closed.";
            SelectApplicationDetails(row);
        }
    }

    private void ShowRunning_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CheckResult row }) ActOnRunningApplication(row, RunningApplicationAction.Show);
    }

    private void CloseApplication_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CheckResult row }) ActOnRunningApplication(row, RunningApplicationAction.CloseNormally);
    }

    private void RunningActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CheckResult row } button) return;
        var menu = new ContextMenu();
        foreach (var (label, action) in new[] { ("Show application", RunningApplicationAction.Show),
            ("Close…", RunningApplicationAction.CloseNormally), ("Force stop…", RunningApplicationAction.ForceStop) })
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => ActOnRunningApplication(row, action);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    private void ActOnRunningApplication(CheckResult row, RunningApplicationAction action)
    {
        if (!CanControlRunningApplication() || !TryAcquireMaintenanceGuard(out var lease)) return;
        using var guard = lease;
        var copies = RunningCopies(row).Where(process => action == RunningApplicationAction.ForceStop || ApplicationProcessControl.HasWindow(process)).ToArray();
        if (copies.Length == 0)
        {
            row.StatusMessage = "Launchpad could not verify a running copy of this application. Use its own window or the Windows taskbar.";
            SelectApplicationDetails(row);
            return;
        }
        if (action == RunningApplicationAction.Show) { ShowRunningApplication(row); return; }
        if (action is RunningApplicationAction.ForceStop or RunningApplicationAction.CloseNormally)
        {
            var verb = action == RunningApplicationAction.ForceStop ? "Force stop" : "Ask to close";
            var detail = action == RunningApplicationAction.ForceStop
                ? "Unsaved work may be lost and active network connections will end." : "Active network connections may end. The application may ask you to save or confirm. Launchpad will not force it to stop.";
            var targets = action == RunningApplicationAction.ForceStop ? $"This affects {copies.Length} verified process{(copies.Length == 1 ? "" : "es")} for the configured application." : $"This asks {copies.Length} application windows to close.";
            if (MessageBox.Show(this, $"{verb} {row.DisplayName}?\n\n{targets}\n\n{detail}",
                    verb + " " + row.DisplayName, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        }
        int accepted = copies.Count(process => ApplicationProcessControl.TryAct(process, action));
        row.StatusMessage = accepted == 0 ? "The application did not accept the request. Use its own window to close it."
            : action == RunningApplicationAction.CloseNormally ? "Close requested. The application may be waiting for you to save or confirm; check its window."
            : "Force-stop requested for the verified running copy. Other installations were left alone.";
        SelectApplicationDetails(row);
        UpdateRunningStates();
    }
}
