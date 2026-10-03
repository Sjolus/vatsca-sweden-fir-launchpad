using Velopack;

namespace VatscaUpdateChecker;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Installer/update hooks must exit before WPF, settings, timers or external-tool logic runs.
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
