using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

public partial class MainWindow
{
    private ActivityWorkspaceControl ActivityWorkspace => SetupWorkspace.Activity;
    internal void InitializeSyntheticWorkspaces()
    {
        WorkspaceLayoutReview.PopulateSetup(SetupWorkspace, UiFixture.Blank);
        SetupWorkspace.SectionChanged += () => PresentWorkspace("Setup");
        PresentWorkspace("Apps");
    }

    internal void ShowSyntheticWorkspace(string name)
    {
        if (name is "Activity" or "Preferences")
        {
            SetupWorkspace.ShowSection(name);
            PresentWorkspace("Setup");
        }
        else PresentWorkspace(name);
    }

    private void WorkspaceTab_Click(object sender, RoutedEventArgs e)
    {
        if (AppsWorkspace is null || SetupWorkspace is null || sender is not FrameworkElement { Tag: string page }) return;
        ShowSyntheticWorkspace(page);
    }

}

internal static class WorkspaceLayoutReview
{
    private static readonly string[] Sections = ["Applications", "Gng", "Profile", "Preferences", "Activity"];
    internal static string Run(string? output)
    {
        if (output is not null) Directory.CreateDirectory(output);
        int count = 0;
        UiFixture.Scale = 1;
        try
        {
            foreach (bool dark in new[] { true, false })
            foreach (double scale in new[] { 1d, 1.25d, 1.5d })
            foreach (bool minimum in new[] { false, true })
            foreach (bool empty in new[] { false, true })
            {
                if (UiFixture.Dark != dark) UiFixture.ToggleTheme();
                UiFixture.SetTextScale(scale);
                var setup = new SetupWorkspaceControl();
                PopulateSetup(setup, empty);
                var window = Host(setup, minimum);
                try
                {
                    foreach (string section in Sections)
                    {
                        setup.ShowSection(section);
                        LayoutValidation.CheckWindowContent(window);
                        CheckControlLayout(setup);
                        CheckSetupSection(setup, section, empty);
                        if (section == "Preferences")
                        {
                            CheckPreferences(setup.Preferences);
                            CheckPreferenceScrolling(window, setup.Preferences);
                        }
                        if (section == "Profile") CheckControllerEditor(window, setup, empty);
                        if (output is not null && !empty && (scale == 1 && !minimum || scale == 1.5 && minimum))
                            LayoutSnapshots.SaveWorkspace(window, Path.Combine(output,
                                $"{(dark ? "dark" : "light")}-setup-{section.ToLowerInvariant()}-{scale * 100:0}-{(minimum ? "minimum" : "default")}.png"));
                        count++;
                    }
                    setup.ShowSection("Applications");
                    CheckLocationChoices(window, setup);
                    CheckRequests(setup);
                    CheckPluginChoices(window, setup, empty);
                    count += 6;
                }
                finally { window.Close(); }

                var activity = new ActivityWorkspaceControl();
                PopulateActivity(activity, empty);
                window = Host(activity, minimum);
                try
                {
                    LayoutValidation.CheckWindowContent(window);
                    CheckControlLayout(activity);
                    CheckHistory(activity, empty);
                    if (output is not null && !empty && (scale == 1 && !minimum || scale == 1.5 && minimum))
                        LayoutSnapshots.SaveWorkspace(window, Path.Combine(output,
                            $"{(dark ? "dark" : "light")}-activity-{scale * 100:0}-{(minimum ? "minimum" : "default")}.png"));
                    count++;
                }
                finally { window.Close(); }
            }

            foreach (bool dark in new[] { true, false })
            foreach (bool minimum in new[] { false, true })
            {
                if (UiFixture.Dark != dark) UiFixture.ToggleTheme();
                UiFixture.SetTextScale(minimum ? 1.5 : 1);
                UiFixture.Minimum = minimum;
                UiFixture.Blank = false;
                var main = new MainWindow();
                try
                {
                    LayoutValidation.CheckWindowContent(main);
                    CheckMainNavigation(main);
                    CheckApplicationVisibility(main, output);
                    foreach (string section in Sections.Prepend("Apps"))
                    {
                        if (section == "Apps") main.ShowSyntheticWorkspace("Apps");
                        else
                        {
                            main.ShowSyntheticWorkspace("Setup");
                            main.SetupWorkspace.ShowSection(section);
                        }
                        LayoutValidation.CheckWindowContent(main);
                        Equal(section == "Apps" ? Visibility.Visible : Visibility.Collapsed, Required<FrameworkElement>(main, "AppsWorkspace").Visibility);
                        Equal(section == "Apps" ? Visibility.Collapsed : Visibility.Visible, Required<FrameworkElement>(main, "SetupWorkspace").Visibility);
                        Equal(section == "Apps" ? Visibility.Visible : Visibility.Collapsed, Required<Button>(main, "CheckButton").Visibility);
                        Equal(section == "Apps" ? Visibility.Visible : Visibility.Collapsed, Required<FrameworkElement>(main, "AppBulkActions").Visibility);
                        Equal(section == "Apps", Required<RadioButton>(main, "AppsTab").IsChecked == true);
                        Equal(section != "Apps", Required<RadioButton>(main, "SetupTab").IsChecked == true);
                        if (section != "Apps") CheckSetupSection(main.SetupWorkspace, section, false);
                        if (output is not null)
                            LayoutSnapshots.SaveWorkspace(main, Path.Combine(output,
                                $"{(dark ? "dark" : "light")}-main-{section.ToLowerInvariant()}-{(minimum ? "minimum-150" : "default-100")}.png"));
                        if (output is not null && section == "Profile")
                        {
                            main.SetupWorkspace.ControllerProfile.ShowResultLines(SyntheticControllerResults());
                            LayoutValidation.CheckWindowContent(main);
                            LayoutSnapshots.SaveWorkspace(main, Path.Combine(output,
                                $"{(dark ? "dark" : "light")}-controller-mixed-result-{(minimum ? "minimum-150" : "default-100")}.png"));
                            main.SetupWorkspace.ControllerProfile.ShowResultLines([]);
                        }
                        if (output is not null && section == "Preferences")
                        {
                            Required<Expander>(main.SetupWorkspace.Preferences, "NotUsedExpander").IsExpanded = true;
                            LayoutValidation.CheckWindowContent(main);
                            LayoutSnapshots.SaveWorkspace(main, Path.Combine(output,
                                $"{(dark ? "dark" : "light")}-preferences-optional-{(minimum ? "minimum-150" : "default-100")}.png"));
                            Required<Expander>(main.SetupWorkspace.Preferences, "NotUsedExpander").IsExpanded = false;
                        }
                        if (output is not null && section == "Applications")
                        {
                            var items = Required<ItemsControl>(main.SetupWorkspace, "ApplicationsList").Items.Cast<SetupWorkspaceApplication>().ToArray();
                            main.SetupWorkspace.SetApplications(items.Select(row => row.Key == "SAAMAN" ? row with
                            {
                                CanManage = false, Feedback = new("Downloading SAAMAN…", Busy: true, Percent: 43, CanCancel: true)
                            } : row));
                            main.SetupWorkspace.SetActionsEnabled(false);
                            LayoutValidation.CheckWindowContent(main);
                            RowControl<Button>(main.SetupWorkspace, "SAAMAN", "PluginCancelButton").BringIntoView();
                            LayoutValidation.CheckWindowContent(main);
                            LayoutSnapshots.SaveWorkspace(main, Path.Combine(output,
                                $"{(dark ? "dark" : "light")}-plugins-downloading-{(minimum ? "minimum-150" : "default-100")}.png"));
                            main.SetupWorkspace.SetActionsEnabled(true);
                            main.SetupWorkspace.SetApplications(items);
                        }
                        Equal(false, main.IsVisible);
                        count++;
                    }

                }
                finally { main.Close(); }
            }
            count += CheckFirstSetup(output);
        }
        finally { UiFixture.SetTextScale(1); UiFixture.Minimum = false; UiFixture.Blank = false; }
        return $"PASS: {count} workspace layouts and local interaction checks; production controls, synthetic data, no windows shown.";
    }

