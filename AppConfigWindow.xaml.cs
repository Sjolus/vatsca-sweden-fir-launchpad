using System.Windows;
using System.Windows.Media;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class AppConfigWindow : Window
{
    public AppSettings Settings { get; private set; }

    private readonly string _euroscopeDataPath;

    private static readonly Brush BrushAmber = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#ff9800"));
    private static readonly Brush BrushGreen = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#41826e"));

    public AppConfigWindow(AppSettings current, string euroscopeDataPath)
    {
        InitializeComponent();

        _euroscopeDataPath = euroscopeDataPath;

        Settings = new AppSettings
        {
            CheckOnStartup    = current.CheckOnStartup,
            EuroscopeExePath  = current.EuroscopeExePath,
            EuroscopeDataPath = current.EuroscopeDataPath,
            TrackAudioExePath = current.TrackAudioExePath,
            VacsExePath       = current.VacsExePath,
            VatisExePath      = current.VatisExePath,
            VatsimName        = current.VatsimName,
            VatsimRating      = current.VatsimRating,
            VatsimCid         = current.VatsimCid,
            ObsCallsign       = current.ObsCallsign,
        };

        // Populate rating ComboBox
        foreach (var (value, label) in ProfileService.Ratings)
            RatingBox.Items.Add(new System.Windows.Controls.ComboBoxItem
                { Content = label, Tag = value });

        // Restore field values
        NameBox.Text    = current.VatsimName;
        CidBox.Text     = current.VatsimCid;
        ObsBox.Text          = current.ObsCallsign;
        PasswordBox.Password = CredentialManagerService.Load(CredentialManagerService.TargetVatsim)  ?? string.Empty;
        HoppieBox.Password   = CredentialManagerService.Load(CredentialManagerService.TargetHoppie) ?? string.Empty;

        // Select current rating
        var ratingToSelect = current.VatsimRating >= 0 ? current.VatsimRating : 0;
        foreach (System.Windows.Controls.ComboBoxItem item in RatingBox.Items)
            if ((int)item.Tag == ratingToSelect) { RatingBox.SelectedItem = item; break; }
        if (RatingBox.SelectedItem is null && RatingBox.Items.Count > 0)
            RatingBox.SelectedIndex = 0;

        UpdateSyncBanner();
    }

    private void UpdateSyncBanner()
    {
        if (!ProfileService.IsConfigured(Settings))
        {
            SyncBanner.Background = BrushAmber;
            SyncText.Text = "Profile not yet configured — fill in the fields below and click Save & Apply.";
        }
        else if (!ProfileService.IsInSync(Settings, _euroscopeDataPath))
        {
            SyncBanner.Background = BrushAmber;
            SyncText.Text = "Profile is out of sync with EuroScope files — click Save & Apply to update.";
        }
        else
        {
            SyncBanner.Background = BrushGreen;
            SyncText.Text = "Profile is up to date.";
        }
    }

    private void TogglePassword_Click(object sender, RoutedEventArgs e) =>
        ToggleSecret(PasswordBox, PasswordBoxText, TogglePassword);

    private void ToggleHoppie_Click(object sender, RoutedEventArgs e) =>
        ToggleSecret(HoppieBox, HoppieBoxText, ToggleHoppie);

    private static void ToggleSecret(
        System.Windows.Controls.PasswordBox masked,
        System.Windows.Controls.TextBox     plain,
        System.Windows.Controls.Button      toggle)
    {
        if (plain.Visibility == Visibility.Collapsed)
        {
            plain.Text       = masked.Password;
            plain.Visibility = Visibility.Visible;
            masked.Visibility = Visibility.Collapsed;
            toggle.Content   = "Hide";
        }
        else
        {
            masked.Password   = plain.Text;
            masked.Visibility = Visibility.Visible;
            plain.Visibility  = Visibility.Collapsed;
            toggle.Content    = "Show";
        }
    }

    // Ensure Password / Hoppie properties return the active control's value
    private string VatsimPasswordValue =>
        PasswordBoxText.Visibility == Visibility.Visible
            ? PasswordBoxText.Text : PasswordBox.Password;

    private string HoppieCodeValue =>
        HoppieBoxText.Visibility == Visibility.Visible
            ? HoppieBoxText.Text : HoppieBox.Password;

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        var temp        = BuildSettingsFromForm();
        var maskedText  = ProfileService.GeneratePreview(temp, VatsimPasswordValue, HoppieCodeValue, _euroscopeDataPath, showCredentials: false);
        var clearText   = ProfileService.GeneratePreview(temp, VatsimPasswordValue, HoppieCodeValue, _euroscopeDataPath, showCredentials: true);
        bool showing    = false;

        var appBg   = Application.Current.Resources["AppBg"]   as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White;
        var inputBg = Application.Current.Resources["InputBg"] as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.White;
        var inputFg = Application.Current.Resources["InputFg"] as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Black;

        var tb = new System.Windows.Controls.TextBox
        {
            Text       = maskedText,
            IsReadOnly = true,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize   = 12,
            Margin     = new Thickness(16, 16, 16, 8),
            BorderThickness = new Thickness(0),
            Background = inputBg,
            Foreground = inputFg,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility   = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
        };

        var toggleBtn = new System.Windows.Controls.Button
        {
            Content         = "Show Credentials",
            Margin          = new Thickness(16, 0, 16, 12),
            Padding         = new Thickness(12, 6, 12, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background      = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1a475f")),
            Foreground      = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor          = System.Windows.Input.Cursors.Hand,
        };
        toggleBtn.Click += (_, _) =>
        {
            showing    = !showing;
            tb.Text    = showing ? clearText : maskedText;
            toggleBtn.Content = showing ? "Hide Credentials" : "Show Credentials";
        };

        var dock = new System.Windows.Controls.DockPanel();
        System.Windows.Controls.DockPanel.SetDock(toggleBtn, System.Windows.Controls.Dock.Bottom);
        dock.Children.Add(toggleBtn);
        dock.Children.Add(tb);

        new Window
        {
            Title  = "Preview — changes that will be applied",
            Width  = 660, Height = 480,
            Owner  = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = appBg,
            Content    = dock,
        }.ShowDialog();
    }

    private AppSettings BuildSettingsFromForm()
    {
        var rating = RatingBox.SelectedItem is System.Windows.Controls.ComboBoxItem item
            ? (int)item.Tag : 0;
        return new AppSettings
        {
            CheckOnStartup    = Settings.CheckOnStartup,
            EuroscopeExePath  = Settings.EuroscopeExePath,
            EuroscopeDataPath = Settings.EuroscopeDataPath,
            TrackAudioExePath = Settings.TrackAudioExePath,
            VacsExePath       = Settings.VacsExePath,
            VatisExePath      = Settings.VatisExePath,
            VatsimName        = NameBox.Text.Trim(),
            VatsimRating      = rating,
            VatsimCid         = CidBox.Text.Trim(),
            ObsCallsign       = ObsBox.Text.Trim().ToUpper(),
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text.Trim()))
        {
            MessageBox.Show("Please enter your full name.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(CidBox.Text.Trim()))
        {
            MessageBox.Show("Please enter your VATSIM CID.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(VatsimPasswordValue))
        {
            MessageBox.Show("Please enter your VATSIM password.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var built = BuildSettingsFromForm();
        Settings.VatsimName   = built.VatsimName;
        Settings.VatsimRating = built.VatsimRating;
        Settings.VatsimCid    = built.VatsimCid;
        Settings.ObsCallsign  = built.ObsCallsign;

        CredentialManagerService.Save(CredentialManagerService.TargetVatsim,  VatsimPasswordValue);
        CredentialManagerService.Save(CredentialManagerService.TargetHoppie, HoppieCodeValue);

        if (!string.IsNullOrWhiteSpace(_euroscopeDataPath))
        {
            try
            {
                ProfileService.Apply(Settings, _euroscopeDataPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Settings saved, but could not update EuroScope files:\n\n{ex.Message}",
                    "Apply failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
