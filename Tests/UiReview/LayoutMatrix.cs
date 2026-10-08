using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

internal static partial class LayoutMatrix
{
    private static readonly Func<Window>[] Factories =
    [
        () => new MainWindow(), () => new SettingsWindow(), () => new AppConfigWindow(),
        () => new MaintenanceWindow(), () => new InstallationDiscoveryWindow(),
        () => new EuroScopeInstallWindow(), () => new FreshSoftwareInstallWindow(), () => new FreshSoftwareInstallWindow(vatEfs: true), () => new GngCleanupWindow()
    ];

    public static string Run()
    {
        LayoutValidation.CheckProgressBindingSource();
        var errorsBefore = UiFixture.ErrorCount;
        var trace = new BindingErrors();
        var source = PresentationTraceSources.DataBindingSource;
        var priorLevel = source.Switch.Level;
        source.Switch.Level = SourceLevels.Error;
        source.Listeners.Add(trace);
        int layouts = 0;
        try
        {
            foreach (var dark in new[] { false, true })
            {
                if (UiFixture.Dark != dark) UiFixture.ToggleTheme();
                CheckThemeReadability();
                layouts += CheckGngUpdateLayouts();
                layouts += CheckGngFileDiffLayouts();
                CheckCompactDensity();
                layouts++;
                CheckRestartNotice();
                layouts++;
                layouts += CheckPathVerificationLayouts();
                foreach (var blank in new[] { false, true })
                foreach (var minimum in new[] { false, true })
                {
                    UiFixture.Blank = blank;
                    UiFixture.Minimum = minimum;
                    UiFixture.Scale = 1;
                    foreach (var factory in Factories)
                    {
                        var window = factory();
                        var context = $"{window.GetType().Name}, {(dark ? "dark" : "light")}, {(blank ? "blank" : "mixed")}, {(minimum ? "minimum" : "default")}";
                        try
                        {
                            LayoutValidation.CheckWindowContent(window);
                            CheckFooter(window);
                            if (window is MainWindow main) { CheckRowReadability(main); CheckFirstRunActions(main); CheckSoftwareStates(main); CheckLongStatusWidth(main); }
                            if (window is SettingsWindow settings) CheckSettingsExpansion(settings);
                            if (window is AppConfigWindow profile) CheckProfileScroll(profile, minimum);
                            if (window is GngCleanupWindow cleanup) CheckCleanupLayout(cleanup);
                            if (window is MaintenanceWindow or EuroScopeInstallWindow or FreshSoftwareInstallWindow)
                                CheckReviewLocation(window);
                            DrainBindings();
                            Require(UiFixture.ErrorCount == errorsBefore, "An unhandled fixture exception was logged.");
                            Require(trace.Errors.Count == 0, "WPF binding error: " + string.Join(" | ", trace.Errors));
                            layouts++;
                        }
                        catch (Exception error) { throw new InvalidDataException(context + ": " + error.Message, error); }
                        finally { window.Close(); }
                    }
                }

                // Layout scaling is a deliberate stress test, not a claim about Windows DPI.
                foreach (var scale in new[] { 1.5, 2.0 })
                {
                    UiFixture.Blank = false;
                    UiFixture.Minimum = true;
                    UiFixture.Scale = scale;
                    var profile = new AppConfigWindow();
                    try
                    {
                        LayoutValidation.CheckWindowContent(profile);
                        CheckProfileScroll(profile, true);
                        CheckFooter(profile);
                        layouts++;
                    }
                    finally { profile.Close(); }
                }
                layouts += CheckWizardSteps();
            }
            Require(UiFixture.ErrorCount == errorsBefore, "An unhandled fixture exception was logged.");
            Require(trace.Errors.Count == 0, "WPF binding errors: " + string.Join(" | ", trace.Errors));
            return $"PASS: {layouts} hidden layout cases; both themes, blank/mixed paths, default/minimum sizes, uniform compact/comfortable rows, compact eight-row fit, selected application details, persistent restart notice, profile and wizard scrolling/footer reachability, optional GNG setup, inert GNG start/sign-in/download/cancel/review/completion/runtime fallback, production changes-only review filter/counts/reset/empty state, warning/input contrast, read-only progress bindings, missing/stale paths and update/cancellation states. No windows shown.";
        }
        finally
        {
            source.Listeners.Remove(trace);
            source.Switch.Level = priorLevel;
            UiFixture.Blank = UiFixture.Minimum = false;
            UiFixture.Scale = 1;
        }
    }