    internal static void PopulateSetup(SetupWorkspaceControl setup, bool empty)
    {
        PopulateActivity(setup.Activity, empty);
        setup.Preferences.SetPreferences(SyntheticPreferences());
        UiFixture.PopulateController(setup.ControllerProfile, empty);
        setup.SetApplications(new[]
        {
            App("EuroScope", "EuroScope", @"C:\Synthetic\EuroScope\EuroScope.exe"),
            App("TrackAudio", "TrackAudio", @"C:\Synthetic\TrackAudio\TrackAudio.exe"),
            App("VACS", "VACS", @"C:\Synthetic\VACS\VACS.exe"),
            App("vATIS", "vATIS", @"C:\Synthetic\vATIS\vATIS.exe"),
            App("VatEFS", "VatEFS", @"C:\Synthetic\VatEFS"),
            App("SAAMAN", "SAAMAN", @"C:\Synthetic\EuroScope\ESAA\Plugins\SAAMAN\SAAMAN.dll")
        });
        setup.SetSummary(empty ? "" : @"C:\Synthetic\EuroScope", empty ? "No package folder chosen." : "Installed package 2610/01 rev.3.",
            empty ? "Not configured" : "Controller details saved. Review before applying changes.", !empty, !empty);
        SetupWorkspaceApplication App(string key, string name, string path) =>
            new(key, name, empty ? "" : path, key is "VatEFS" or "SAAMAN" ? empty ? "Off" : "Enabled" : empty ? "Not chosen" : "Location saved",
                key == "SAAMAN" ? empty ? "" : "Update" : empty ? "Install…" : key == "EuroScope" ? "Manage…" : "Updates…",
                Plugin: key is "VatEFS" or "SAAMAN" ? new(!empty, key == "SAAMAN" ? "Use in APP / ACC" : "Use in EuroScope",
                    key == "SAAMAN" ? "Load SAAMAN first in the selected APP / ACC profile. Turning it off restores MAESTRO." : "Add VatEFS to EuroScope profiles. Turning it off keeps the installed application.") : null);
    }

    internal static void PopulateActivity(ActivityWorkspaceControl activity, bool empty)
    {
        activity.SelectionChanged += entry => activity.SetRecoveryState(entry.RecoveryFolder is not null,
            entry.RecoveryFolder is not null && (entry.Application == ActivityApplication.Gng ||
                entry.Application == ActivityApplication.Saaman && entry.PreviousVersion is not null && entry.Operation is ActivityOperation.Install or ActivityOperation.Update), entry.Guidance);
        activity.SetHistory(empty ? [] : Entries(), "Recent changes on this PC.");
    }

    private static ActivityEntry[] Entries() =>
    [
        new(Guid.Parse("00000000-0000-0000-0000-000000000001"), DateTimeOffset.Parse("2026-10-21T12:00:00Z"), ActivityApplication.Gng,
            ActivityOperation.Update, ActivityOutcome.Completed, "2610/01 rev.3", @"C:\Synthetic\Recovery\GNG", @"C:\Synthetic\EuroScope", PreviousVersion: "2610/01 rev.2"),
        new(Guid.Parse("00000000-0000-0000-0000-000000000002"), DateTimeOffset.Parse("2026-10-21T11:00:00Z"), ActivityApplication.Saaman,
            ActivityOperation.Update, ActivityOutcome.Completed, "1.3.37", @"C:\Synthetic\Recovery\SAAMAN", PreviousVersion: "1.3.36"),
        new(Guid.Parse("00000000-0000-0000-0000-000000000003"), DateTimeOffset.Parse("2026-10-21T10:00:00Z"), ActivityApplication.TrackAudio,
            ActivityOperation.Update, ActivityOutcome.Cancelled, "1.4.0")
    ];

    private static Window Host(UserControl control, bool minimum) => new()
    {
        Content = control, WindowStyle = WindowStyle.None,
        Width = minimum ? 744 : 864, Height = minimum ? 290 : 410,
        Background = (Brush)Application.Current.Resources["AppBg"]
    };

    private static void CheckSetupSection(SetupWorkspaceControl setup, string section, bool empty)
    {
        foreach (string name in Sections)
        {
            Equal(name == section ? Visibility.Visible : Visibility.Collapsed, Required<FrameworkElement>(setup, name + "Panel").Visibility);
            var tab = Required<RadioButton>(setup, name + "Tab");
            Equal(name == section, tab.IsChecked == true);
            Equal(true, Presented(tab) && tab.IsEnabled && tab.Focusable && tab.IsTabStop);
        }
        Equal(section is "Applications" or "Gng" ? Visibility.Visible : Visibility.Collapsed,
            Required<FrameworkElement>(setup, "SetupSectionScroll").Visibility);
        Equal(true, ReferenceEquals(setup, setup.Preferences.Parent is FrameworkElement p ? FindSetupAncestor(p) : null));
        Equal(true, !LayoutValidation.Descendants(setup).OfType<Button>().Any(button => Equals(button.Content, "⋯")));
        var list = Required<ItemsControl>(setup, "ApplicationsList");
        Equal(6, list.Items.Count);
        foreach (var row in list.Items.Cast<SetupWorkspaceApplication>())
            Equal(empty, string.IsNullOrWhiteSpace(row.Path));
        Equal(true, Required<TextBox>(setup, "GngLocation").IsReadOnly);
        Equal(empty ? Visibility.Collapsed : Visibility.Visible, Required<FrameworkElement>(setup, "GngMaintenanceActions").Visibility);
    }

    private static void CheckLocationChoices(Window window, SetupWorkspaceControl setup)
    {
        int accepted = 0, cancelled = 0;
        setup.LocationAccepted += (_, _) => accepted++;
        setup.LocationCancelled += (_, _) => cancelled++;
        foreach (var state in new[] { PathValidationStatus.Verified, PathValidationStatus.Warning, PathValidationStatus.Invalid })
        {
            setup.ReviewLocation("VACS", @"C:\Synthetic\Old\VACS.exe", new(ConfiguredPathKind.VacsExecutable,
                @"C:\Synthetic\New\VACS.exe", state, state == PathValidationStatus.Verified ? "VACS executable found." : "Synthetic location needs review."));
            LayoutValidation.CheckWindowContent(window);
            var save = Required<Button>(setup, "SaveLocationButton");
            var warning = Required<CheckBox>(setup, "AcceptLocationWarning");
            Equal(state == PathValidationStatus.Verified, save.IsEnabled);
            Equal(state == PathValidationStatus.Warning ? Visibility.Visible : Visibility.Collapsed, warning.Visibility);
            if (state == PathValidationStatus.Warning) { warning.IsChecked = true; Equal(true, save.IsEnabled); }
            int before = accepted;
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Equal(before + (save.IsEnabled ? 1 : 0), accepted);
            Equal(true, Required<TextBox>(setup, "ProposedLocation").IsReadOnly);
        }
        var cancel = LayoutValidation.Descendants(Required<FrameworkElement>(setup, "LocationReview")).OfType<Button>().Single(button => button.Content?.ToString() == "Cancel");
        cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Equal(1, cancelled);
        Equal(Visibility.Collapsed, Required<FrameworkElement>(setup, "LocationReview").Visibility);
    }

