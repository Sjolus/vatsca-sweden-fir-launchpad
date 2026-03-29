using System.IO;
using Microsoft.Win32;
using System.Windows;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

public partial class SettingsWindow : Window
{
    public AppSettings Settings { get; private set; }

    private static readonly Dictionary<string, string> StandardPaths = new()
    {
        ["EuroScope"]    = @"C:\Program Files (x86)\EuroScope\EuroScope.exe",
        ["EuroscopeData"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EuroScope"),
        ["TrackAudio"]   = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\trackaudio\trackaudio.exe"),
        ["VACS"]         = @"C:\Program Files\vacs\vacs-client.exe",
        ["vATIS"]        = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"org.vatsim.vatis\current\vATIS.exe"),
    };

    public SettingsWindow(AppSettings current)
    {
        InitializeComponent();

        // Work on a copy so Cancel truly discards changes
        Settings = new AppSettings
        {
            CheckOnStartup    = current.CheckOnStartup,
            EuroscopeExePath  = current.EuroscopeExePath,
            EuroscopeDataPath = current.EuroscopeDataPath,
            TrackAudioExePath = current.TrackAudioExePath,
            VacsExePath       = current.VacsExePath,
            VatisExePath      = current.VatisExePath,
        };

        CheckOnStartup.IsChecked  = Settings.CheckOnStartup;
        EuroscopeExePath.Text = Settings.EuroscopeExePath;
        EuroscopePath.Text    = Settings.EuroscopeDataPath;
        TrackAudioPath.Text   = Settings.TrackAudioExePath;
        VacsPath.Text         = Settings.VacsExePath;
        VatisPath.Text        = Settings.VatisExePath;
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select EuroScope Data Folder",
            Multiselect = false,
        };
        if (!string.IsNullOrWhiteSpace(EuroscopePath.Text))
            dialog.InitialDirectory = EuroscopePath.Text;

        if (dialog.ShowDialog() == true)
            EuroscopePath.Text = dialog.FolderName;
    }

    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var appName = (sender as FrameworkElement)?.Tag?.ToString() ?? "Application";
        var dialog  = new OpenFileDialog
        {
            Title            = $"Select {appName} Executable",
            Filter           = "Executables (*.exe)|*.exe",
            CheckFileExists  = true,
        };

        // Pre-navigate to the currently set directory if any
        var current = appName switch
        {
            "EuroScope"  => EuroscopeExePath.Text,
            "TrackAudio" => TrackAudioPath.Text,
            "VACS"       => VacsPath.Text,
            "vATIS"      => VatisPath.Text,
            _            => string.Empty,
        };
        if (!string.IsNullOrWhiteSpace(current))
            dialog.InitialDirectory = System.IO.Path.GetDirectoryName(current);

        if (dialog.ShowDialog() != true) return;

        switch (appName)
        {
            case "EuroScope":  EuroscopeExePath.Text = dialog.FileName; break;
            case "TrackAudio": TrackAudioPath.Text   = dialog.FileName; break;
            case "VACS":       VacsPath.Text         = dialog.FileName; break;
            case "vATIS":      VatisPath.Text        = dialog.FileName; break;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Settings.CheckOnStartup    = CheckOnStartup.IsChecked == true;
        Settings.EuroscopeExePath  = EuroscopeExePath.Text.Trim();
        Settings.EuroscopeDataPath = EuroscopePath.Text.Trim();
        Settings.TrackAudioExePath = TrackAudioPath.Text.Trim();
        Settings.VacsExePath       = VacsPath.Text.Trim();
        Settings.VatisExePath      = VatisPath.Text.Trim();
        DialogResult = true;
    }

    private void InsertDefault_Click(object sender, RoutedEventArgs e)
    {
        var key = (sender as FrameworkElement)?.Tag?.ToString() ?? "";
        if (!StandardPaths.TryGetValue(key, out var path)) return;
        TextBoxFor(key).Text = path;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        var key = (sender as FrameworkElement)?.Tag?.ToString() ?? "";
        TextBoxFor(key).Text = string.Empty;
    }

    private System.Windows.Controls.TextBox TextBoxFor(string key) => key switch
    {
        "EuroScope"    => EuroscopeExePath,
        "EuroscopeData" => EuroscopePath,
        "TrackAudio"   => TrackAudioPath,
        "VACS"         => VacsPath,
        "vATIS"        => VatisPath,
        _              => throw new ArgumentOutOfRangeException(nameof(key), key, null),
    };

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
