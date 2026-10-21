using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

// Presentation only. The owner performs discovery, validates paths and saves explicit choices.
public partial class SetupWorkspaceControl : UserControl
{
    public event EventHandler<SetupWorkspaceRequest>? Requested;
    public event EventHandler? LocationAccepted;
    public event EventHandler? LocationCancelled;
    public event Action? SectionChanged;
    public string SelectedSection { get; private set; } = "Applications";
    public PreferencesWorkspaceControl Preferences => PreferencesPanel;
    public ActivityWorkspaceControl Activity => ActivityPanel;
    public ControllerProfileControl ControllerProfile => ControllerProfilePanel;
    public static readonly DependencyProperty ActionsEnabledProperty = DependencyProperty.Register(
        nameof(ActionsEnabled), typeof(bool), typeof(SetupWorkspaceControl), new PropertyMetadata(true));
    public bool ActionsEnabled { get => (bool)GetValue(ActionsEnabledProperty); private set => SetValue(ActionsEnabledProperty, value); }
    private PathValidationStatus _validation;
    private bool _guideVisible;
    private bool _noticeIsDefault = true;

    public SetupWorkspaceControl() => InitializeComponent();

    public void SetApplications(IEnumerable<SetupWorkspaceApplication> applications)
    {
        var next = applications.ToArray();
        if (ApplicationsList.ItemsSource is SetupWorkspaceApplication[] current && current.SequenceEqual(next)) return;
        ApplicationsList.ItemsSource = next;
    }
    public void SetSummary(string gngPath, string gngSummary, string identity, bool saamanEnabled, bool vatEfsEnabled)
    {
        GngLocation.Text = gngPath;
        GngSummary.Text = gngSummary;
        bool newPackage = string.IsNullOrWhiteSpace(gngPath);
        GngLocationButton.Content = newPackage ? "Use existing folder…" : "Location…";
        GngPackageButton.Content = newPackage ? "Install Swedish GNG…" : "Get / update package…";
        GngDestinationHint.Visibility = newPackage ? Visibility.Visible : Visibility.Collapsed;
        GngMaintenanceActions.Visibility = newPackage ? Visibility.Collapsed : Visibility.Visible;
    }
    public void SetGuide(SetupGuidePresentation? guide)
    {
        _guideVisible = guide is not null;
        SetupGuide.Visibility = _guideVisible ? Visibility.Visible : Visibility.Collapsed;
        if (guide is not null)
        {
            GuideHeading.Text = guide.Heading;
            GuideMessage.Text = guide.Message;
            GuideNextButton.Content = guide.NextLabel;
        }
        UpdateNoticeVisibility();
    }
    public void ShowSection(string section)
    {
        ApplicationsTab.IsChecked = section == "Applications";
        GngTab.IsChecked = section == "Gng";
        ProfileTab.IsChecked = section == "Profile";
        PreferencesTab.IsChecked = section == "Preferences";
        ActivityTab.IsChecked = section == "Activity";
    }
    public void ShowNotice(string message)
    {
        _noticeIsDefault = false;
        SetupNotice.Text = message;
        UpdateNoticeVisibility();
    }
    private void UpdateNoticeVisibility() => SetupNotice.Visibility =
        _guideVisible && _noticeIsDefault ? Visibility.Collapsed : Visibility.Visible;
    public void SetActionsEnabled(bool enabled)
    {
        ActionsEnabled = enabled;
        GngPanel.IsEnabled = ProfilePanel.IsEnabled = LocationReview.IsEnabled = enabled;
        PreferencesPanel.SetActionsEnabled(enabled);
        ControllerProfilePanel.SetActionsEnabled(enabled);
    }
    public void ReviewLocation(string name, string previous, ConfiguredPathValidation result)
    {
        LocationHeading.Text = "Change " + name + " location";
        PreviousLocation.Text = string.IsNullOrWhiteSpace(previous) ? "Not chosen" : previous;
        ProposedLocation.Text = result.Path;
        LocationValidation.Text = result.Message;
        _validation = result.Status;
        AcceptLocationWarning.IsChecked = false;
        AcceptLocationWarning.Visibility = _validation == PathValidationStatus.Warning ? Visibility.Visible : Visibility.Collapsed;
        SaveLocationButton.IsEnabled = _validation == PathValidationStatus.Verified;
        LocationReview.Visibility = Visibility.Visible;
        LocationReview.BringIntoView();
        if (SaveLocationButton.IsEnabled) SaveLocationButton.Focus();
    }
    public void ClearLocationReview() => LocationReview.Visibility = Visibility.Collapsed;
    private void Section_Checked(object sender, RoutedEventArgs e)
    {
        if (ApplicationsPanel is null || PreferencesPanel is null || ActivityPanel is null || ProfilePanel is null) return;
        var section = (sender as FrameworkElement)?.Tag as string;
        ApplicationsPanel.Visibility = section == "Applications" ? Visibility.Visible : Visibility.Collapsed;
        GngPanel.Visibility = section == "Gng" ? Visibility.Visible : Visibility.Collapsed;
        ProfilePanel.Visibility = section == "Profile" ? Visibility.Visible : Visibility.Collapsed;
        SetupSectionScroll.Visibility = section is "Applications" or "Gng" ? Visibility.Visible : Visibility.Collapsed;
        PreferencesPanel.Visibility = section == "Preferences" ? Visibility.Visible : Visibility.Collapsed;
        ActivityPanel.Visibility = section == "Activity" ? Visibility.Visible : Visibility.Collapsed;
        if (section is not null && SelectedSection != section)
        {
            SelectedSection = section;
            SectionChanged?.Invoke();
        }
    }
    private void Request(SetupWorkspaceAction action, object? sender = null, string? key = null) =>
        Requested?.Invoke(this, new(action, key ?? ((sender as FrameworkElement)?.DataContext as SetupWorkspaceApplication)?.Key));
    private void Discover_Click(object sender, RoutedEventArgs e) => Request(SetupWorkspaceAction.Discover);
    private void Change_Click(object sender, RoutedEventArgs e) => Request(SetupWorkspaceAction.ChangeLocation, sender);
    private void Manage_Click(object sender, RoutedEventArgs e) => Request(SetupWorkspaceAction.Manage, sender);
    private void GngLocation_Click(object sender, RoutedEventArgs e) => Request(SetupWorkspaceAction.ChangeLocation, key: "EuroScope (GNG Pack)");
    private void Gng_Click(object sender, RoutedEventArgs e) => Request(SetupWorkspaceAction.Gng);
    private void Cleanup_Click(object sender, RoutedEventArgs e) => Request(SetupWorkspaceAction.Cleanup);
    private void Fonts_Click(object sender, RoutedEventArgs e) => Request(SetupWorkspaceAction.Fonts);
    private void Controller_Click(object sender, RoutedEventArgs e) => Request(SetupWorkspaceAction.ControllerProfile);
    private void Plugin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { IsEnabled: true, DataContext: SetupWorkspaceApplication { Plugin.CanChange: true } row } choice)
        {
            bool requested = choice.IsChecked == true;
            choice.GetBindingExpression(CheckBox.IsCheckedProperty)?.UpdateTarget();
            Requested?.Invoke(this, new(SetupWorkspaceAction.TogglePlugin, row.Key, requested));
        }
    }
    private void PluginCancel_Click(object sender, RoutedEventArgs e) => Request(SetupWorkspaceAction.PluginCancel, sender);
    private void PluginRetrySave_Click(object sender, RoutedEventArgs e) => Request(SetupWorkspaceAction.PluginRetrySave, sender);
    private void PluginRuntime_Click(object sender, RoutedEventArgs e) => Request(SetupWorkspaceAction.PluginRuntime, sender);
    private void PluginGng_Click(object sender, RoutedEventArgs e) => ShowSection("Gng");
    private void GuideNext_Click(object sender, RoutedEventArgs e) => Request(SetupWorkspaceAction.GuideNext);
    private void GuideLater_Click(object sender, RoutedEventArgs e) => Request(SetupWorkspaceAction.GuideLater);
    private void PluginInfo_Open(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { ToolTip: ToolTip tip } target) return;
        tip.PlacementTarget = target;
        tip.IsOpen = true;
    }
    private void PluginInfo_Close(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ToolTip: ToolTip tip }) tip.IsOpen = false;
    }
    private void PluginInfo_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || sender is not FrameworkElement { ToolTip: ToolTip { IsOpen: true } tip }) return;
        tip.IsOpen = false;
        e.Handled = true;
    }
    private void Warning_Changed(object sender, RoutedEventArgs e) { if (SaveLocationButton != null) SaveLocationButton.IsEnabled = _validation == PathValidationStatus.Verified || _validation == PathValidationStatus.Warning && AcceptLocationWarning.IsChecked == true; }
    private void SaveLocation_Click(object sender, RoutedEventArgs e) { if (SaveLocationButton.IsEnabled) LocationAccepted?.Invoke(this, EventArgs.Empty); }
    private void CancelLocation_Click(object sender, RoutedEventArgs e) { ClearLocationReview(); LocationCancelled?.Invoke(this, EventArgs.Empty); }
}