    private static void CheckRequests(SetupWorkspaceControl setup)
    {
        SetupWorkspaceRequest? request = null;
        setup.Requested += (_, value) => request = value;
        var change = RowControl<Button>(setup, "EuroScope", "LocationButton");
        Equal(((SetupWorkspaceApplication)change.DataContext).LocationAction, change.Content?.ToString());
        change.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Equal(SetupWorkspaceAction.ChangeLocation, request!.Action);
        Equal("EuroScope", request.Application);
        foreach (var section in new[] { "Preferences", "Activity" })
        {
            var tab = Required<RadioButton>(setup, section + "Tab");
            Equal(true, tab.IsTabStop);
            tab.IsChecked = true;
            Equal(section, setup.SelectedSection);
            Equal(SetupWorkspaceAction.ChangeLocation, request.Action);
        }
        setup.SetActionsEnabled(false);
        Equal(false, setup.ActionsEnabled);
        Equal(false, change.IsEnabled);
        foreach (string section in Sections) Equal(true, Required<RadioButton>(setup, section + "Tab").IsEnabled);
        setup.SetActionsEnabled(true);

    }

    private static void CheckPluginChoices(Window window, SetupWorkspaceControl setup, bool empty)
    {
        setup.ShowSection("Applications");
        var list = Required<ItemsControl>(setup, "ApplicationsList");
        var original = list.Items.Cast<SetupWorkspaceApplication>().ToArray();
        var received = new List<SetupWorkspaceRequest>();
        EventHandler<SetupWorkspaceRequest> observe = (_, request) => received.Add(request);
        setup.Requested += observe;
        try
        {
            setup.SetApplications(original);
            LayoutValidation.CheckWindowContent(window);
            Equal(0, received.Count);
            var itemsSource = list.ItemsSource;
            var originalChoice = RowControl<CheckBox>(setup, "SAAMAN", "PluginChoice");
            setup.SetApplications(original.Select(row => row with { }));
            LayoutValidation.CheckWindowContent(window);
            Equal(true, ReferenceEquals(itemsSource, list.ItemsSource));
            Equal(true, ReferenceEquals(originalChoice, RowControl<CheckBox>(setup, "SAAMAN", "PluginChoice")));
            Equal(0, received.Count);
            foreach (string key in new[] { "VatEFS", "SAAMAN" })
            {
                var choice = RowControl<CheckBox>(setup, key, "PluginChoice");
                Equal(true, Presented(choice) && choice.IsTabStop && choice.Focusable);
                Equal(true, AutomationProperties.GetName(choice).Contains(key, StringComparison.Ordinal));
                Equal(false, string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(choice)));
                var info = RowControl<Button>(setup, key, "PluginInfoButton");
                Equal(true, info.Focusable && info.IsTabStop && info.ToolTip is ToolTip);
                Equal(true, AutomationProperties.GetName(info).Contains(key, StringComparison.Ordinal));
                Equal(false, string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(info)));
                Equal(true, info.Style.Setters.OfType<EventSetter>().Any(setter => setter.Event == UIElement.GotKeyboardFocusEvent));
                Equal(!empty, choice.IsChecked == true);
                choice.SetCurrentValue(CheckBox.IsCheckedProperty, empty);
                Equal(0, received.Count); // Populating/changing the visual state is not an operation.
                choice.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Equal(1, received.Count);
                Equal(SetupWorkspaceAction.TogglePlugin, received[0].Action);
                Equal(key, received[0].Application);
                Equal(empty, received[0].Enabled == true);
                Equal(!empty, choice.IsChecked == true); // A rejected/no-op request keeps the confirmed choice.
                setup.SetApplications(original.Select(row => row with { }));
                LayoutValidation.CheckWindowContent(window);
                Equal(true, ReferenceEquals(choice, RowControl<CheckBox>(setup, key, "PluginChoice")));
                Equal(!empty, choice.IsChecked == true);
                received.Clear();
                Equal(!empty, original.Single(row => row.Key == key).Plugin!.Enabled); // One-way binding.
            }

