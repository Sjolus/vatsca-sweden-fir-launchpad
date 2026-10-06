using Velopack;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Installer/update hooks must exit before WPF, settings, timers or external-tool logic runs.
        VelopackApp.Build().SetAutoApplyOnStartup(false)
            .OnAfterInstallFastCallback(version => LaunchpadUninstallService.ScheduleRegistrationAfterSetup())
            .OnAfterUpdateFastCallback(version => LaunchpadUninstallService.TryRegister(out _))
            .OnFirstRun(version => LaunchpadUninstallService.TryRegister(out _))
            .OnRestarted(version => LaunchpadUninstallService.TryRegister(out _))
            .Run();

        if (args.Contains(LaunchpadUninstallService.FinalizerArgument, StringComparer.Ordinal))
        {
            Environment.ExitCode = LaunchpadUninstallService.IsFinalizerRequested(args)
                ? LaunchpadUninstallService.RunRegistrationFinalizer(args[1]) : 1;
            return;
        }
        if (args.Contains(LaunchpadUninstallService.UninstallHelperArgument, StringComparer.Ordinal))
        {
            Environment.ExitCode = LaunchpadUninstallService.IsUninstallHelperRequested(args)
                ? LaunchpadUninstallService.RunUninstallHelper(args[1]) : 1;
            return;
        }
        bool uninstall = LaunchpadUninstallService.IsUninstallRequested(args);
        if (!uninstall && args.Contains(LaunchpadUninstallService.UninstallArgument, StringComparer.Ordinal))
        {
            Environment.ExitCode = 1;
            return;
        }
        // Also repairs registration after silent install, a failed finalizer, or an update without hooks.
        LaunchpadUninstallService.TryRegister(out _);
        var app = new App(uninstall);
        app.InitializeComponent();
        app.Run();
    }
}