    private static void CheckThemeReadability()
    {
        Require(Contrast(Colors.White, Color.FromRgb(0x8a, 0x4b, 0)) >= 4.5, "Standard warning badge text contrast is below 4.5:1.");
        foreach (var (foreground, background) in new[] { ("PrimaryText", "AppBg"), ("SecondaryText", "AppBg"), ("InputFg", "InputBg"), ("WarningFg", "WarningBg"), ("SuccessFg", "SuccessBg"), ("SelectionFg", "SelectionBg") })
        {
            var fg = (SolidColorBrush)Application.Current.FindResource(foreground);
            var bg = (SolidColorBrush)Application.Current.FindResource(background);
            Require(Contrast(fg.Color, bg.Color) >= 4.5, foreground + "/" + background + " text contrast is below 4.5:1.");
        }
        foreach (var foreground in new[] { "PrimaryText", "ApplicationSecondaryText", "ApplicationWarningText", "ApplicationUpdateText", "ApplicationSuccessText" })
        foreach (var background in new[] { "RowBg", "RowAltBg", "ApplicationSelectedBg" })
        {
            var fg = (SolidColorBrush)Application.Current.FindResource(foreground);
            var bg = (SolidColorBrush)Application.Current.FindResource(background);
            Require(Contrast(fg.Color, bg.Color) >= 4.5,
                foreground + "/" + background + " application text contrast is below 4.5:1.");
        }
        foreach (var foreground in new[] { "PrimaryText", "ApplicationSecondaryText" })
        {
            var fg = (SolidColorBrush)Application.Current.FindResource(foreground);
            var bg = (SolidColorBrush)Application.Current.FindResource("ColumnHeaderBg");
            Require(Contrast(fg.Color, bg.Color) >= 4.5,
                foreground + "/ColumnHeaderBg detail and heading text contrast is below 4.5:1.");
        }
    }

    private static void CheckFooter(Window window)
    {
        var content = (FrameworkElement)window.Content;
        var names = window switch
        {
            MainWindow => new[] { "CheckButton", "SetupButton", "MaintenanceButton" },
            SettingsWindow => new[] { "SaveButton" },
            AppConfigWindow => new[] { "PreviewButton", "SaveProfileButton" },
            MaintenanceWindow => new[] { "ReviewButton", "ApplyButton", "CloseButton", "RefreshButton" },
            EuroScopeInstallWindow or FreshSoftwareInstallWindow => new[] { "ReviewButton", "ApplyButton", "CloseButton" },
            SetupWizardWindow => new[] { "SkipButton", "BackButton", "NextButton" },
            GngCleanupWindow => new[] { "RestoreButton", "ArchiveButton", "CloseButton" },
            GngUpdateWindow => new[] { "ImportButton", "ExternalBrowserButton", "RestoreButton", "CloseButton" },
            GngFileDiffWindow => new[] { "CloseButton" },
            _ => Array.Empty<string>()
        };
        foreach (var name in names)
        {
            var button = Required<Button>(window, name);
            Require(IsPresented(button), name + " is unexpectedly collapsed.");
            var rectangle = Bounds(button, content);
            Require(rectangle.Width > 20 && rectangle.Height > 15, name + " has no usable layout area.");
            Require(rectangle.Left >= -.5 && rectangle.Top >= -.5 &&
                rectangle.Right <= content.ActualWidth + .5 && rectangle.Bottom <= content.ActualHeight + .5,
                name + " extends beyond the available content area.");
        }
    }