            var normal = original.Single(row => row.Key == "SAAMAN");
            var cases = new[]
            {
                normal with { ManageText = "", Plugin = normal.Plugin! with { Enabled = false } },
                normal with { Plugin = normal.Plugin! with { CanChange = false, BlockReason = "Close EuroScope before changing this plugin." } },
                normal with { CanManage = false, Plugin = normal.Plugin! with { CanChange = false }, Feedback = new("Downloading SAAMAN…", Busy: true, Percent: 43, CanCancel: true) },
                normal with { CanManage = false, Plugin = normal.Plugin! with { CanChange = false }, Feedback = new("Applying SAAMAN…", Busy: true) },
                normal with { Feedback = new("The plugin was updated, but Launchpad could not save its location. Retry saving to keep this choice.", Warning: true, CanRetrySave: true) },
                normal with { Feedback = new("SAAMAN needs the Microsoft WebView2 runtime before it can be used in EuroScope.", RuntimeRequired: true) }
            };
            foreach (var row in cases)
            {
                setup.SetApplications(original.Select(item => item.Key == row.Key ? row : item));
                LayoutValidation.CheckWindowContent(window);
                Equal(0, received.Count);
                var choice = RowControl<CheckBox>(setup, "SAAMAN", "PluginChoice");
                var manage = RowControl<Button>(setup, "SAAMAN", "ManageApplicationButton");
                Equal(row.Plugin!.CanChange, choice.IsEnabled);
                if (!choice.IsEnabled)
                {
                    choice.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    Equal(0, received.Count);
                }
                Equal(row.CanManage, manage.IsEnabled);
                Equal(string.IsNullOrEmpty(row.ManageText) ? Visibility.Collapsed : Visibility.Visible, manage.Visibility);
                Equal(row.Feedback is null ? Visibility.Collapsed : Visibility.Visible, RowControl<FrameworkElement>(setup, "SAAMAN", "PluginFeedbackPanel").Visibility);
                var progress = RowControl<ProgressBar>(setup, "SAAMAN", "PluginProgress");
                Equal(System.Windows.Data.BindingMode.OneWay, System.Windows.Data.BindingOperations.GetBinding(progress, ProgressBar.ValueProperty)!.Mode);
                Equal(row.Feedback?.Busy == true ? Visibility.Visible : Visibility.Collapsed, progress.Visibility);
                if (row.Feedback?.Busy == true)
                {
                    Equal(row.Feedback.Percent is null, progress.IsIndeterminate);
                    if (row.Feedback.Percent is double percent) Equal(percent, progress.Value);
                }
                foreach (var (name, action, visible) in new[]
                {
                    ("PluginCancelButton", SetupWorkspaceAction.PluginCancel, row.Feedback?.CanCancel == true),
                    ("PluginRetrySaveButton", SetupWorkspaceAction.PluginRetrySave, row.Feedback?.CanRetrySave == true),
                    ("PluginRuntimeButton", SetupWorkspaceAction.PluginRuntime, row.Feedback?.RuntimeRequired == true)
                })
                {
                    var button = RowControl<Button>(setup, "SAAMAN", name);
                    Equal(visible ? Visibility.Visible : Visibility.Collapsed, button.Visibility);
                    if (!visible) continue;
                    setup.SetActionsEnabled(false);
                    LayoutValidation.CheckWindowContent(window);
                    Equal(false, choice.IsEnabled);
                    Equal(false, manage.IsEnabled);
                    Equal(action == SetupWorkspaceAction.PluginCancel, button.IsEnabled);
                    if (action != SetupWorkspaceAction.PluginCancel) setup.SetActionsEnabled(true);
                    button.BringIntoView(); LayoutValidation.CheckWindowContent(window);
                    var scroll = Required<ScrollViewer>(setup, "SetupSectionScroll");
                    var bounds = button.TransformToAncestor(scroll).TransformBounds(new Rect(button.RenderSize));
                    Equal(true, bounds.Left >= -1 && bounds.Right <= scroll.ViewportWidth + 1 && bounds.Top >= -1 && bounds.Bottom <= scroll.ViewportHeight + 1);
                    CheckButtonCaption(button);
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Equal(1, received.Count);
                    Equal(action, received[0].Action);
                    Equal("SAAMAN", received[0].Application);
                    received.Clear();
                    setup.SetActionsEnabled(true);
                }
                var text = RowControl<TextBlock>(setup, "SAAMAN", "PluginFeedbackText");
                if (row.Feedback is not null)
                    Equal(((SolidColorBrush)setup.FindResource(row.Feedback.Warning ? "WarningFg" : "SecondaryText")).Color, ((SolidColorBrush)text.Foreground).Color);
            }
        }
        finally
        {
            setup.Requested -= observe;
            setup.SetActionsEnabled(true);
            setup.SetApplications(original);
            Required<ScrollViewer>(setup, "SetupSectionScroll").ScrollToTop();
            LayoutValidation.CheckWindowContent(window);
        }
    }

    private static T RowControl<T>(SetupWorkspaceControl setup, string key, string name) where T : FrameworkElement =>
        LayoutValidation.Descendants(setup).OfType<T>().Single(control => control.Name == name && control.DataContext is SetupWorkspaceApplication row && row.Key == key);

    private static int CheckFirstSetup(string? output)
    {
        int count = 0;
        foreach (bool dark in new[] { true, false })
        foreach (double scale in new[] { 1d, 1.25d, 1.5d })
        foreach (bool minimum in new[] { false, true })
        foreach (bool partial in new[] { false, true })
        {
            if (UiFixture.Dark != dark) UiFixture.ToggleTheme();
            UiFixture.SetTextScale(scale);
            UiFixture.Minimum = minimum;
            UiFixture.Blank = true;
            var main = new MainWindow();
            try
            {
                var setup = main.SetupWorkspace;
                var list = Required<ItemsControl>(setup, "ApplicationsList");
                var rows = list.Items.Cast<SetupWorkspaceApplication>().Select(row =>
                {
                    bool installed = partial && row.Key is "EuroScope" or "TrackAudio";
                    return row with
                    {
                        Path = installed ? $@"C:\Synthetic\{row.Key}\{row.Key}.exe" : "",
                        Status = row.Plugin is not null ? "Off" : installed ? "Installed" : "Not installed",
                        ManageText = row.Key == "SAAMAN" ? "" : installed ? "Updates…" : "Install…",
                        Plugin = row.Plugin is null ? null : row.Plugin with
                        { Enabled = false, CanChange = false, BlockReason = "Set up the Swedish GNG folder first.", NeedsGng = true }
                    };
                }).ToArray();
                setup.SetApplications(rows);
                setup.Preferences.SetPreferences(new AppSettings());
                foreach (string field in new[] { "NameBox", "CidBox", "ObsBox" }) Required<TextBox>(setup.ControllerProfile, field).Clear();
                foreach (string field in new[] { "PasswordBox", "HoppieBox" }) Required<PasswordBox>(setup.ControllerProfile, field).Clear();
                Required<ComboBox>(setup.ControllerProfile, "RatingBox").SelectedIndex = 0;
                main.ShowSyntheticWorkspace("Setup");
                SetupWorkspaceRequest? requested = null;
                setup.Requested += (_, request) => requested = request;
                var guideSettings = new AppSettings { SetupGuideActive = true, SetupGuideSection = "Applications" };
                foreach (string section in Sections)
                {
                    setup.ShowSection(section);
                    SetupGuide.RememberSection(guideSettings, section);
                    var guide = SetupGuide.Present(guideSettings, section)!;
                    setup.SetGuide(guide);
                    LayoutValidation.CheckWindowContent(main);
                    CheckControlLayout(setup);
                    var strip = Required<FrameworkElement>(setup, "SetupGuide");
                    Equal(Visibility.Visible, strip.Visibility);
                    Equal(Visibility.Collapsed, Required<TextBlock>(setup, "SetupNotice").Visibility);
                    Equal(guide.Heading, Required<TextBlock>(setup, "GuideHeading").Text);
                    Equal(guide.Message, Required<TextBlock>(setup, "GuideMessage").Text);
                    var next = Required<Button>(setup, "GuideNextButton");
                    var later = Required<Button>(setup, "GuideLaterButton");
                    Equal(guide.NextLabel, next.Content?.ToString());
                    foreach (var (button, action) in new[] { (next, SetupWorkspaceAction.GuideNext), (later, SetupWorkspaceAction.GuideLater) })
                    {
                        var bounds = button.TransformToAncestor(setup).TransformBounds(new Rect(button.RenderSize));
                        Equal(true, bounds.Top >= 0 && bounds.Bottom <= setup.ActualHeight && bounds.Left >= 0 && bounds.Right <= setup.ActualWidth);
                        Equal(true, button.IsTabStop && button.Focusable && button.IsEnabled);
                        int windows = Application.Current.Windows.Count;
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Equal(action, requested!.Action);
                        Equal(windows, Application.Current.Windows.Count);
                        Equal(Visibility.Visible, strip.Visibility); // The owner decides whether to advance/finish.
                    }
                    if (section == "Applications")
                    {
                        foreach (var row in rows)
                        {
                            Equal(string.IsNullOrEmpty(row.Path) ? "Use existing…" : "Location…", RowControl<Button>(setup, row.Key, "LocationButton").Content?.ToString());
                            var name = RowControl<Button>(setup, row.Key, "ApplicationNameButton");
                            Equal(true, name.Focusable && name.IsTabStop && name.ToolTip is ToolTip);
                            Equal(ApplicationDescriptions.For(row.Key), AutomationProperties.GetHelpText(name));
                        }
                    }
                    if (section == "Profile")
                    {
                        Equal(((SolidColorBrush)setup.FindResource("InputBg")).Color,
                            ((SolidColorBrush)Required<Border>(setup.ControllerProfile, "SyncBanner").Background).Color);
                        Equal(((SolidColorBrush)setup.FindResource("SecondaryText")).Color,
                            ((SolidColorBrush)Required<TextBlock>(setup.ControllerProfile, "SyncText").Foreground).Color);
                        Equal(true, Required<ScrollViewer>(setup.ControllerProfile, "ProfileFormScroll").ViewportHeight > 0);
                        foreach (string name in new[] { "SaveProfileButton", "PreviewButton" })
                        {
                            var button = Required<Button>(setup.ControllerProfile, name);
                            var bounds = button.TransformToAncestor(setup).TransformBounds(new Rect(button.RenderSize));
                            Equal(true, bounds.Bottom <= setup.ActualHeight + 1 && bounds.Left >= -1);
                        }
                    }
                    if (section == "Gng")
                    {
                        Equal("Use existing folder…", Required<Button>(setup, "GngLocationButton").Content?.ToString());
                        Equal("Install Swedish GNG…", Required<Button>(setup, "GngPackageButton").Content?.ToString());
                        Equal(Visibility.Visible, Required<TextBlock>(setup, "GngDestinationHint").Visibility);
                        Equal(Visibility.Collapsed, Required<FrameworkElement>(setup, "GngMaintenanceActions").Visibility);
                        Equal("New installs use %APPDATA%\\EuroScope.", Required<TextBlock>(setup, "GngDestinationHint").Text);
                    }
                    if (section == "Preferences")
                    {
                        CheckPreferenceScrolling(main, setup.Preferences);
                        Equal("Continue: Controller profile", next.Content?.ToString());
                    }
                    if (output is not null && (scale == 1 && !minimum || scale == 1.5 && minimum))
                        LayoutSnapshots.SaveWorkspace(main, Path.Combine(output,
                            $"{(dark ? "dark" : "light")}-first-{(partial ? "partial" : "blank")}-{section.ToLowerInvariant()}-{(minimum ? "minimum-150" : "default-100")}.png"));
                    setup.SetActionsEnabled(false);
                    Equal(false, next.IsEnabled);
                    Equal(false, later.IsEnabled);
                    setup.SetActionsEnabled(true);
                    count++;
                }
                setup.ShowSection("Applications");
                LayoutValidation.CheckWindowContent(main);
                foreach (string key in new[] { "VatEFS", "SAAMAN" })
                {
                    var gng = RowControl<Button>(setup, key, "PluginGngButton");
                    Equal(Visibility.Visible, gng.Visibility);
                    Equal(false, RowControl<CheckBox>(setup, key, "PluginChoice").IsEnabled);
                    requested = null;
                    int windows = Application.Current.Windows.Count;
                    gng.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Equal("Gng", setup.SelectedSection);
                    Equal(true, requested is null);
                    Equal(windows, Application.Current.Windows.Count);
                    setup.ShowSection("Applications");
                }
                setup.ShowNotice("Synthetic application location saved.");
                Equal(Visibility.Visible, Required<TextBlock>(setup, "SetupNotice").Visibility);
                setup.SetGuide(null);
                Equal(Visibility.Collapsed, Required<FrameworkElement>(setup, "SetupGuide").Visibility);
                Equal("Synthetic application location saved.", Required<TextBlock>(setup, "SetupNotice").Text);
                Equal(false, main.IsVisible);
            }
            finally { main.Close(); }
        }
        return count;
    }

    private static void CheckMainNavigation(MainWindow main)
    {
        var help = Required<Button>(main, "HelpButton");
        Equal("?", help.Content?.ToString());
        Equal("Help, reporting and privacy", AutomationProperties.GetName(help));
        Equal(true, help.IsTabStop && help.ActualWidth >= 28 && help.ActualHeight >= 28);
        Equal(false, string.IsNullOrWhiteSpace(help.ToolTip?.ToString()));
        foreach (var name in new[] { "SettingsButton", "PinButton", "DensityToggleButton", "SetupButton", "BackToAppsButton", "ActivityButton", "ApplicationDetails" })
            Equal(true, main.FindName(name) is null);
        CheckButtonCaption(help);
        var apps = Required<RadioButton>(main, "AppsTab");
        var setup = Required<RadioButton>(main, "SetupTab");
        foreach (var tab in new[] { apps, setup })
        {
            Equal(Visibility.Visible, tab.Visibility);
            Equal(true, tab.Focusable && tab.IsTabStop && tab.ActualHeight >= 28);
            Equal(false, string.IsNullOrWhiteSpace(AutomationProperties.GetName(tab)));
        }
        Equal(true, apps.IsChecked == true && setup.IsChecked == false);
        setup.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Equal(Visibility.Visible, Required<FrameworkElement>(main, "SetupWorkspace").Visibility);
        Required<RadioButton>(main.SetupWorkspace, "ActivityTab").IsChecked = true;
        Equal(Visibility.Visible, main.SetupWorkspace.Activity.Visibility);
        Equal(Visibility.Visible, main.SetupWorkspace.Visibility);
        Equal(true, setup.IsChecked == true && apps.IsChecked == false);
        apps.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Equal(Visibility.Visible, Required<FrameworkElement>(main, "AppsWorkspace").Visibility);
        var notice = Required<TextBlock>(main, "ActivityNotice");
        notice.Text = "The latest outcome could not be saved. Keep the recovery folder.";
        main.ShowSyntheticWorkspace("Apps"); LayoutValidation.CheckWindowContent(main);
        var noticeButton = Required<Button>(main, "ActivityNoticeButton");
        Equal(Visibility.Visible, noticeButton.Visibility); Equal(true, noticeButton.IsTabStop);
        noticeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Equal(Visibility.Visible, main.SetupWorkspace.Activity.Visibility);
        Equal(Visibility.Visible, main.SetupWorkspace.Visibility);
        notice.Text = ""; main.ShowSyntheticWorkspace("Apps"); LayoutValidation.CheckWindowContent(main);
        Equal(Visibility.Collapsed, noticeButton.Visibility);
    }

    private static void CheckApplicationVisibility(MainWindow main, string? output)
    {
        var rows = main.SyntheticRows.ToArray();
        var oldFlags = rows.Select(row => row.IsNotUsed).ToArray();
        var preferences = main.SetupWorkspace.Preferences;
        var settings = SyntheticPreferences();
        settings.NotUsedApplications = "";
        preferences.SetPreferences(settings);
        var choices = Required<Panel>(preferences, "NotUsedChoices").Children.OfType<CheckBox>().ToArray();
        var list = Required<ItemsControl>(main, "AppList");
        var setupList = Required<ItemsControl>(main.SetupWorkspace, "ApplicationsList");
        var originalSetupRows = setupList.Items.Cast<SetupWorkspaceApplication>().ToArray();
        SetupWorkspaceRequest? setupRequest = null;
        EventHandler<SetupWorkspaceRequest> observeSetup = (_, request) => setupRequest = request;
        void Apply(AppSettings draft)
        {
            foreach (var row in rows) row.IsNotUsed = ApplicationPreferences.IsNotUsed(draft, row.AppName);
            main.RefreshSyntheticApplicationRows();
        }
        void Toggle(string key, bool unused)
        {
            var choice = choices.Single(item => Equals(item.Tag, key));
            choice.IsChecked = unused;
            choice.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            LayoutValidation.CheckWindowContent(main);
        }
        string[] VisibleNames() => list.Items.Cast<CheckResult>().Select(row => row.AppName).ToArray();
        string[] expected = ["EuroScope", "EuroScope (GNG Pack)", "VatEFS", "SAAMAN", "TrackAudio", "VACS", "vATIS", "VATIRIS", "Sweden FIR Launchpad"];
        preferences.PreferencesChanged += Apply;
        main.SetupWorkspace.Requested += observeSetup;
        try
        {
            main.ShowSyntheticWorkspace("Apps");
            Equal(string.Join("|", expected), string.Join("|", VisibleNames()));
            foreach (var key in new[] { "TrackAudio", "VACS", "vATIS", "VatEFS", "SAAMAN" })
            {
                Toggle(key, true);
                Equal(false, VisibleNames().Contains(key));
                Equal(6, setupList.Items.Count);
                Equal(true, setupList.Items.Cast<SetupWorkspaceApplication>().Any(row => row.Key == key));
                if (key != "SAAMAN")
                {
                    main.SetupWorkspace.SetApplications(originalSetupRows.Select(row => row.Key == key
                        ? row with { NotUsed = true, ManageText = "Show in Apps" } : row));
                    main.ShowSyntheticWorkspace("Setup");
                    main.SetupWorkspace.ShowSection("Applications");
                    LayoutValidation.CheckWindowContent(main);
                    var restore = RowControl<Button>(main.SetupWorkspace, key, "ManageApplicationButton");
                    restore.BringIntoView(); LayoutValidation.CheckWindowContent(main);
                    CheckButtonCaption(restore);
                    Equal(true, restore.IsEnabled && Presented(restore));
                    restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Equal(SetupWorkspaceAction.Manage, setupRequest!.Action);
                    Equal(key, setupRequest.Application);
                    main.SetupWorkspace.SetApplications(originalSetupRows);
                    main.ShowSyntheticWorkspace("Apps");
                }
                Toggle(key, false);
                Equal(string.Join("|", expected), string.Join("|", VisibleNames()));
            }
            Toggle("EuroScope", true);
            Equal(false, VisibleNames().Contains("EuroScope"));
            Equal("EuroScope (GNG Pack)|VatEFS|SAAMAN", string.Join("|", VisibleNames().Take(3)));
            Toggle("EuroScope (GNG Pack)", true);
            Equal("VatEFS|SAAMAN", string.Join("|", VisibleNames().Take(2)));
            foreach (string key in new[] { "VatEFS", "SAAMAN" })
            {
                var row = rows.Single(item => item.AppName == key);
                var label = LayoutValidation.Descendants(list).OfType<StackPanel>().Single(item => item.Name == "ApplicationLabel" && ReferenceEquals(item.DataContext, row));
                Equal(12d, label.Margin.Left);
                var status = LayoutValidation.Descendants(list).OfType<TextBox>().Single(item => item.Name == "RowStatusMessage" && ReferenceEquals(item.DataContext, row));
                Equal(true, Presented(status) && status.IsTabStop);
            }
            var beforeTheme = VisibleNames();
            UiFixture.ToggleTheme();
            LayoutValidation.CheckWindowContent(main);
            Equal(string.Join("|", beforeTheme), string.Join("|", VisibleNames()));
            UiFixture.ToggleTheme();
            LayoutValidation.CheckWindowContent(main);
            Equal(string.Join("|", beforeTheme), string.Join("|", VisibleNames()));
            if (output is not null)
                LayoutSnapshots.SaveWorkspace(main, Path.Combine(output,
                    $"{(UiFixture.Dark ? "dark" : "light")}-apps-parent-hidden-{(UiFixture.Minimum ? "minimum-150" : "default-100")}.png"));
            var launchpad = rows.Single(row => row.HasSelfUpdate);
            launchpad.IsNotUsed = true;
            main.RefreshSyntheticApplicationRows();
            Equal(true, VisibleNames().Contains(launchpad.AppName));
            Equal(true, rows.SequenceEqual(main.SyntheticRows));
            foreach (var visible in list.Items.Cast<CheckResult>()) Equal(true, rows.Any(row => ReferenceEquals(row, visible)));
        }
        finally
        {
            preferences.PreferencesChanged -= Apply;
            main.SetupWorkspace.Requested -= observeSetup;
            for (int index = 0; index < rows.Length; index++) rows[index].IsNotUsed = oldFlags[index];
            main.SetSyntheticApplicationRows(rows);
            main.SetupWorkspace.SetApplications(originalSetupRows);
            preferences.SetPreferences(SyntheticPreferences());
            LayoutValidation.CheckWindowContent(main);
        }
    }
    private static AppSettings SyntheticPreferences() => new()
    {
        EuroscopeDataPath = @"C:\Synthetic\EuroScope",
        SaamanDllPath = @"C:\Synthetic\EuroScope\ESAA\Plugins\SAAMAN\SAAMAN.dll",
        SaamanProfilePath = @"C:\Synthetic\EuroScope\ESAA APP ACC.prf",
        VatEfsPath = @"C:\Synthetic\VatEFS", PatchVatEfs = true,
        LastEuroscopeProfile = @"C:\Synthetic\EuroScope\ESAA APP ACC.prf",
        CheckOnStartup = true, TextScale = 1.25, CompactLayout = true,
        NotUsedApplications = "VATIRIS", PatchSaaman = true, SaamanProfileManaged = true
    };

    private static void CheckControllerEditor(Window window, SetupWorkspaceControl setup, bool empty)
    {
        var editor = setup.ControllerProfile;
        var name = Required<TextBox>(editor, "NameBox");
        var cid = Required<TextBox>(editor, "CidBox");
        var password = Required<PasswordBox>(editor, "PasswordBox");
        var preview = Required<Button>(editor, "PreviewButton");
        var save = Required<Button>(editor, "SaveProfileButton");
        Equal(empty ? "Save profile" : "Save & apply", save.Content?.ToString());
        Equal(!empty, preview.IsEnabled);
        Equal(true, editor.FindName("PatchSaamanBox") is null && editor.FindName("PatchVatEfsBox") is null);
        Equal(true, setup.FindName("SaamanSummary") is null);
        Equal(Visibility.Collapsed, Required<TextBox>(editor, "PasswordBoxText").Visibility);
        name.Text = "Synthetic unsaved controller";
        cid.Text = "0000001";
        password.Password = "SYNTHETIC-DRAFT-ONLY";
        setup.ShowSection("Applications");
        PopulateSetup(setup, empty);
        setup.ShowSection("Profile");
        Equal(true, ReferenceEquals(editor, setup.ControllerProfile));
        Equal("Synthetic unsaved controller", name.Text);
        Equal("0000001", cid.Text);
        Equal("SYNTHETIC-DRAFT-ONLY", password.Password);
        Equal(0, editor.SyntheticSaveRequests);
        Equal(0, editor.SyntheticPreviewRequests);
        LayoutValidation.CheckWindowContent(window);
        var fields = Required<System.Windows.Controls.Primitives.UniformGrid>(editor, "IdentityFields");
        Equal(fields.ActualWidth >= 560 * Math.Max(1, editor.FontSize / 12) ? 2 : 1, fields.Columns);
        var scroll = Required<ScrollViewer>(editor, "ProfileFormScroll");
        var footer = Required<FrameworkElement>(editor, "FooterActions");
        var scrollBounds = scroll.TransformToAncestor(editor).TransformBounds(new Rect(scroll.RenderSize));
        var footerBounds = footer.TransformToAncestor(editor).TransformBounds(new Rect(footer.RenderSize));
        Equal(true, scroll.ViewportHeight > 0 && scrollBounds.Bottom <= footerBounds.Top + 1);
        foreach (var button in new[] { preview, save })
        {
            var bounds = button.TransformToAncestor(editor).TransformBounds(new Rect(button.RenderSize));
            Equal(true, bounds.Top >= -1 && bounds.Bottom <= editor.ActualHeight + 1 && bounds.Right <= editor.ActualWidth + 1);
            Equal(true, button.Focusable && button.IsTabStop);
            CheckButtonCaption(button);
        }
        if (!empty) preview.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Equal(empty ? 0 : 1, editor.SyntheticPreviewRequests);
        Equal(1, editor.SyntheticSaveRequests);
        Equal(true, Presented(setup));
        var resultLines = SyntheticControllerResults(longReason: true);
        editor.ShowResultLines(resultLines);
        LayoutValidation.CheckWindowContent(window);
        var result = Required<TextBlock>(editor, "ResultText");
        var resultScroll = LayoutValidation.Descendants(editor).OfType<ScrollViewer>()
            .Single(candidate => IsVisualAncestor(candidate, result));
        var runs = result.Inlines.OfType<Run>().ToArray();
        Equal(resultLines.Count, runs.Length);
        Equal(resultLines.Count - 1, result.Inlines.OfType<LineBreak>().Count());
        for (int index = 0; index < runs.Length; index++)
        {
            var line = resultLines[index];
            Equal((index == 0 ? "" : line.NeedsAttention ? "! " : "• ") + line.Text, runs[index].Text);
            Equal(((SolidColorBrush)editor.FindResource(line.NeedsAttention ? "WarningFg" : "SecondaryText")).Color,
                ((SolidColorBrush)runs[index].Foreground).Color);
        }
        Equal(1, runs.Count(run => run.Text.StartsWith("! ", StringComparison.Ordinal)));
        Equal(TextWrapping.Wrap, result.TextWrapping);
        Equal(true, resultScroll.MaxHeight <= 90 && resultScroll.ActualHeight <= 90 && resultScroll.ScrollableHeight > 0);
        resultScroll.ScrollToEnd();
        LayoutValidation.CheckWindowContent(window);
        Equal(true, resultScroll.VerticalOffset > 0 && resultScroll.VerticalOffset >= resultScroll.ScrollableHeight - 1);
        foreach (var button in new[] { preview, save })
        {
            var bounds = button.TransformToAncestor(editor).TransformBounds(new Rect(button.RenderSize));
            Equal(true, bounds.Top >= -1 && bounds.Bottom <= editor.ActualHeight + 1 && bounds.Right <= editor.ActualWidth + 1);
        }
        editor.ShowResultLines([("Synthetic changes saved.", false), ("TrackAudio: Updated.", false)]);
        LayoutValidation.CheckWindowContent(window);
        Equal(0d, resultScroll.VerticalOffset);
        Equal(true, result.Inlines.OfType<Run>().All(run =>
            ((SolidColorBrush)run.Foreground).Color == ((SolidColorBrush)editor.FindResource("SecondaryText")).Color));
        editor.ShowResultLines([]);
        scroll.ScrollToTop();
        LayoutValidation.CheckWindowContent(window);
        var unloaded = new ControllerProfileControl();
        var unloadedHost = Host(unloaded, window.Width < 800);
        try
        {
            unloaded.SetLoadUnavailable();
            LayoutValidation.CheckWindowContent(unloadedHost);
            Equal(false, Required<Button>(unloaded, "SaveProfileButton").IsEnabled);
            Equal(false, Required<TextBox>(unloaded, "NameBox").IsEnabled);
            var retry = Required<Button>(unloaded, "RetryLoadButton");
            var bounds = retry.TransformToAncestor(unloaded).TransformBounds(new Rect(retry.RenderSize));
            Equal(true, retry.IsEnabled && retry.IsTabStop && bounds.Bottom <= unloaded.ActualHeight + 1);
            CheckButtonCaption(retry);
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Equal(1, unloaded.SyntheticRetryLoadRequests);
            Equal(0, unloaded.SyntheticSaveRequests);
            Equal(false, unloaded.HasDraft);
        }
        finally { unloadedHost.Close(); }
    }

    private static IReadOnlyList<(string Text, bool NeedsAttention)> SyntheticControllerResults(bool longReason = false) =>
    [
        ("Controller details saved.", false),
        ("EuroScope: Updated.", false),
        ("TrackAudio: This configuration is not supported; existing settings were kept." +
            (longReason ? string.Concat(Enumerable.Repeat(" Synthetic diagnostic detail: close the application before retrying this configuration step.", 18)) : ""), true),
        ("vATIS sign-in: Updated.", false),
        ("vATIS Swedish profile: Already configured.", false)
    ];

    private static bool IsVisualAncestor(DependencyObject ancestor, DependencyObject descendant)
    {
        for (var current = VisualTreeHelper.GetParent(descendant); current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private static void CheckPreferenceScrolling(Window window, PreferencesWorkspaceControl preferences)
    {
        var optional = Required<Expander>(preferences, "NotUsedExpander");
        optional.IsExpanded = true;
        preferences.SetSaveResult(false, "Synthetic save failure. Your selected preferences remain active for this session.");
        LayoutValidation.CheckWindowContent(window);
        var scroll = LayoutValidation.Descendants(preferences).OfType<ScrollViewer>().Single();
        Equal(true, scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled);
        foreach (var choice in Required<Panel>(preferences, "NotUsedChoices").Children.OfType<CheckBox>())
        {
            choice.BringIntoView(); LayoutValidation.CheckWindowContent(window);
            var bounds = choice.TransformToAncestor(scroll).TransformBounds(new Rect(choice.RenderSize));
            Equal(true, bounds.Top >= -1 && bounds.Bottom <= scroll.ViewportHeight + 1 && bounds.Right <= scroll.ViewportWidth + 1);
            Equal(true, choice.Focusable && choice.IsTabStop);
        }
        foreach (var name in new[] { "PrivacyButton", "ReportsButton", "RetrySaveButton" })
        {
            var button = Required<Button>(preferences, name);
            button.BringIntoView();
            LayoutValidation.CheckWindowContent(window);
            var bounds = button.TransformToAncestor(scroll).TransformBounds(new Rect(button.RenderSize));
            if (bounds.Top < -1 || bounds.Bottom > scroll.ViewportHeight + 1 || bounds.Left < -1 || bounds.Right > scroll.ViewportWidth + 1)
                throw new InvalidDataException("Embedded preference action is not reachable by scrolling: " + name);
            Equal(true, button.Focusable && button.IsTabStop);
            CheckButtonCaption(button);
        }
        optional.IsExpanded = false;
        preferences.SetSaveResult(true);
        scroll.ScrollToTop();
        LayoutValidation.CheckWindowContent(window);
    }

    private static void CheckPreferences(PreferencesWorkspaceControl preferences)
    {
        var original = SyntheticPreferences();
        var changes = new List<AppSettings>();
        void Changed(AppSettings draft) => changes.Add(draft);
        preferences.PreferencesChanged += Changed;
        try
        {
            preferences.SetPreferences(original);
            Equal(0, changes.Count);
            var compact = Required<CheckBox>(preferences, "CompactLayoutChoice");
            compact.IsChecked = false;
            compact.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Equal(1, changes.Count);
            Equal(false, changes[0].CompactLayout);
            Equal(true, original.CompactLayout);
            Equal(original.EuroscopeDataPath, changes[0].EuroscopeDataPath);
            Equal(original.SaamanDllPath, changes[0].SaamanDllPath);
            Equal(original.LastEuroscopeProfile, changes[0].LastEuroscopeProfile);
            Equal(true, changes[0].PatchSaaman && changes[0].SaamanProfileManaged);
            Required<ComboBox>(preferences, "TextSizeChoice").SelectedIndex = 2;
            Equal(2, changes.Count); Equal(1.5, changes[1].TextScale);
            var topmost = Required<CheckBox>(preferences, "AlwaysOnTopChoice");
            topmost.IsChecked = true;
            topmost.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Equal(3, changes.Count); Equal(true, changes[2].AlwaysOnTop);
            var optional = Required<Panel>(preferences, "NotUsedChoices").Children.OfType<CheckBox>().Single(choice => Equals(choice.Tag, "VATIRIS"));
            optional.IsChecked = false;
            optional.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Equal(4, changes.Count); Equal(false, ApplicationPreferences.IsNotUsed(changes[3], "VATIRIS"));
            var choices = Required<Panel>(preferences, "NotUsedChoices").Children.OfType<CheckBox>().ToArray();
            Equal(ApplicationPreferences.OptionalApplications.Length, choices.Length);
            foreach (string key in ApplicationPreferences.OptionalApplications) Equal(1, choices.Count(choice => Equals(choice.Tag, key)));
            foreach (string key in new[] { "VatEFS", "SAAMAN" })
            {
                var choice = choices.Single(item => Equals(item.Tag, key));
                foreach (bool hidden in new[] { true, false })
                {
                    int before = changes.Count;
                    choice.IsChecked = hidden;
                    choice.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    Equal(before + 1, changes.Count);
                    var draft = changes[^1];
                    Equal(hidden, ApplicationPreferences.IsNotUsed(draft, key));
                    Equal(original.PatchSaaman, draft.PatchSaaman);
                    Equal(original.PatchVatEfs, draft.PatchVatEfs);
                    Equal(original.SaamanProfileManaged, draft.SaamanProfileManaged);
                    Equal(original.SaamanDllPath, draft.SaamanDllPath);
                    Equal(original.SaamanProfilePath, draft.SaamanProfilePath);
                    Equal(original.VatEfsPath, draft.VatEfsPath);
                }
            }
            int expectedChanges = changes.Count;
            preferences.SetSaveResult(false, "Synthetic save failure; choices remain active for this session.");
            Equal(false, compact.IsChecked == true); Equal(true, topmost.IsChecked == true);
            var retry = Required<Button>(preferences, "RetrySaveButton");
            Equal(Visibility.Visible, retry.Visibility);
            int retries = 0, privacy = 0, reports = 0;
            preferences.RetrySaveRequested += () => retries++;
            preferences.PrivacyRequested += () => privacy++;
            preferences.ReportsRequested += () => reports++;
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Required<Button>(preferences, "PrivacyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Required<Button>(preferences, "ReportsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Equal(1, retries); Equal(1, privacy); Equal(1, reports); Equal(expectedChanges, changes.Count);
            preferences.SetActionsEnabled(false);
            compact.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Equal(expectedChanges, changes.Count);
            preferences.SetActionsEnabled(true);
            preferences.SetSaveResult(true);
            Equal(Visibility.Collapsed, retry.Visibility);
            preferences.SetPreferences(original);
            Equal(expectedChanges, changes.Count);
        }
        finally { preferences.PreferencesChanged -= Changed; }
    }

    private static SetupWorkspaceControl? FindSetupAncestor(DependencyObject? element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is SetupWorkspaceControl setup) return setup;
        return null;
    }

    private static void CheckHistory(ActivityWorkspaceControl activity, bool empty)
    {
        var grid = Required<DataGrid>(activity, "HistoryGrid");
        Equal(empty ? 0 : 3, grid.Items.Count);
        if (empty)
        {
            Equal("No recorded changes", Required<TextBlock>(activity, "SelectionHeading").Text);
            Equal(Visibility.Collapsed, Required<Button>(activity, "ReportButton").Visibility);
            return;
        }
        ActivityWorkspaceRequest? requested = null;
        activity.Requested += request => requested = request;
        Equal(true, Required<Button>(activity, "GngRecoveryButton").IsEnabled);
        Required<Button>(activity, "GngRecoveryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Equal(ActivityWorkspaceAction.ReviewRestore, requested!.Action);
        Equal(ActivityApplication.Gng, requested.Entry.Application);
        grid.SelectedIndex = 1;
        var selected = ((ActivityEntry)grid.SelectedItem).Id;
        activity.SetHistory(Entries(), "Refreshed synthetic history.");
        Equal(selected, ((ActivityEntry)grid.SelectedItem).Id);
        Equal(Visibility.Visible, Required<Button>(activity, "GngRecoveryButton").Visibility);
        Equal(true, Required<Button>(activity, "GngRecoveryButton").IsEnabled);
        Required<Button>(activity, "GngRecoveryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Equal(ActivityWorkspaceAction.ReviewRestore, requested!.Action);
        Equal(ActivityApplication.Saaman, requested.Entry.Application);
        grid.SelectedIndex = 2;
        Equal(Visibility.Collapsed, Required<TextBox>(activity, "RecoveryPath").Visibility);
        var saaman = Entries()[1];
        foreach (var entry in new[]
        {
            saaman with { Operation = ActivityOperation.Install, PreviousVersion = null },
            saaman with { Operation = ActivityOperation.Configure },
            saaman with { Operation = ActivityOperation.Restore },
            saaman with { RecoveryFolder = null },
            saaman with { Operation = ActivityOperation.Install },
            saaman with { Operation = ActivityOperation.Update }
        })
        {
            activity.SetHistory([entry], "Synthetic recovery eligibility.");
            bool canRestore = entry.PreviousVersion is not null && entry.RecoveryFolder is not null && entry.Operation is ActivityOperation.Install or ActivityOperation.Update;
            Equal(canRestore ? Visibility.Visible : Visibility.Collapsed, Required<Button>(activity, "GngRecoveryButton").Visibility);
            Equal(canRestore, Required<Button>(activity, "GngRecoveryButton").IsEnabled);
        }
        activity.SetHistory([], "No history.");
        Equal(Visibility.Collapsed, Required<Button>(activity, "ReportButton").Visibility);
    }

    private static void CheckControlLayout(UserControl control)
    {
        Equal(((SolidColorBrush)control.FindResource("PrimaryText")).Color, ((SolidColorBrush)control.Foreground).Color);
        foreach (var button in LayoutValidation.Descendants(control).OfType<Button>().Where(button => Presented(button) && button.ActualWidth > 0))
            CheckButtonCaption(button);
        foreach (var radio in LayoutValidation.Descendants(control).OfType<RadioButton>().Where(Presented))
        {
            var bounds = radio.TransformToAncestor(control).TransformBounds(new Rect(radio.RenderSize));
            if (bounds.Left < -1 || bounds.Right > control.ActualWidth + 1)
                throw new InvalidDataException("Workspace section navigation is outside the viewport.");
            foreach (var text in LayoutValidation.Descendants(radio).OfType<TextBlock>())
                Equal(TextWrapping.Wrap, text.TextWrapping);
        }
    }

    private static void CheckButtonCaption(Button button)
    {
        if (button.Content is not string caption) return;
        var text = new FormattedText(caption, System.Globalization.CultureInfo.CurrentUICulture, button.FlowDirection,
            new Typeface(button.FontFamily, button.FontStyle, button.FontWeight, button.FontStretch), button.FontSize,
            button.Foreground, 1);
        if (text.WidthIncludingTrailingWhitespace + button.Padding.Left + button.Padding.Right > button.ActualWidth + 3)
            throw new InvalidDataException($"Workspace button is clipped: {caption} ({button.ActualWidth:0.0}px).");
    }

    private static bool Presented(FrameworkElement element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement ui && ui.Visibility != Visibility.Visible) return false;
        return true;
    }

    private static T Required<T>(FrameworkElement parent, string name) where T : FrameworkElement =>
        parent.FindName(name) as T ?? throw new InvalidDataException("Missing workspace element: " + name);
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidDataException($"Expected {expected}, got {actual}.");
    }
}

internal static partial class LayoutSnapshots
{
    internal static void SaveWorkspace(Window window, string path) => Save(window, path);
}
