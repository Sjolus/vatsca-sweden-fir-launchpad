using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

// Production form layout only. Credentials and save/apply actions are synthetic and remain in memory.
public partial class ControllerProfileControl : UserControl
{
    public bool HasDraft { get; private set; }
    public bool IsBusy => false;
    public int SyntheticPreviewRequests { get; private set; }
    public int SyntheticSaveRequests { get; private set; }
    public int SyntheticRetryLoadRequests { get; private set; }

    public ControllerProfileControl() { InitializeComponent(); SetActionsEnabled(true); }

    public void Initialize(AppSettings current, string euroscopeDataPath, string password, string hoppie)
    {
        if (HasDraft) return;
        NameBox.Text = current.VatsimName;
        CidBox.Text = current.VatsimCid;
        ObsBox.Text = current.ObsCallsign;
        RatingBox.ItemsSource = new[] { "Observer (OBS)", "Student 1 (S1)", "Student 2 (S2)", "Student 3 (S3)", "Controller (C1)" };
        RatingBox.SelectedIndex = current.VatsimRating;
        PasswordBox.Password = password;
        HoppieBox.Password = hoppie;
        HasDraft = true;
        RetryLoadButton.Visibility = Visibility.Collapsed;
        SetActionsEnabled(true);
        RefreshContext(current);
    }

    public void RefreshContext(AppSettings current)
    {
        var configured = !string.IsNullOrWhiteSpace(current.EuroscopeDataPath);
        TargetPathText.Text = configured ? current.EuroscopeDataPath : "Not configured";
        PreviewButton.IsEnabled = configured;
        SaveProfileButton.Content = configured ? "Save & apply" : "Save profile";
        SyncBanner.SetResourceReference(Border.BackgroundProperty, "InputBg");
        SyncBanner.Visibility = configured ? Visibility.Collapsed : Visibility.Visible;
        SyncText.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
        SyncText.Text = configured ? "Synthetic profile needs review. No real credentials or files were loaded."
            : "No EuroScope folder selected yet. You can save your details now and apply them after setting up GNG.";
        ApplyHintText.Text = configured ? "Synthetic EuroScope profile changes only." : "Save this synthetic profile for later setup.";
    }

    public void SetActionsEnabled(bool enabled)
    {
        ProfileFormScroll.IsEnabled = enabled && HasDraft;
        FooterActions.IsEnabled = enabled && HasDraft;
        RetryLoadButton.IsEnabled = enabled;
    }

    public void SetLoadUnavailable()
    {
        SetSaveResult(false, "Synthetic saved details could not be loaded. Try loading them again before editing.");
        RetryLoadButton.Visibility = Visibility.Visible;
    }

    public void SetSaveResult(bool saved, string message) => ResultText.Text = message;
    private void Preview_Click(object sender, RoutedEventArgs e) => SyntheticPreviewRequests++;
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SyntheticSaveRequests++;
        ResultText.Text = "Synthetic save only. No settings, credentials or external profiles were changed.";
    }
    private void RetryLoad_Click(object sender, RoutedEventArgs e) => SyntheticRetryLoadRequests++;
    private void TogglePassword_Click(object sender, RoutedEventArgs e) => ToggleSecret(PasswordBox, PasswordBoxText);
    private void ToggleHoppie_Click(object sender, RoutedEventArgs e) => ToggleSecret(HoppieBox, HoppieBoxText);
    private static void ToggleSecret(PasswordBox masked, TextBox visible)
    {
        bool show = visible.Visibility != Visibility.Visible;
        if (show) visible.Text = masked.Password; else masked.Password = visible.Text;
        visible.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        masked.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
    }
}

internal static partial class UiFixture
{
    internal static void PopulateController(ControllerProfileControl profile, bool blank)
    {
        var settings = new AppSettings
        {
            VatsimName = "Åsa Östergård (synthetic)", VatsimCid = "0000000", ObsCallsign = "ES", VatsimRating = 2,
            EuroscopeDataPath = blank ? "" : @"C:\Synthetic\EuroScope\An intentionally long controller profile directory name\ESAA"
        };
        profile.Initialize(settings, settings.EuroscopeDataPath, "FAKE-PASSWORD", "FAKE-HOPPIE");
        profile.RefreshContext(settings);
    }
}
