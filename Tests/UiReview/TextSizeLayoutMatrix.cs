using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

internal static partial class LayoutMatrix
{
    private static int CheckTextSizeLayouts()
    {
        int cases = 0;
        try
        {
            // These cases change the same resources as the app preference. They do not
            // scale the window geometry with the separate LayoutTransform simulation.
            UiFixture.Scale = 1;
            foreach (double scale in new[] { 1d, 1.25d, 1.5d })
            {
                UiFixture.SetTextScale(scale);
                foreach (bool minimum in new[] { false, true })
                foreach (string density in new[] { "Compact", "Comfortable" })
                {
                    UiFixture.Blank = false;
                    UiFixture.Minimum = minimum;
                    var main = new MainWindow { Tag = density };
                    try
                    {
                        main.RefreshSyntheticDetailsLayout();
                        LayoutValidation.CheckWindowContent(main);
                        CheckTextPreference(main, scale);
                        CheckRowReadability(main);
                        CheckMainPresentationControls(main, scale);
                        CheckDetailsSelection(main);
                        CheckFooter(main);
                        cases++;
                    }
                    catch (Exception error)
                    {
                        throw new InvalidDataException($"Main, text {scale:P0}, {density}, {(minimum ? "minimum" : "default")}: {error.Message}", error);
                    }
                    finally { main.Close(); }
                }

                if (scale == 1) continue; // Ordinary cases already cover each dialog at 100%.
                foreach (bool blank in new[] { false, true })
                {
                    UiFixture.Blank = blank;
                    UiFixture.Minimum = true;
                    foreach (var factory in Factories.Skip(1))
                    {
                        var window = factory();
                        try
                        {
                            LayoutValidation.CheckWindowContent(window);
                            CheckTextPreference(window, scale);
                            CheckFooter(window);
                            if (window is SettingsWindow settings) CheckSettingsExpansion(settings);
                            if (window is AppConfigWindow profile) CheckProfileScroll(profile, true);
                            if (window is ActivityWindow activity) CheckActivityLayout(activity);
                            if (window is GngCleanupWindow cleanup) CheckCleanupLayout(cleanup);
                            if (window is MaintenanceWindow or EuroScopeInstallWindow or FreshSoftwareInstallWindow)
                                CheckReviewLocation(window);
                            cases++;
                        }
                        catch (Exception error)
                        {
                            throw new InvalidDataException($"{window.GetType().Name}, text {scale:P0}, minimum, {(blank ? "blank" : "mixed")}: {error.Message}", error);
                        }
                        finally { window.Close(); }
                    }
                }
                UiFixture.Blank = false;
                UiFixture.Minimum = true;
                var gng = new GngUpdateWindow();
                try
                {
                    gng.SetSyntheticState(GngUpdateFixtureState.Review);
                    LayoutValidation.CheckWindowContent(gng);
                    CheckTextPreference(gng, scale);
                    CheckGngReview(gng);
                    cases++;
                }
                catch (Exception error) { throw new InvalidDataException($"GNG review, text {scale:P0}: {error.Message}", error); }
                finally { gng.Close(); }

                cases += CheckGngFileDiffLayouts();
                cases += CheckWizardSteps(textSizeOnly: true);
            }
        }
        finally
        {
            UiFixture.SetTextScale(1);
            UiFixture.Scale = 1;
            UiFixture.Blank = UiFixture.Minimum = false;
        }
        return cases;
    }

    private static void CheckTextPreference(Window window, double scale)
    {
        var content = (FrameworkElement)window.Content;
        Require(content.LayoutTransform.Value.IsIdentity,
            "Text-size coverage must not accidentally use geometry scaling.");
        Require(Math.Abs(window.FontSize - 12 * scale) < .01,
            $"{window.GetType().Name} does not inherit the chosen text size: {window.FontSize}.");
        Require(!window.IsVisible, "A text-size fixture showed a native window.");
    }

