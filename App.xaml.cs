using System.Reflection;
using System.Windows;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class App : Application
{
    private readonly bool _uninstallMode;
    public App() : this(false) { }
    internal App(bool uninstallMode) => _uninstallMode = uninstallMode;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (_uninstallMode)
        {
            if (!LaunchpadUninstallService.CanUninstall(out var reason))
            {
                MessageBox.Show(reason, "Launchpad uninstall", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(1);
                return;
            }
            var settings = SettingsService.Load();
            SetTheme(settings.IsDarkMode);
            MainWindow = new MaintenanceWindow(settings, uninstallLaunchpad: true);
            MainWindow.Show();
            return;
        }
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        Logger.Log("STARTUP", $"VATSCA Launchpad v{version} started");
        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    public static void SetTheme(bool isDark)
    {
        var uri  = new Uri(isDark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative);
        Current.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = uri };
    }
}
