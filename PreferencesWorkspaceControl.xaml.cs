using System.Windows;
using System.Windows.Controls;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

public partial class PreferencesWorkspaceControl : UserControl
{
    private AppSettings? _current;
    private bool _populating;

    public event Action<AppSettings>? PreferencesChanged;
    public event Action? RetrySaveRequested;
    public event Action? SetupGuideRequested;
    public event Action? PrivacyRequested;
    public event Action? ReportsRequested;

    public PreferencesWorkspaceControl()
    {
        InitializeComponent();
        foreach (var name in ApplicationPreferences.OptionalApplications)
        {
            var label = name == "EuroScope (GNG Pack)" ? "Swedish GNG package" : name;
            var choice = new CheckBox
            {
                Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
                Tag = name,
                MinWidth = 150,
                MaxWidth = 240,
                Margin = new Thickness(0, 0, 16, 8)
            };
            choice.SetResourceReference(ForegroundProperty, "PrimaryText");
            System.Windows.Automation.AutomationProperties.SetName(choice, "I don't use " + label);
            choice.Click += Preference_Click;
            NotUsedChoices.Children.Add(choice);
        }
    }

    public void SetPreferences(AppSettings settings)
    {
        _populating = true;
        try
        {
            _current = settings.Copy();
            CheckOnStartupChoice.IsChecked = settings.CheckOnStartup;
            TextSizeChoice.SelectedIndex = settings.TextScale >= 1.5 ? 2 : settings.TextScale >= 1.25 ? 1 : 0;
            CompactLayoutChoice.IsChecked = settings.CompactLayout;
            AlwaysOnTopChoice.IsChecked = settings.AlwaysOnTop;
            foreach (var choice in NotUsedChoices.Children.OfType<CheckBox>())
                choice.IsChecked = ApplicationPreferences.IsNotUsed(settings, (string)choice.Tag);
        }
        finally { _populating = false; }
    }

    public void SetActionsEnabled(bool enabled) => PreferenceContent.IsEnabled = enabled;

    public void SetSaveResult(bool saved, string? message = null)
    {
        SaveStatus.Text = message ?? (saved ? "Saved." : "Changes apply for this session, but could not be saved.");
        SaveStatus.SetResourceReference(TextBlock.ForegroundProperty, saved ? "SecondaryText" : "WarningFg");
        RetrySaveButton.Visibility = saved ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Preference_Click(object sender, RoutedEventArgs e) => SendPreferences();
    private void TextSize_Changed(object sender, SelectionChangedEventArgs e) => SendPreferences();

    private void SendPreferences()
    {
        if (_populating || _current is null || !PreferenceContent.IsEnabled) return;
        var draft = _current.Copy();
        draft.CheckOnStartup = CheckOnStartupChoice.IsChecked == true;
        draft.TextScale = TextSizeChoice.SelectedIndex switch { 1 => 1.25, 2 => 1.5, _ => 1 };
        draft.CompactLayout = CompactLayoutChoice.IsChecked == true;
        draft.AlwaysOnTop = AlwaysOnTopChoice.IsChecked == true;
        foreach (var choice in NotUsedChoices.Children.OfType<CheckBox>())
            ApplicationPreferences.SetNotUsed(draft, (string)choice.Tag, choice.IsChecked == true);
        _current = draft.Copy();
        PreferencesChanged?.Invoke(draft);
    }

    private void RetrySave_Click(object sender, RoutedEventArgs e) => RetrySaveRequested?.Invoke();
    private void SetupGuide_Click(object sender, RoutedEventArgs e) => SetupGuideRequested?.Invoke();
    private void Privacy_Click(object sender, RoutedEventArgs e) => PrivacyRequested?.Invoke();
    private void Reports_Click(object sender, RoutedEventArgs e) => ReportsRequested?.Invoke();
}
