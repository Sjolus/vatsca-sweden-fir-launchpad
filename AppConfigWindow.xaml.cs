using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class AppConfigWindow : Window
{
    public AppSettings Settings { get; private set; }

    private readonly string _euroscopeDataPath;
    private bool _canApplyProfile;

    public AppConfigWindow(AppSettings current, string euroscopeDataPath)
    {
        InitializeComponent();
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);

        _euroscopeDataPath = euroscopeDataPath;

        Settings = current.Copy();

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
        PatchVatEfsBox.IsChecked = current.PatchVatEfs;

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
        bool canApply = !string.IsNullOrWhiteSpace(_euroscopeDataPath) && Directory.Exists(_euroscopeDataPath);
        _canApplyProfile = canApply;
        TargetPathText.Text = string.IsNullOrWhiteSpace(_euroscopeDataPath) ? "Not configured" : _euroscopeDataPath;
        SaveProfileButton.Content = canApply ? "Save & apply" : "Save profile";
        PreviewButton.IsEnabled = canApply;
        PreviewButton.ToolTip = canApply ? "Review changes to the configured EuroScope files." : "Set an existing EuroScope data folder in Settings to preview file changes.";
        ApplyHintText.Text = canApply
            ? "Save & apply stores this controller profile and updates all EuroScope profiles (ES*.prf) in the folder above. Preview lets you review the file changes first."
            : "Save profile stores your details and credentials in Launchpad only. Set an existing EuroScope data folder in Settings before applying them to files.";
        SyncBanner.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "WarningBg");
        SyncText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "WarningFg");
        if (!canApply)
        {
            SyncText.Text = string.IsNullOrWhiteSpace(_euroscopeDataPath)
                ? "No EuroScope data folder is configured. No profile files will be updated."
                : "The configured EuroScope data folder is unavailable. No profile files will be updated.";
        }
        else if (!ProfileService.IsConfigured(Settings))
        {
            SyncText.Text = "Controller profile not yet configured. Fill in the fields below, then preview or save & apply.";
        }
        else if (!ProfileService.IsInSync(Settings, _euroscopeDataPath))
        {
            SyncText.Text = "The saved profile is out of sync with EuroScope files. Preview the changes before applying.";
        }
        else
        {
            SyncBanner.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "SuccessBg");
            SyncText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "SuccessFg");
            SyncText.Text = "The saved controller profile matches the current EuroScope profile check.";
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
        AutomationProperties.SetName(toggle, (plain.Visibility == Visibility.Visible ? "Hide " : "Show ") + AutomationProperties.GetName(masked));
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
        if (string.IsNullOrWhiteSpace(_euroscopeDataPath) || !Directory.Exists(_euroscopeDataPath))
        {
            UpdateSyncBanner();
            return;
        }
        var temp        = BuildSettingsFromForm();
        var maskedText  = ProfileService.GeneratePreview(temp, VatsimPasswordValue, HoppieCodeValue, _euroscopeDataPath, showCredentials: false);
        var changes = "Apply your name, CID, rating and password to the Swedish EuroScope profiles in the folder below. The server selection becomes AUTOMATIC.";
        if (!string.IsNullOrWhiteSpace(HoppieCodeValue)) changes += " Update the supported Hoppie code files.";
        if (!string.IsNullOrWhiteSpace(temp.ObsCallsign)) changes += " Update the observer login callsign.";
        if (!string.IsNullOrWhiteSpace(temp.VatEfsPath)) changes += temp.PatchVatEfs
            ? " Enable the configured VatEFS plugin where its DLL is available."
            : " Remove VatEFS plugin entries from these profiles.";
        var kept = "Other profile settings stay in place." +
            (string.IsNullOrWhiteSpace(HoppieCodeValue) ? " Existing Hoppie codes in plugin files stay unchanged because the code field is blank." : "") +
            (string.IsNullOrWhiteSpace(temp.ObsCallsign) ? " Existing observer logins stay unchanged because the prefix is blank." : "");
        var summary = ReviewSummaries.Format(changes + "\n\nProfile folder: " + _euroscopeDataPath, kept,
            "This profile action does not create a recovery copy. Make a copy of any custom profiles you want to keep before applying.",
            "Passwords are saved in Windows Credential Manager and also written into the profile files. The preview is read-only; return to the profile form to save and apply.");
        var previewWindow = new ProfilePreviewWindow(summary, maskedText,
            () => ProfileService.GeneratePreview(temp, VatsimPasswordValue, HoppieCodeValue, _euroscopeDataPath, showCredentials: true))
        {
            Owner = this,
        };
        previewWindow.ShowDialog();
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
            VatEfsPath        = Settings.VatEfsPath,
            PatchVatEfs       = PatchVatEfsBox.IsChecked == true,
            VatsimName        = NameBox.Text.Trim(),
            VatsimRating      = rating,
            VatsimCid         = CidBox.Text.Trim(),
            ObsCallsign       = ObsBox.Text.Trim().ToUpper(),
            LastEuroscopeProfile = Settings.LastEuroscopeProfile,
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
        try
        {
            CredentialManagerService.Save(CredentialManagerService.TargetVatsim, VatsimPasswordValue);
            CredentialManagerService.Save(CredentialManagerService.TargetHoppie, HoppieCodeValue);
        }
        catch
        {
            // Credential Manager writes are separate operations. A first successful
            // write cannot be rolled back safely; never apply files after either fails.
            MessageBox.Show(this, "Windows could not save both credentials. One may already have been saved. No EuroScope files were updated. Your entries are still here; try saving again.",
                "Controller profile not saved", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Settings.VatsimName   = built.VatsimName;
        Settings.VatsimRating = built.VatsimRating;
        Settings.VatsimCid    = built.VatsimCid;
        Settings.ObsCallsign  = built.ObsCallsign;
        Settings.PatchVatEfs  = built.PatchVatEfs;

        // Follow the action shown in the dialog: a folder appearing later must not turn
        // "Save profile" into permission to write files. Apply itself rechecks a disappearing folder.
        if (_canApplyProfile)
        {
            try
            {
                ProfileService.Apply(Settings, _euroscopeDataPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Settings saved, but could not update EuroScope files:\n\n{ex.Message}",
                    "Apply failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        else
        {
            MessageBox.Show(this, "Your controller profile was saved in Launchpad. No EuroScope files were updated because the data folder is not configured or is unavailable.",
                "Controller profile saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
