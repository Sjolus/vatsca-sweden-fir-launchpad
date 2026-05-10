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
        ["VatEFS"]       = @"C:\Program Files\VatEFS",
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
            VatEfsPath        = current.VatEfsPath,
            PatchVatEfs       = current.PatchVatEfs,
            VatsimName        = current.VatsimName,
            VatsimRating      = current.VatsimRating,
            VatsimCid         = current.VatsimCid,
            ObsCallsign       = current.ObsCallsign,
            LastEuroscopeProfile = current.LastEuroscopeProfile,
        };

        CheckOnStartup.IsChecked  = Settings.CheckOnStartup;
        EuroscopeExePath.Text = Settings.EuroscopeExePath;
        EuroscopePath.Text    = Settings.EuroscopeDataPath;
        TrackAudioPath.Text   = Settings.TrackAudioExePath;
        VacsPath.Text         = Settings.VacsExePath;
        VatisPath.Text        = Settings.VatisExePath;
        VatEfsPath.Text       = Settings.VatEfsPath;
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)?.Tag?.ToString();
        var (title, target) = tag switch
        {
            "VatEFS" => ("Select VatEFS Folder",          VatEfsPath),
            _        => ("Select EuroScope Data Folder", EuroscopePath),
        };

        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (!string.IsNullOrWhiteSpace(target.Text))
            dialog.InitialDirectory = target.Text;

        if (dialog.ShowDialog() == true)
            target.Text = dialog.FolderName;
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
        Settings.VatEfsPath        = VatEfsPath.Text.Trim();
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
        "VatEFS"       => VatEfsPath,
        _              => throw new ArgumentOutOfRangeException(nameof(key), key, null),
    };

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
