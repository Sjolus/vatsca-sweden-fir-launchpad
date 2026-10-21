using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class ControllerProfileControl : UserControl
{
    public AppSettings Settings { get; private set; } = new();
    public bool HasDraft { get; private set; }
    public bool IsBusy { get; private set; }
    public string SaveResultMessage { get; private set; } = "Controller details saved.";
    public bool ApplyNeedsAttention { get; private set; }
    public Func<IDisposable?>? AcquireAction { get; set; }
    public event Action<AppSettings, string, bool>? Applied;
    public event Action? RetryLoadRequested;
    private string _euroscopeDataPath = "";
    private bool _canApplyProfile;
    private bool _actionsEnabled = true;
    private bool _acquiringAction;
    private IReadOnlyList<(string Text, bool NeedsAttention)>? _appliedResult;

    public ControllerProfileControl()
    {
        InitializeComponent();
        foreach (var (value, label) in ProfileService.Ratings)
            RatingBox.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        SetActionsEnabled(true);
    }

    // The owner supplies credentials once on first use. Refresh never reloads them or replaces the form draft.
    public void Initialize(AppSettings current, string euroscopeDataPath, string password, string hoppie)
    {
        if (HasDraft) return;
        Settings = current.Copy();
        _euroscopeDataPath = euroscopeDataPath;
        NameBox.Text = current.VatsimName;
        CidBox.Text = current.VatsimCid;
        ObsBox.Text = current.ObsCallsign;
        PasswordBox.Password = password;
        HoppieBox.Password = hoppie;
        foreach (ComboBoxItem item in RatingBox.Items)
            if ((int)item.Tag == Math.Max(0, current.VatsimRating)) { RatingBox.SelectedItem = item; break; }
        if (RatingBox.SelectedItem is null && RatingBox.Items.Count > 0) RatingBox.SelectedIndex = 0;
        HasDraft = true;
        RetryLoadButton.Visibility = Visibility.Collapsed;
        SetActionsEnabled(_actionsEnabled);
        RefreshContext(current);
    }

    public void RefreshContext(AppSettings current)
    {
        if (!HasDraft || IsBusy || _acquiringAction) return;
        Settings = current.Copy();
        _euroscopeDataPath = current.EuroscopeDataPath;
        _canApplyProfile = !string.IsNullOrWhiteSpace(_euroscopeDataPath) && Directory.Exists(_euroscopeDataPath);
        TargetPathText.Text = string.IsNullOrWhiteSpace(_euroscopeDataPath) ? "Not configured" : _euroscopeDataPath;
        bool hasClients = ControllerClientSetup.HasConfiguredClients(Settings);
        SaveProfileButton.Content = _canApplyProfile || hasClients ? "Save & apply" : "Save profile";
        PreviewButton.IsEnabled = _actionsEnabled && (_canApplyProfile || hasClients);
        PreviewButton.ToolTip = "Review where these details will be applied.";
        ApplyHintText.Text = "Save & apply uses these details for configured EuroScope, vATIS and TrackAudio copies. vATIS also receives the Swedish profile. Close the applications first; audio settings and PTT are kept.";
        bool missingSavedFolder = !_canApplyProfile && !string.IsNullOrWhiteSpace(_euroscopeDataPath);
        SyncBanner.SetResourceReference(Border.BackgroundProperty, missingSavedFolder ? "WarningBg" : "InputBg");
        SyncBanner.Visibility = _canApplyProfile ? Visibility.Collapsed : Visibility.Visible;
        SyncText.SetResourceReference(TextBlock.ForegroundProperty, missingSavedFolder ? "WarningFg" : "SecondaryText");
        SyncText.Text = _canApplyProfile
            ? "Changes apply only when you choose Save & apply. Close the clients first."
            : missingSavedFolder ? "The saved EuroScope folder is unavailable. Your details can still be saved for configured vATIS and TrackAudio copies."
            : "No EuroScope folder selected yet. You can save your details now and apply them after setting up GNG.";
    }

    public void SetActionsEnabled(bool enabled)
    {
        _actionsEnabled = enabled;
        FooterActions.IsEnabled = ProfileFormScroll.IsEnabled = enabled && HasDraft && !IsBusy;
        RetryLoadButton.IsEnabled = enabled && !IsBusy;
        PreviewButton.IsEnabled = enabled && HasDraft && !IsBusy && (_canApplyProfile || ControllerClientSetup.HasConfiguredClients(Settings));
    }

    public void SetLoadUnavailable()
    {
        SetSaveResult(false, "Windows could not read the saved credentials. Try loading them again before editing.");
        RetryLoadButton.Visibility = Visibility.Visible;
        SetActionsEnabled(_actionsEnabled);
    }

    public void DiscardDraft()
    {
        HasDraft = false;
        NameBox.Clear(); CidBox.Clear(); ObsBox.Clear(); PasswordBox.Clear(); HoppieBox.Clear();
        PasswordBoxText.Clear(); HoppieBoxText.Clear();
        PasswordBox.Visibility = HoppieBox.Visibility = Visibility.Visible;
        PasswordBoxText.Visibility = HoppieBoxText.Visibility = Visibility.Collapsed;
        TogglePassword.Content = ToggleHoppie.Content = "Show";
        AutomationProperties.SetName(TogglePassword, "Show VATSIM password");
        AutomationProperties.SetName(ToggleHoppie, "Show Hoppie ACARS code");
        RatingBox.SelectedIndex = 0;
        TargetPathText.Clear(); SyncText.Text = "";
        Settings = new(); ResultText.Text = "";
        _appliedResult = null;
        SetActionsEnabled(_actionsEnabled);
    }

    private void RetryLoad_Click(object sender, RoutedEventArgs e) => RetryLoadRequested?.Invoke();

    public void SetSaveResult(bool saved, string message)
    {
        _appliedResult = null;
        ResultText.Text = message;
        ResultText.SetResourceReference(TextBlock.ForegroundProperty, saved ? "SecondaryText" : "WarningFg");
    }

    public void SetPersistenceResult(bool saved, string message)
    {
        if (_appliedResult is null) { SetSaveResult(saved, message); return; }
        if (saved) { ShowResultLines(_appliedResult); return; }
        var lines = _appliedResult.ToList();
        lines[0] = ("Controller details kept for this session.", false);
        lines.Add(("Launchpad: Choices could not be saved. Choose Save & apply to retry.", true));
        ShowResultLines(lines);
    }

    private void TogglePassword_Click(object sender, RoutedEventArgs e) => ToggleSecret(PasswordBox, PasswordBoxText, TogglePassword);
    private void ToggleHoppie_Click(object sender, RoutedEventArgs e) => ToggleSecret(HoppieBox, HoppieBoxText, ToggleHoppie);
    private static void ToggleSecret(PasswordBox masked, TextBox plain, Button toggle)
    {
        if (plain.Visibility == Visibility.Collapsed)
        {
            plain.Text = masked.Password; plain.Visibility = Visibility.Visible; masked.Visibility = Visibility.Collapsed;
            toggle.Content = "Hide";
        }
        else
        {
            masked.Password = plain.Text; plain.Text = ""; masked.Visibility = Visibility.Visible; plain.Visibility = Visibility.Collapsed;
            toggle.Content = "Show";
        }
        AutomationProperties.SetName(toggle, (plain.Visibility == Visibility.Visible ? "Hide " : "Show ") + AutomationProperties.GetName(masked));
    }
    private string VatsimPasswordValue => PasswordBoxText.Visibility == Visibility.Visible ? PasswordBoxText.Text : PasswordBox.Password;
    private string HoppieCodeValue => HoppieBoxText.Visibility == Visibility.Visible ? HoppieBoxText.Text : HoppieBox.Password;

    private AppSettings BuildSettingsFromForm()
    {
        var draft = Settings.Copy();
        draft.VatsimName = NameBox.Text.Trim();
        draft.VatsimRating = RatingBox.SelectedItem is ComboBoxItem item ? (int)item.Tag : 0;
        draft.VatsimCid = CidBox.Text.Trim();
        draft.ObsCallsign = ObsBox.Text.Trim().ToUpperInvariant();
        return draft;
    }

    private IDisposable? BeginAction()
    {
        if (!HasDraft || IsBusy || !_actionsEnabled) return null;
        IDisposable? lease;
        _acquiringAction = true;
        try { lease = AcquireAction?.Invoke(); }
        finally { _acquiringAction = false; }
        if (lease is null) SetSaveResult(false, "Finish the current operation before previewing or applying this profile.");
        return lease;
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        using var lease = BeginAction();
        if (lease is null) return;
        try
        {
            var draft = BuildSettingsFromForm();
            ValidateProfileApply(requireClosed: false);
            var clientSummary = string.Join("\n", new[] { SoftwareApp.Vatis, SoftwareApp.TrackAudio }
                .Where(app => ControllerClientSetup.IsIncluded(draft, app)).Select(ControllerClientSetup.Describe));
            var masked = _canApplyProfile
                ? ProfileService.GeneratePreview(draft, VatsimPasswordValue, HoppieCodeValue, _euroscopeDataPath, showCredentials: false, includePluginChoices: false)
                : "EuroScope files will not be changed because its data folder is unavailable.";
            if (clientSummary.Length > 0) masked += "\n\n" + clientSummary + "\nClients must be closed. Passwords are not shown.";
            var summary = ReviewSummaries.Format(
                "Save controller details and apply them to configured EuroScope, vATIS and TrackAudio copies.\n\nEuroScope folder: " + _euroscopeDataPath,
                "Other profile settings stay in place. Blank Hoppie and observer fields leave existing plugin codes and observer logins unchanged.",
                "This action does not create a recovery copy. Plugin entries are unchanged. Keep a copy of any custom profiles before applying.",
                "Passwords are saved in Windows Credential Manager and copied to each configured application's settings. TrackAudio stores its copy unencrypted. Return to the form to save and apply.");
            var preview = new ProfilePreviewWindow(summary, masked,
                () => (_canApplyProfile ? ProfileService.GeneratePreview(draft, VatsimPasswordValue, HoppieCodeValue, _euroscopeDataPath, showCredentials: true, includePluginChoices: false)
                    : "EuroScope files will not be changed.") + "\n\n" + clientSummary) { Owner = Window.GetWindow(this) };
            ReportingEvents.Feature(ReportingFeature.ControllerProfileReview);
            preview.ShowDialog();
        }
        catch (Exception ex) { SetSaveResult(false, "Preview unavailable: " + ex.Message); }
    }

    private void ValidateProfileApply(bool requireClosed)
    {
        if (!_canApplyProfile) return;
        GngUpdateService.RequireNoPendingUpdate(_euroscopeDataPath);
        if (requireClosed) EuroScopeProcessGuard.RequireClosed();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        using var lease = BeginAction();
        if (lease is null) return;
        var draft = BuildSettingsFromForm();
        if (string.IsNullOrWhiteSpace(draft.VatsimName)) { SetSaveResult(false, "Enter your full name."); NameBox.Focus(); return; }
        if (string.IsNullOrWhiteSpace(draft.VatsimCid)) { SetSaveResult(false, "Enter your VATSIM CID."); CidBox.Focus(); return; }
        if (string.IsNullOrWhiteSpace(VatsimPasswordValue)) { SetSaveResult(false, "Enter your VATSIM password."); PasswordBox.Focus(); return; }
        try { ValidateProfileApply(requireClosed: true); }
        catch (Exception ex) { SetSaveResult(false, ex.Message); return; }
        ApplyNeedsAttention = false;
        try
        {
            CredentialManagerService.Save(CredentialManagerService.TargetVatsim, VatsimPasswordValue);
            CredentialManagerService.Save(CredentialManagerService.TargetHoppie, HoppieCodeValue);
        }
        catch
        {
            ReportingService.RecordError(ReportingErrorCode.SettingsSaveFailed, new ReportingDetails
            { Application = ReportingApplication.Launchpad, Operation = ReportingOperation.Save, Reason = ReportingReason.SaveFailed });
            SetSaveResult(false, "Windows could not save both credentials. One may already be saved. No application files were changed. Your entries are still here; try again.");
            return;
        }
        Settings = draft;
        SaveResultMessage = "Controller details saved.";
        var resultLines = new List<(string Text, bool NeedsAttention)> { (SaveResultMessage, false) };
        IsBusy = true;
        FooterActions.IsEnabled = ProfileFormScroll.IsEnabled = false;
        SetSaveResult(true, "Applying controller details…");
        try
        {
            if (_canApplyProfile)
            {
                ReportingEvents.Start(ReportingApplication.Euroscope, ReportingOperation.ConfigureSignIn);
                try
                {
                    EuroScopeProcessGuard.RequireClosed();
                    ProfileService.Apply(draft, _euroscopeDataPath, includePluginChoices: false);
                    ReportingEvents.Complete(ReportingApplication.Euroscope, ReportingOperation.ConfigureSignIn, ReportingOutcome.Completed);
                    resultLines.Add(("EuroScope: Updated.", false));
                }
                catch (Exception ex)
                {
                    ApplyNeedsAttention = true;
                    ReportingEvents.Stop(ex, ReportingApplication.Euroscope, ReportingOperation.ConfigureSignIn, ReportingStage.Apply);
                    resultLines.Add(("EuroScope: Could not update. " + ex.Message, true));
                }
            }
            var clients = await ControllerClientSetup.ApplyAsync(draft, VatsimPasswordValue);
            ApplyNeedsAttention |= clients.NeedsAttention;
            resultLines.AddRange(ControllerProfileResultPresentation.ClientLines(clients));
        }
        catch
        {
            ApplyNeedsAttention = true;
            resultLines.Add(("Client setup: Some applications could not be configured. Keep the clients closed and try again.", true));
        }
        finally
        {
            IsBusy = false;
            SetActionsEnabled(_actionsEnabled);
        }
        SaveResultMessage = string.Join(" ", resultLines.Select(line => line.Text));
        _appliedResult = resultLines;
        ShowResultLines(resultLines);
        Applied?.Invoke(Settings, SaveResultMessage, ApplyNeedsAttention);
    }
}