    private static void CheckRestartNotice()
    {
        UiFixture.Blank = false;
        UiFixture.Minimum = true;
        UiFixture.Scale = 1;
        var window = new MainWindow();
        try
        {
            var content = (FrameworkElement)window.Content;
            var notice = Required<TextBox>(window, "RestartNotice");
            var footer = (Grid)notice.Parent;
            LayoutValidation.CheckWindowContent(window);
            Require(notice.Visibility == Visibility.Collapsed && footer.RowDefinitions[1].ActualHeight < .5,
                "The ordinary footer must not reserve a blank restart-notice row.");
            var ordinaryFooterHeight = footer.ActualHeight;

            window.SetSyntheticRestart(true);
            LayoutValidation.CheckWindowContent(window);
            DrainBindings();
            Require(IsPresented(notice), "The restart notice must remain visible while restart is required.");
            Require(notice.IsReadOnly && notice.Focusable && notice.IsTabStop && notice.IsReadOnlyCaretVisible &&
                !string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(notice)),
                "The restart notice must be keyboard-readable, selectable and named for accessibility.");
            Require(notice.TextWrapping == TextWrapping.Wrap && notice.LineCount >= 1,
                "The restart instruction must be laid out with wrapping at the minimum window width.");
            var textScroll = LayoutValidation.Descendants(notice).OfType<ScrollViewer>().Single();
            Require(textScroll.ExtentHeight <= textScroll.ViewportHeight + .5,
                "The complete restart instruction must fit without an inner scrollbar.");
            var rectangle = Bounds(notice, content);
            Require(rectangle.Left >= -.5 && rectangle.Top >= -.5 && rectangle.Right <= content.ActualWidth + .5 &&
                rectangle.Bottom <= content.ActualHeight + .5, "The restart notice extends beyond the minimum window.");
            CheckFooter(window);
            Require(Required<ScrollViewer>(window, "ApplicationsScroll").ViewportHeight > 25,
                "The restart notice must leave the application list scrollable.");
            var fg = (SolidColorBrush)notice.Foreground;
            var bg = (SolidColorBrush)notice.Background;
            Require(Contrast(fg.Color, bg.Color) >= 4.5, "The restart notice needs readable warning contrast.");

            var instruction = notice.Text;
            UiFixture.Refresh();
            Required<TextBlock>(window, "LastCheckedText").Text = "Last checked: synthetic later check";
            LayoutValidation.CheckWindowContent(window);
            Require(IsPresented(notice) && notice.Text == instruction,
                "Ordinary footer updates and fixture refreshes must not replace the restart instruction.");

            window.SetSyntheticRestart(false);
            LayoutValidation.CheckWindowContent(window);
            Require(footer.RowDefinitions[1].ActualHeight < .5 && Math.Abs(footer.ActualHeight - ordinaryFooterHeight) < .5,
                "Collapsing the restart notice must recover all of its footer space.");
        }
        finally { window.Close(); }
    }

    private static int CheckWizardSteps()
    {
        int cases = 0;
        foreach (bool blank in new[] { false, true })
        foreach (var (minimum, scale) in new[] { (false, 1d), (true, 1d), (true, 1.5d) })
        {
            UiFixture.Blank = blank;
            UiFixture.Minimum = minimum;
            UiFixture.Scale = scale;
            var window = new SetupWizardWindow();
            try
            {
                var content = (FrameworkElement)window.Content;
                var scroll = Required<ScrollViewer>(window, "ContentScroll");
                var pages = new[] { "WelcomePage", "ApplicationsPage", "PreferencesPage", "SummaryPage" };
                var lastControls = new[] { "ManualChoice", "VatisButton", "StartupChoice", "SummaryText" };
                for (int step = 0; step < 4; step++)
                {
                    Require(window.SyntheticPage == step, "Wizard navigation did not reach the intended step.");
                    LayoutValidation.CheckWindowContent(window);
                    DrainBindings();
                    CheckFooter(window);
                    Require(scroll.ViewportHeight > 25, $"Wizard step {step} has no usable scrolling body (viewport {scroll.ViewportHeight}, scale {scale}).");
                    foreach (var (name, index) in pages.Select((name, index) => (name, index)))
                        Require(IsPresented(Required<FrameworkElement>(window, name)) == (index == step), "Wizard has the wrong visible page.");
                    Require(Required<Button>(window, "BackButton").IsEnabled == (step > 0), "Wizard Back availability is wrong.");
                    Require(Equals(Required<Button>(window, "NextButton").Content, step == 3 ? "Finish" : "Next"), "Wizard final action is mislabeled.");
                    var page = Required<FrameworkElement>(window, pages[step]);
                    foreach (var button in LayoutValidation.Descendants(page).OfType<Button>().Where(IsPresented))
                    {
                        var bounds = Bounds(button, scroll);
                        Require(bounds.Width > 20 && bounds.Left >= -.5 && bounds.Right <= scroll.ViewportWidth + .5,
                            $"Wizard {button.Name} is horizontally clipped: {bounds}, viewport {scroll.ViewportWidth}, scale {scale}.");
                    }
                    var last = Required<FrameworkElement>(window, lastControls[step]);
                    last.BringIntoView();
                    DrainBindings(); content.UpdateLayout();
                    Require(new Rect(0, 0, scroll.ViewportWidth, scroll.ViewportHeight).IntersectsWith(Bounds(last, scroll)),
                        "Wizard's final choice cannot be reached by scrolling on " + pages[step] + ".");
                    if (step == 1)
                    {
                        var descriptions = LayoutValidation.Descendants(page).OfType<TextBlock>().Select(label => label.Text).ToArray();
                        foreach (var description in new[] { ApplicationDescriptions.EuroScope, ApplicationDescriptions.Vacs,
                            ApplicationDescriptions.TrackAudio, ApplicationDescriptions.Vatis, ApplicationDescriptions.VatEfs, ApplicationDescriptions.SwedishGng })
                            Require(descriptions.Contains(description), "Wizard application purpose description is missing: " + description);
                        foreach (var label in LayoutValidation.Descendants(page).OfType<TextBlock>().Where(label =>
                            label.Text is "EuroScope" or "VACS" or "TrackAudio" or "vATIS" or "VatEFS"))
                        {
                            var natural = new FormattedText(label.Text, System.Globalization.CultureInfo.CurrentCulture,
                                label.FlowDirection, new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch),
                                label.FontSize, label.Foreground, VisualTreeHelper.GetDpi(label).PixelsPerDip);
                            Require(label.ActualWidth + 1 >= natural.WidthIncludingTrailingWhitespace,
                                $"Wizard application name '{label.Text}' is clipped: width {label.ActualWidth}, text {natural.WidthIncludingTrailingWhitespace}, scale {scale}.");
                        }
                        foreach (var name in new[] { "VacsButton", "TrackAudioButton", "VatisButton", "VatEfsButton" })
                            Require(Required<Button>(window, name).IsEnabled == blank, "Configured clients must use their existing application row, not fresh setup.");
                        if (!blank)
                        {
                            window.SetSyntheticExecutablePresence(false);
                            LayoutValidation.CheckWindowContent(window); CheckFooter(window);
                            foreach (var (buttonName, pathName) in new[] { ("VacsButton", "VacsPath"), ("TrackAudioButton", "TrackAudioPath"), ("VatisButton", "VatisPath"), ("VatEfsButton", "VatEfsPath") })
                            {
                                var button = Required<Button>(window, buttonName);
                                var path = Required<TextBlock>(window, pathName);
                                Require(button.IsEnabled, "A retained path with a missing executable must allow a fresh setup review.");
                                Require(path.Text.Contains("not found", StringComparison.OrdinalIgnoreCase) &&
                                    path.Text.Contains(@"C:\Synthetic ATC applications\", StringComparison.Ordinal) &&
                                    path.Text.Contains("Settings", StringComparison.Ordinal),
                                    "Missing-program guidance must retain the chosen location and explain how to correct it.");
                                Require(button.ToolTip?.ToString()?.Contains("remaining program folders and settings", StringComparison.Ordinal) == true,
                                    "Setup review must not imply that a retained data root is safe to overwrite.");
                            }
                            cases++;
                        }
                        window.SetSyntheticRestart(true);
                        LayoutValidation.CheckWindowContent(window); CheckFooter(window);
                        Require(scroll.ViewportHeight > 25, "Wizard restart status leaves no usable scrolling body.");
                        foreach (var name in new[] { "EuroScopeButton", "VacsButton", "TrackAudioButton", "VatisButton", "VatEfsButton", "ProfileButton" })
                            Require(!Required<Button>(window, name).IsEnabled, "Further setup remained available after a synthetic restart requirement.");
                        window.SetSyntheticRestart(false);
                        window.SetSyntheticExecutablePresence(null);
                    }
                    if (step == 3)
                    {
                        CheckWizardGngChoice(window);
                        CheckWizardGuides(window, blank ? [] : [SetupWizardGuide.EuroScopeGng, SetupWizardGuide.TrackAudio, SetupWizardGuide.Vatis]);
                        if (!blank)
                        {
                            foreach (var (settings, expected) in new (AppSettings, SetupWizardGuide[])[]
                            {
                                (new() { VacsExePath = "C:\\Synthetic\\VACS.exe" }, []),
                                (new() { EuroscopeDataPath = "C:\\Synthetic\\Sweden GNG" }, [SetupWizardGuide.EuroScopeGng]),
                                (new() { TrackAudioExePath = "C:\\Synthetic\\TrackAudio.exe" }, [SetupWizardGuide.TrackAudio]),
                                (new() { VatisExePath = "C:\\Synthetic\\vATIS.exe" }, [SetupWizardGuide.Vatis])
                            })
                            {
                                window.SetSyntheticGuideSettings(settings);
                                CheckWizardGuides(window, expected);
                                cases++;
                            }
                            window.SetSyntheticGuideSettings(null);
                        }
                    }
                    cases++;
                    if (step < 3) window.SyntheticNext();
                }
                for (int step = 2; step >= 0; step--) { window.SyntheticBack(); Require(window.SyntheticPage == step, "Wizard Back skipped a normal step."); }
                Required<RadioButton>(window, "ManualChoice").IsChecked = true;
                window.SyntheticNext();
                Require(window.SyntheticPage == 2, "Manual route must skip application setup.");
                window.SyntheticBack();
                Require(window.SyntheticPage == 0, "Manual route Back must return to Welcome.");
            }
            catch (Exception error) { throw new InvalidDataException($"Wizard, {(UiFixture.Dark ? "dark" : "light")}, {(blank ? "blank" : "mixed")}, {(minimum ? "minimum" : "default")}, scale {scale}: {error.Message}", error); }
            finally { window.Close(); }
        }
        return cases;
    }

    private static void CheckWizardGuides(SetupWizardWindow window, SetupWizardGuide[] expected)
    {
        LayoutValidation.CheckWindowContent(window);
        DrainBindings();
        CheckFooter(window);
        var scroll = Required<ScrollViewer>(window, "ContentScroll");
        var content = (FrameworkElement)window.Content;
        Require(IsPresented(Required<FrameworkElement>(window, "NextStepsPanel")) == (expected.Length > 0),
            "Optional next steps should be hidden when none of the chosen applications has a guide.");
        Require(LayoutValidation.Descendants(Required<FrameworkElement>(window, "SummaryPage")).OfType<TextBlock>()
            .Any(label => label.Text.StartsWith("You do not need every ", StringComparison.Ordinal) &&
                label.Text.Contains("Finish", StringComparison.Ordinal)),
            "The final page must still explain that applications are optional when no guide matches.");
        foreach (var (guide, prefix) in new[] { (SetupWizardGuide.EuroScopeGng, "EuroScope"), (SetupWizardGuide.TrackAudio, "TrackAudio"), (SetupWizardGuide.Vatis, "Vatis") })
        {
            var panel = Required<FrameworkElement>(window, prefix + "GuidePanel");
            var button = Required<Button>(window, prefix + "GuideButton");
            bool visible = expected.Contains(guide);
            Require(IsPresented(panel) == visible && IsPresented(button) == visible, "Unexpected optional guide visibility: " + guide);
            if (!visible) continue;
            Require(Equals(button.Tag, guide), "Guide button must carry the typed fixed destination identifier.");
            button.BringIntoView(); DrainBindings(); content.UpdateLayout();
            var bounds = Bounds(button, scroll);
            Require(bounds.Left >= -.5 && bounds.Right <= scroll.ViewportWidth + .5 && bounds.Top >= -.5 && bounds.Bottom <= scroll.ViewportHeight + .5,
                $"Guide {guide} cannot be reached without clipping: {bounds}, viewport {scroll.ViewportWidth}×{scroll.ViewportHeight}.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(Required<TextBlock>(window, "StatusText").Text.Contains("is inert in this synthetic fixture", StringComparison.Ordinal),
                "Guide clicks must use the inert fixture action.");
            Required<TextBlock>(window, "StatusText").Text = "";
        }
        CheckFooter(window);
    }

    private static void CheckProfileScroll(AppConfigWindow window, bool cramped)
    {
        var content = (FrameworkElement)window.Content;
        var scroll = Required<ScrollViewer>(window, "ProfileFormScroll");
        var footer = Required<FrameworkElement>(window, "FooterActions");
        var bottomField = Required<CheckBox>(window, "PatchVatEfsBox");
        Require(!IsWithin(footer, scroll), "Profile footer is inside the scrolling form.");
        Require(IsWithin(bottomField, scroll), "Last profile field is outside the scrolling form.");
        Require(scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto && scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled,
            "Profile form needs vertical scrolling without horizontal scrolling.");
        Require(scroll.ViewportHeight > 0, "Profile form has no usable viewport.");
        Require(Bounds(scroll, content).Bottom <= Bounds(footer, content).Top + .5, "Profile form overlaps the fixed footer.");
        if (cramped) Require(scroll.ScrollableHeight > 0, "Cramped profile form did not expose its overflow through scrolling.");
        scroll.ScrollToEnd();
        DrainBindings();
        content.UpdateLayout();
        // The explanatory paragraph after the last checkbox may fill a tiny viewport.
        // Ask to reveal the actual option rather than assuming "bottom" shows that option.
        bottomField.BringIntoView();
        DrainBindings();
        content.UpdateLayout();
        var viewport = new Rect(0, 0, scroll.ActualWidth, scroll.ActualHeight);
        Require(viewport.IntersectsWith(Bounds(bottomField, scroll)), $"Last profile option cannot be reached by scrolling (scale {UiFixture.Scale}, viewport {viewport}, option {Bounds(bottomField,scroll)}, offset {scroll.VerticalOffset}).");
        var banner = Required<Border>(window, "SyncBanner");
        var message = Required<TextBlock>(window, "SyncText");
        Require(banner.Background is SolidColorBrush && message.Foreground is SolidColorBrush, "Profile warning needs explicit readable brushes.");
        Require(Contrast(((SolidColorBrush)banner.Background).Color, ((SolidColorBrush)message.Foreground).Color) >= 4.5,
            "Profile attention message contrast is below 4.5:1.");
        Require(Required<TextBox>(window, "TargetPathText").IsReadOnly, "Target path should be reviewable without editing it in the profile form.");
        Require(Required<Button>(window,"PreviewButton").IsEnabled == !UiFixture.Blank, "Preview must be unavailable without a configured target.");
        Require(Equals(Required<Button>(window,"SaveProfileButton").Content, UiFixture.Blank ? "Save profile" : "Save & apply"),
            "The save action must distinguish profile storage from applying external changes.");
    }

    private static void CheckSettingsExpansion(SettingsWindow window)
    {
        var expander = Required<Expander>(window,"ApplicationPaths");
        Require(!expander.IsExpanded, "Advanced application paths must start collapsed.");
        CheckFooter(window);
        expander.IsExpanded=true;
        LayoutValidation.CheckWindowContent(window);
        CheckFooter(window);
        var content=(FrameworkElement)window.Content;
        var field=Required<TextBox>(window,"VatEfsPath");
        var scroll=LayoutValidation.Descendants(content).OfType<ScrollViewer>().First(item=>IsWithin(expander,item));
        Require(scroll.ViewportHeight>0, "Expanded paths have no usable viewport.");
        scroll.ScrollToEnd(); DrainBindings(); content.UpdateLayout();
        Require(new Rect(0,0,scroll.ActualWidth,scroll.ActualHeight).IntersectsWith(Bounds(field,scroll)),
            "Last advanced path cannot be reached by scrolling.");
        expander.IsExpanded=false;
    }

    private static void CheckReviewLocation(Window window)
    {
        var content = (FrameworkElement)window.Content;
        var scroll = Required<ScrollViewer>(window, "ContentScroll");
        var panel = Required<FrameworkElement>(window, "ReviewPanel");
        var review = Required<TextBox>(window, "ReviewText");
        var confirm = Required<CheckBox>(window, window is MaintenanceWindow ? "ConfirmRemoval" : window is EuroScopeInstallWindow ? "ConfirmChange" : "ConfirmInstall");
        Require(IsWithin(review, panel) && IsWithin(confirm, panel) && IsWithin(panel, scroll),
            "Review and confirmation must share the scrollable review area.");
        Require(confirm.IsChecked != true && !confirm.IsEnabled, "Destructive confirmation must start unchecked and unavailable.");
        Require(Bounds(confirm, panel).Top >= Bounds(review, panel).Bottom, "Confirmation must follow the reviewed text.");
        Require(review.IsReadOnly && review.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
            "Long review text needs a read-only scrollable viewport.");
        UiFixture.Handle(window, window, new RoutedEventArgs(), "Review_Click");
        LayoutValidation.CheckWindowContent(window);
        Require(confirm.IsEnabled && confirm.IsChecked != true, "Synthetic review must never preconfirm the operation.");
        DrainBindings();
        content.UpdateLayout();
        var reviewBounds=Bounds(review,scroll);
        Require(reviewBounds.Top>=-.5 && reviewBounds.Top<scroll.ActualHeight && reviewBounds.Bottom>0,
            "The shared production review helper did not bring the beginning of the review into view.");
        Require(review.CaretIndex==0 && review.VerticalOffset<.5, "The shared review helper did not start at the beginning of the review text.");
        // Separately verify that the subsequent confirmation remains reachable.
        // Do not move the viewport before checking automatic review positioning.
        confirm.BringIntoView();
        DrainBindings();
        content.UpdateLayout();
        Require(new Rect(0, 0, scroll.ActualWidth, scroll.ActualHeight).IntersectsWith(Bounds(confirm, scroll)),
            "Reviewed confirmation cannot be reached by scrolling.");
        CheckFooter(window);
    }

    private static T Bound<T>(FrameworkElement root, DependencyProperty property, string path, CheckResult row) where T : FrameworkElement =>
        LayoutValidation.Descendants(root).OfType<T>().Single(element =>
            ReferenceEquals(element.DataContext, row) && BindingOperations.GetBinding(element, property)?.Path?.Path == path);
    private static T Required<T>(Window window, string name) where T : class =>
        window.FindName(name) as T ?? throw new InvalidDataException("Expected named control " + name + ".");
    private static void DrainBindings() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static Rect Bounds(FrameworkElement element, Visual ancestor) => element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
    private static bool IsWithin(DependencyObject child, DependencyObject ancestor)
    {
        for (var item = VisualTreeHelper.GetParent(child); item is not null; item = VisualTreeHelper.GetParent(item))
            if (ReferenceEquals(item, ancestor)) return true;
        return false;
    }
    private static bool IsPresented(FrameworkElement element)
    {
        // An unopened Window is itself collapsed; inspect only the content visibility chain.
        for (DependencyObject? item = element; item is not null && item is not Window; item = VisualTreeHelper.GetParent(item))
            if (item is UIElement ui && ui.Visibility != Visibility.Visible) return false;
        return true;
    }
    private static double Contrast(Color first, Color second)
    {
        static double Channel(byte value) { var n = value / 255d; return n <= .04045 ? n / 12.92 : Math.Pow((n + .055) / 1.055, 2.4); }
        static double Light(Color color) => .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
        var a = Light(first); var b = Light(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
