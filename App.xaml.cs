using System.Reflection;
using System.Windows;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        Logger.Log("STARTUP", $"VATSCA Launchpad v{version} started");
    }

    public static void SetTheme(bool isDark)
    {
        var uri  = new Uri(isDark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative);
        Current.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = uri };
    }
}