    private static void CheckMainPresentationControls(MainWindow window, double scale)
    {
        var content = (FrameworkElement)window.Content;
        var list = Required<ItemsControl>(window, "AppList");
        var euroScope = list.Items.Cast<CheckResult>().Single(row => row.HasEuroScopeManagement);
        var caption = Bound<TextBlock>(list, TextBlock.TextProperty, "SelectedProfileCaption", euroScope);
        Require(IsPresented(caption) && caption.Text == euroScope.SelectedProfileCaption && caption.Text.Length > 0,
            "The chosen EuroScope profile must be visible before launching.");
        Require(Math.Abs(caption.FontSize - 11 * scale) < .01 && caption.ToolTip?.ToString() == caption.Text,
            "Selected-profile text must scale and retain its full value in a tooltip when shortened.");

        var selectedProfile = euroScope.SelectedProfile;
        var density = window.Tag;
        double otherScale = scale == 1 ? 1.5 : 1;
        UiFixture.SetTextScale(otherScale);
        LayoutValidation.CheckWindowContent(window);
        Require(Equals(window.Tag, density) && ReferenceEquals(euroScope.SelectedProfile, selectedProfile) &&
            Math.Abs(caption.FontSize - 11 * otherScale) < .01,
            "Changing text size in an existing window must retain its density and selected profile.");
        CheckRowReadability(window);
        UiFixture.SetTextScale(scale);
        LayoutValidation.CheckWindowContent(window);

        foreach (var text in LayoutValidation.Descendants(list).OfType<TextBlock>())
        {
            var path = System.Windows.Data.BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path?.Path;
            if (path is "DisplayName" or "RowStatusText")
                Require(Math.Abs(text.FontSize - 13 * scale) < .01, path + " did not adopt the text preference.");
        }
        foreach (var button in LayoutValidation.Descendants(list).OfType<Button>().Where(IsPresented))
            CheckButtonCaption(button);

        foreach (string name in new[] { "PinButton", "DensityToggleButton", "ThemeToggleButton", "AppConfigButton", "SettingsButton" })
        {
            var button = Required<Button>(window, name);
            CheckControlBounds(button, content);
            CheckButtonCaption(button);
        }
        foreach (var title in LayoutValidation.Descendants(content).OfType<TextBlock>().Where(text =>
            text.Text is "Sweden FIR Launchpad" or "Open" or "Application" or "Your version" or "Latest / supported" or "Status" or "Options"))
            CheckUntrimmedLabel(title);

        var pin = Required<Button>(window, "PinButton");
        pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        LayoutValidation.CheckWindowContent(window);
        Require(Equals(pin.Content, "Unpin"), "Pin must expose its reverse action in the inert fixture.");
        CheckButtonCaption(pin);
        CheckControlBounds(pin, content);
        pin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        SelectDetails(window, euroScope);
        var details = Required<FrameworkElement>(window, "ApplicationDetails");
        var size = Required<Button>(window, "DetailsSizeButton");
        var initialHeight = details.Height;
        size.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        LayoutValidation.CheckWindowContent(window);
        Require(details.Height > initialHeight && Equals(size.Content, "Less space"), "More space must expand the inert details panel.");
        CheckDetailsActionBounds(details);
        CheckControlBounds(details, content);
        CheckFooter(window);
        size.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        LayoutValidation.CheckWindowContent(window);
        Require(Math.Abs(details.Height - initialHeight) < .5 && Equals(size.Content, "More space"), "Less space must recover the original row area.");
        var thumb = Required<Thumb>(window, "DetailsResizeThumb");
        Require(IsPresented(thumb) && !string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(thumb)),
            "The details resize handle needs a visible, named control.");
        CheckDetailsActionBounds(details);
    }

    private static void CheckActivityLayout(ActivityWindow window)
    {
        var content = (FrameworkElement)window.Content;
        var history = Required<DataGrid>(window, "HistoryGrid");
        var path = Required<TextBox>(window, "RecoveryPath");
        var open = Required<Button>(window, "OpenRecoveryButton");
        var gng = Required<Button>(window, "GngRecoveryButton");
        var close = LayoutValidation.Descendants(content).OfType<Button>().Single(button => button.IsCancel);
        CheckControlBounds(close, content);
        CheckButtonCaption(close);
        Require(history.IsReadOnly && history.ActualHeight >= 59.5 && !history.CanUserAddRows && !history.CanUserDeleteRows,
            "Activity history must remain a usable, read-only list.");
        CheckActivityColumns(window, history);
        if (UiFixture.Blank)
        {
            Require(history.Items.Count == 0 && !IsPresented(path) && !IsPresented(open) && !IsPresented(gng),
                "Empty history must not offer an unspecified recovery action.");
            return;
        }
        foreach (var entry in history.Items.Cast<ActivityEntry>())
        {
            history.SelectedItem = entry;
            LayoutValidation.CheckWindowContent(window);
            DrainBindings();
            Require(Required<TextBlock>(window, "SelectionHeading").Text == entry.Summary,
                "Activity details must describe the selected entry.");
            Require(IsPresented(path) == (entry.RecoveryFolder is not null) && IsPresented(open) == (entry.RecoveryFolder is not null),
                "The recovery path and folder action must match the selected synthetic entry.");
            Require(IsPresented(gng) == (entry.Application == ActivityApplication.Gng && entry.RecoveryFolder is not null),
                "Only the GNG history example should show GNG recovery.");
            if (entry.RestartRequired)
                Require(Required<TextBlock>(window, "SelectionGuidance").Text.Contains("cannot tell whether Windows has restarted", StringComparison.Ordinal),
                    "Historical restart guidance must not claim that a restart is still pending.");
            if (entry.RecoveryFolder is not null)
            {
                Require(path.IsReadOnly && path.Focusable && path.Text == entry.RecoveryFolder && path.TextWrapping == TextWrapping.Wrap,
                    "Recovery locations must remain complete, selectable and wrapped.");
                path.SelectAll(); Require(path.SelectedText == entry.RecoveryFolder, "The recovery location cannot be copied in full.");
            }
            foreach (var action in new[] { open, gng }.Where(IsPresented))
            {
                action.BringIntoView(); DrainBindings(); content.UpdateLayout();
                CheckControlBounds(action, content);
                CheckButtonCaption(action);
            }
            CheckControlBounds(close, content);
        }
        Require(!window.IsVisible, "An activity fixture showed a native window.");
    }

    private static void CheckActivityColumns(ActivityWindow window, DataGrid history)
    {
        // Materialize the three synthetic rows for text measurement. A never-shown
        // DataGrid does not reliably realize offscreen rows through ScrollIntoView.
        // Production virtualization remains unchanged; scroll extents are checked below.
        history.EnableRowVirtualization = false;
        history.EnableColumnVirtualization = false;
        VirtualizingPanel.SetIsVirtualizing(history, false);
        // DataGrid sizing is deferred even when measuring unshown fixture content.
        DrainBindings();
        LayoutValidation.CheckWindowContent(window);
        foreach (var column in history.Columns)
            Require(column.ActualWidth >= column.MinWidth - .5 && column.MinWidth >= 100,
                $"Activity column '{column.Header}' collapsed below its readable minimum: {column.ActualWidth:0.0}px.");
        Require(history.Columns.Take(4).All(column => column.Width.IsAuto),
            "Activity labels must grow with their contents and the selected text size.");
        foreach (var entry in history.Items.Cast<ActivityEntry>())
        foreach (var column in history.Columns)
        {
            history.ScrollIntoView(entry, column);
            // Auto column widths and horizontal scrolling each queue a layout pass.
            // Measuring an unopened window's content does not supply the render loop.
            for (int pass = 0; pass < 3; pass++)
            {
                DrainBindings();
                LayoutValidation.CheckWindowContent(window);
            }
            var text = column.GetCellContent(entry) as TextBlock;
            Require(text is not null && text.ActualWidth > 20 && text.ActualHeight >= text.FontSize,
                $"Activity value '{column.Header}' for '{entry.ApplicationName}' is not readable or reachable by scrolling: " +
                $"text {text?.ActualWidth:0.0}×{text?.ActualHeight:0.0}, font {text?.FontSize:0.0}, column {column.ActualWidth:0.0}, grid {history.ActualWidth:0.0}×{history.ActualHeight:0.0}.");
            CheckUntrimmedLabel(text!);
            if (Equals(column.Header, "Version"))
                Require(text!.TextWrapping == TextWrapping.Wrap && text.Text == entry.VersionChange,
                    "The complete before/after version must wrap within its column.");
        }
        if (history.Items.Count > 0)
        {
            var scroll = LayoutValidation.Descendants(history).OfType<ScrollViewer>().First();
            if (history.Columns.Sum(column => column.ActualWidth) > scroll.ViewportWidth + 1)
                Require(scroll.ScrollableWidth > 0,
                    "Activity columns wider than the viewport must have a horizontal scroll range.");
            scroll.ScrollToRightEnd();
            scroll.ScrollToBottom();
            for (int pass = 0; pass < 3; pass++)
            {
                DrainBindings();
                LayoutValidation.CheckWindowContent(window);
            }
            Require(scroll.HorizontalOffset >= scroll.ScrollableWidth - .5 &&
                scroll.VerticalOffset >= scroll.ScrollableHeight - .5,
                "Activity scrolling must reach the last column and last row.");
            scroll.ScrollToLeftEnd();
            scroll.ScrollToTop();
            DrainBindings(); LayoutValidation.CheckWindowContent(window);
        }
    }

    private static void CheckControlBounds(FrameworkElement control, FrameworkElement content)
    {
        var bounds = Bounds(control, content);
        Require(IsPresented(control) && bounds.Width > 10 && bounds.Height > 10 && bounds.Left >= -.5 && bounds.Top >= -.5 &&
            bounds.Right <= content.ActualWidth + .5 && bounds.Bottom <= content.ActualHeight + .5,
            $"{control.Name} lies outside available content: {bounds}, content {content.ActualWidth}×{content.ActualHeight}.");
    }

    private static void CheckButtonCaption(Button button)
    {
        if (button.Content is not string text || text.Length == 0) return;
        var natural = new FormattedText(text, CultureInfo.CurrentCulture, button.FlowDirection,
            new Typeface(button.FontFamily, button.FontStyle, button.FontWeight, button.FontStretch),
            button.FontSize, button.Foreground, VisualTreeHelper.GetDpi(button).PixelsPerDip);
        double width = button.ActualWidth - button.Padding.Left - button.Padding.Right - button.BorderThickness.Left - button.BorderThickness.Right;
        double height = button.ActualHeight - button.Padding.Top - button.Padding.Bottom - button.BorderThickness.Top - button.BorderThickness.Bottom;
        Require(natural.WidthIncludingTrailingWhitespace <= width + 1 && natural.Height <= height + 1,
            $"'{text}' is clipped at {button.FontSize}px: text {natural.WidthIncludingTrailingWhitespace:0.0}×{natural.Height:0.0}, available {width:0.0}×{height:0.0}.");
    }

    private static void CheckUntrimmedLabel(TextBlock label)
    {
        if (label.TextTrimming != TextTrimming.None || !IsPresented(label)) return;
        var natural = new FormattedText(label.Text, CultureInfo.CurrentCulture, label.FlowDirection,
            new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch),
            label.FontSize, label.Foreground, VisualTreeHelper.GetDpi(label).PixelsPerDip);
        if (label.TextWrapping != TextWrapping.NoWrap) natural.MaxTextWidth = Math.Max(1, label.ActualWidth);
        Require(natural.WidthIncludingTrailingWhitespace <= label.ActualWidth + 1 && natural.Height <= label.ActualHeight + 1,
            $"Untrimmed label '{label.Text}' is clipped at {label.FontSize}px.");
    }
}
