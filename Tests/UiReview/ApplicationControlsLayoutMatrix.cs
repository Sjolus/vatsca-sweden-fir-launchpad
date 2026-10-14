using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

internal static partial class LayoutMatrix
{
    internal static string RunApplicationControlsOnly()
    {
        LayoutValidation.CheckProgressBindingSource();
        int errorsBefore = UiFixture.ErrorCount;
        var trace = new BindingErrors();
        var source = PresentationTraceSources.DataBindingSource;
        var priorLevel = source.Switch.Level;
        source.Switch.Level = SourceLevels.Error;
        source.Listeners.Add(trace);
        int cases = 0;
        try
        {
            foreach (bool dark in new[] { false, true })
            {
                if (UiFixture.Dark != dark) UiFixture.ToggleTheme();
                CheckCompactDensity();
                cases++;
                cases += CheckApplicationControlsLayouts();
            }
            Require(trace.Errors.Count == 0, "WPF binding errors: " + string.Join(" | ", trace.Errors));
            Require(UiFixture.ErrorCount == errorsBefore, "An unhandled fixture exception was logged.");
            return $"PASS: {cases} hidden application-control layouts; both themes, 100/125/150% text, compact/expanded, default/minimum sizes, stopped/mixed/running/busy/browser-only/restart-required states. No windows shown or processes controlled.";
        }
        finally { source.Listeners.Remove(trace); source.Switch.Level = priorLevel; }
    }

    private static int CheckApplicationControlsLayouts()
    {
        int cases = 0;
        try
        {
            UiFixture.Blank = false;
            UiFixture.Scale = 1;
            foreach (double scale in new[] { 1d, 1.25d, 1.5d })
            foreach (bool minimum in new[] { false, true })
            foreach (string density in new[] { "Compact", "Comfortable" })
            {
                UiFixture.SetTextScale(scale);
                UiFixture.Minimum = minimum;
                var window = new MainWindow { Tag = density };
                try
                {
                    window.RefreshSyntheticDetailsLayout();
                    foreach (var state in Enum.GetValues<RunningFixtureState>())
                    {
                        UiFixture.SetRunningScenario(window, state);
                        DrainBindings();
                        LayoutValidation.CheckWindowContent(window);
                        CheckRunningRowControls(window, state);
                        CheckRowReadability(window);
                        CheckFooter(window);
                        cases++;
                    }
                }
                catch (Exception error)
                {
                    throw new InvalidDataException($"Application controls, {scale:P0}, {density}, {(minimum ? "minimum" : "default")}: {error.Message}", error);
                }
                finally { window.Close(); }
                if (density == "Compact")
                foreach (var action in Enum.GetValues<ApplicationBatchAction>())
                {
                    var review = UiFixture.CreateBulkReview(action, minimum);
                    try { CheckBulkReview(review, action); cases++; }
                    catch (Exception error) { throw new InvalidDataException($"Bulk {action}, {scale:P0}, {(minimum ? "minimum" : "default")}: {error.Message}", error); }
                    finally { review.Close(); }
                }
            }
        }
        finally { UiFixture.SetTextScale(1); UiFixture.Minimum = false; UiFixture.RestartRequired = false; }
        return cases;
    }

