using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

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
    private bool _gngHasFolder;
    private bool _gngChecking;
    private bool _gngOperationActive;
    private bool _gngNeedsAttention;
    private bool _gngCanCancel;
    private bool _showGngOperation;
    private SetupWorkspaceAction _gngPrimaryAction = SetupWorkspaceAction.Gng;

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
        _gngHasFolder = !string.IsNullOrWhiteSpace(gngPath);
        GngAdoptButton.Visibility = GngDestinationHint.Visibility = _gngHasFolder ? Visibility.Collapsed : Visibility.Visible;
        GngLocationButton.Visibility = GngCheckFontsButton.Visibility = GngCleanupButton.Visibility = _gngHasFolder ? Visibility.Visible : Visibility.Collapsed;
        _gngPrimaryAction = _gngHasFolder ? SetupWorkspaceAction.GngCheck : SetupWorkspaceAction.Gng;
        GngPackageButton.Content = _gngHasFolder ? "Check for updates" : "Install Swedish GNG";
    }
    public void SetGngState(CheckResult row, string? operationStatus = null)
    {
        GngInstalledVersion.Text = _gngHasFolder ? row.DisplayInstalledVersion : "Not installed";
        GngLatestVersion.Text = row.DisplayLatestVersion;
        _gngChecking = row.Status == CheckStatus.Checking;
        _gngOperationActive = row.GngOperation is not null;
        _gngNeedsAttention = row.GngOperation?.NeedsAttention == true;
        _gngCanCancel = row.GngOperation?.CanCancel == true;
        bool missingFolder = _gngHasFolder && row.InstalledVersion == "Folder not found";
        bool install = !_gngHasFolder || row.Status == CheckStatus.NotConfigured || row.InstalledVersion == "No SCT file found";
        _gngPrimaryAction = _gngNeedsAttention ? SetupWorkspaceAction.Gng : missingFolder ? SetupWorkspaceAction.ChangeLocation
            : install || row.Status == CheckStatus.UpdateAvailable ? SetupWorkspaceAction.Gng : SetupWorkspaceAction.GngCheck;
        GngPackageButton.Content = _gngNeedsAttention ? "Continue" : _gngChecking ? "Checking…" : missingFolder ? "Choose folder…" : install ? "Install Swedish GNG"
            : row.Status == CheckStatus.UpdateAvailable ? "Update GNG" : "Check for updates";
        GngPackageButton.Visibility = _gngOperationActive && !_gngNeedsAttention ? Visibility.Collapsed : Visibility.Visible;
        GngCancelButton.Visibility = _gngCanCancel ? Visibility.Visible : Visibility.Collapsed;
        GngProgress.Visibility = row.GngOperation?.Busy == true ? Visibility.Visible : Visibility.Collapsed;
        GngProgress.IsIndeterminate = row.GngOperation?.Percent is null;
        GngProgress.Value = row.GngOperation?.Percent ?? 0;
        GngSummary.Text = !string.IsNullOrWhiteSpace(operationStatus) ? operationStatus
            : row.GngOperation is { } operation ? operation.Message
            : !_gngHasFolder ? "Install the Swedish package, or choose the copy you already use." : row.RowStatusText;
        GngSummary.SetResourceReference(TextBlock.ForegroundProperty, row.GngOperation?.Warning == true ? "WarningFg" : "SecondaryText");
        GngFontsStatus.Text = !_gngHasFolder ? "Checked after installation" : row.FontsState switch
        {
            FontsState.AllOk => "Up to date",
            FontsState.NeedsAction => "Missing or outdated",
            _ => "Not checked"
        };
        GngFontsStatus.SetResourceReference(TextBlock.ForegroundProperty, row.FontsState == FontsState.NeedsAction && _gngHasFolder ? "WarningFg" : "SecondaryText");
        GngFontsStatus.ToolTip = _gngHasFolder && !string.IsNullOrWhiteSpace(row.FontsTooltip) ? row.FontsTooltip : null;
        GngFontsButton.Visibility = _gngHasFolder && row.FontsState == FontsState.NeedsAction ? Visibility.Visible : Visibility.Collapsed;
        UpdateGngActions();
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
    public void SetGngOperationContent(FrameworkElement? content)
    {
        GngOperationHost.Content = content;
        if (content is null) _showGngOperation = false;
        PresentSectionContent();
    }
    public void ShowGngOperation(bool show)
    {
        _showGngOperation = show && GngOperationHost.Content is not null;
        PresentSectionContent();
    }
    public void ShowNotice(string message)
    {
        _noticeIsDefault = false;
        SetupNotice.Text = message;
        UpdateNoticeVisibility();
    }
    private void UpdateNoticeVisibility() => SetupNotice.Visibility =
        _noticeIsDefault && (_guideVisible || SelectedSection == "Gng") ? Visibility.Collapsed : Visibility.Visible;
    public void SetActionsEnabled(bool enabled)
    {
        ActionsEnabled = enabled;
        ProfilePanel.IsEnabled = LocationReview.IsEnabled = enabled;
        UpdateGngActions();
        PreferencesPanel.SetActionsEnabled(enabled);
        ControllerProfilePanel.SetActionsEnabled(enabled);
    }
    private void UpdateGngActions()
    {
        bool normalActions = ActionsEnabled && !_gngOperationActive;
        GngPackageButton.IsEnabled = _gngNeedsAttention || normalActions && !_gngChecking;
        GngCancelButton.IsEnabled = _gngCanCancel;
        foreach (var button in new[] { GngAdoptButton, GngLocationButton, GngImportButton, GngFreshButton, GngCheckFontsButton, GngCleanupButton, GngFontsButton })
            button.IsEnabled = normalActions;
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
        if (section is null) return;
        bool changed = SelectedSection != section;
        SelectedSection = section;
        PresentSectionContent();
        UpdateNoticeVisibility();
        if (changed)
        {
            SectionChanged?.Invoke();
        }
    }
    private void PresentSectionContent()
    {
        var section = SelectedSection;
        ApplicationsPanel.Visibility = section == "Applications" ? Visibility.Visible : Visibility.Collapsed;
        GngPanel.Visibility = section == "Gng" ? Visibility.Visible : Visibility.Collapsed;
        ProfilePanel.Visibility = !_showGngOperation && section == "Profile" ? Visibility.Visible : Visibility.Collapsed;
        SetupSectionScroll.Visibility = !_showGngOperation && (section is "Applications" or "Gng") ? Visibility.Visible : Visibility.Collapsed;
        PreferencesPanel.Visibility = !_showGngOperation && section == "Preferences" ? Visibility.Visible : Visibility.Collapsed;
        ActivityPanel.Visibility = !_showGngOperation && section == "Activity" ? Visibility.Visible : Visibility.Collapsed;
        GngOperationHost.Visibility = _showGngOperation ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Request(SetupWorkspaceAction action, object? sender = null, string? key = null) =>
        Requested?.Invoke(this, new(action, key ?? ((sender as FrameworkElement)?.DataContext as SetupWorkspaceApplication)?.Key));
    private void Discover_Click(object sender, RoutedEventArgs e) => Request(SetupWorkspaceAction.Discover);
    private void Change_Click(object sender, RoutedEventArgs e) => Request(SetupWorkspaceAction.ChangeLocation, sender);
    private void Manage_Click(object sender, RoutedEventArgs e) => Request(SetupWorkspaceAction.Manage, sender);
    private void GngLocation_Click(object sender, RoutedEventArgs e) => Request(SetupWorkspaceAction.ChangeLocation, key: "EuroScope (GNG Pack)");
    private void Gng_Click(object sender, RoutedEventArgs e) => Request(_gngPrimaryAction,
        key: _gngPrimaryAction == SetupWorkspaceAction.ChangeLocation ? "EuroScope (GNG Pack)" : null);
    private void GngImport_Click(object sender, RoutedEventArgs e) => Request(SetupWorkspaceAction.GngImport);
    private void GngFresh_Click(object sender, RoutedEventArgs e) => Request(SetupWorkspaceAction.GngFresh);
    private void GngCancel_Click(object sender, RoutedEventArgs e) => Request(SetupWorkspaceAction.GngCancel);
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