    private static void CheckRunningRowControls(MainWindow window, RunningFixtureState state)
    {
        var content = (FrameworkElement)window.Content;
        var list = Required<ItemsControl>(window, "AppList");
        foreach (var row in list.Items.Cast<CheckResult>())
        {
            var buttons = LayoutValidation.Descendants(list).OfType<Button>().Where(button => ReferenceEquals(button.DataContext, row)).ToArray();
            var details = buttons.Single(button => button.Name == "RowDetailsButton");
            details.BringIntoView(); DrainBindings(); LayoutValidation.CheckWindowContent(window);
            CheckControlBounds(details, content);
            var show = buttons.Single(button => button.Name == "RowShowButton");
            var close = buttons.Single(button => button.Name == "RowCloseButton");
            Require(IsPresented(show) == row.ShowRunningActions && IsPresented(close) == row.ShowRunningActions,
                row.DisplayName + " must expose both running controls together.");
            if (row.IsFolder || row.HasSelfUpdate) Require(!IsPresented(show), "Folders and Launchpad must not expose client-close controls.");
            if (!row.ShowRunningActions) continue;
            Require(!row.ShowSimpleLaunch && !row.ShowSplitLaunch, row.DisplayName + " shows launch and running actions together.");
            Require(show.IsEnabled == row.LaunchEnabled && close.IsEnabled == row.LaunchEnabled, row.DisplayName + " running controls ignore the operation gate.");
            foreach (var button in new[] { show, close })
            {
                button.BringIntoView();
                DrainBindings();
                LayoutValidation.CheckWindowContent(window);
                CheckButtonCaption(button);
                CheckControlBounds(button, content);
                Require(button.Focusable && button.IsTabStop, "Running controls must be keyboard reachable.");
                Require(AutomationProperties.GetName(button).Contains(row.DisplayName, StringComparison.Ordinal), "Running controls need an application-specific accessible name.");
            }
            var showBounds = Bounds(show, content);
            var closeBounds = Bounds(close, content);
            Require(showBounds.Right < closeBounds.Left && Math.Abs(showBounds.Top - closeBounds.Top) < .5,
                "Show and Close must be separate, adjacent controls on the same row.");
        }
        foreach (string name in new[] { "StartAllButton", "CloseAllButton" })
        {
            var button = Required<Button>(window, name);
            CheckControlBounds(button, content);
            CheckButtonCaption(button);
            Require(IsPresented(button) && button.Focusable && button.IsTabStop && button.ActualHeight >= 27.5,
                name + " must remain visible and keyboard reachable with a full click target.");
            Require(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)), name + " lacks an accessible name.");
            if (state == RunningFixtureState.Busy) Require(!button.IsEnabled, "Busy fixture must disable bulk controls.");
        }
        if (state == RunningFixtureState.BrowserWithoutBackend)
        {
            var browser = list.Items.Cast<CheckResult>().Single(row => row.IsLocalUrl);
            Require(!browser.IsLocalUrlReachable && browser.ShowRunningActions, "A tracked browser window must remain controllable after its backend stops.");
        }
        if (state == RunningFixtureState.RestartRequired)
        {
            Require(IsPresented(Required<TextBox>(window, "RestartNotice")), "The restart-required fixture must show its explanation.");
            Require(!Required<Button>(window, "StartAllButton").IsEnabled && Required<Button>(window, "CloseAllButton").IsEnabled,
                "A restart request must block bulk starts while keeping normal close available.");
            foreach (var row in list.Items.Cast<CheckResult>().Where(row => !row.IsFolder && !row.HasSelfUpdate))
            {
                Require(row.LaunchEnabled == row.ShowRunningActions, "A restart request must allow only the controls for already-running applications.");
                var launchButtons = LayoutValidation.Descendants(list).OfType<Button>()
                    .Where(button => ReferenceEquals(button.DataContext, row) && IsPresented(button) &&
                        ReferenceEquals(button.Style, window.FindResource("LaunchButton"))).ToArray();
                Require(launchButtons.Length == (row.ShowSimpleLaunch || row.ShowSplitLaunch ? 1 : 0),
                    "Restart checks must observe the actual visible launch control.");
                Require(launchButtons.All(button => !button.IsEnabled), "A stopped application's launch control must stay disabled until restart.");
            }
        }
        Require(!window.IsVisible, "Application-control layout testing must not show native windows.");
    }

    private static void CheckBulkReview(BulkApplicationActionWindow window, ApplicationBatchAction action)
    {
        LayoutValidation.CheckWindowContent(window);
        var content = (FrameworkElement)window.Content;
        var apply = Required<Button>(window, "ApplyButton");
        var cancel = LayoutValidation.Descendants(content).OfType<Button>().Single(button => button.IsCancel);
        foreach (var button in new[] { apply, cancel }) { CheckControlBounds(button, content); CheckButtonCaption(button); }
        Require(cancel.IsDefault && !apply.IsDefault, "Bulk actions must keep Cancel as the safe Enter-key default.");
        var list = Required<ItemsControl>(window, "ApplicationChoices");
        var scroll = LayoutValidation.Descendants(content).OfType<ScrollViewer>().First(view => view.Content == list);
        Require(scroll.ViewportHeight >= 60, "The reviewed application list has too little readable space.");
        Require(window.Choices.Count == 6 && window.Choices.Any(choice => !choice.CanSelect), "Bulk review must show eligible and skipped applications.");
        foreach (var checkbox in LayoutValidation.Descendants(list).OfType<CheckBox>())
        {
            var choice = (ApplicationActionChoice)checkbox.DataContext;
            checkbox.BringIntoView(); DrainBindings(); LayoutValidation.CheckWindowContent(window);
            Require(checkbox.IsEnabled == choice.CanSelect && checkbox.IsChecked == choice.IsSelected,
                "Bulk selection availability does not match its reviewed application.");
            Require(AutomationProperties.GetName(checkbox) == choice.ApplicationName, "Bulk checkboxes need application names for keyboard/screen-reader use.");
        }
        foreach (var choice in window.Choices) choice.IsSelected = false;
        Require(!apply.IsEnabled, "An empty bulk selection must not be confirmed.");
        window.Choices.First(choice => choice.CanSelect).IsSelected = true;
        Require(apply.IsEnabled && Required<TextBlock>(window, "SelectionSummary").Text.StartsWith("1 application selected", StringComparison.Ordinal), "Bulk review must update its selection count.");
        var explanation = Required<TextBlock>(window, "Explanation").Text;
        if (action == ApplicationBatchAction.Close) Require(explanation.Contains("will not force", StringComparison.Ordinal), "Normal bulk close must not imply forced termination.");
        Require(!window.IsVisible, "Bulk review layout tests must never show native windows.");
    }
}
