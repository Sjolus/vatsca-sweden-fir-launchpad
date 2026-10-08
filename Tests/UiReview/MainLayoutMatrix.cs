using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

internal static partial class LayoutMatrix
{
    private static void CheckRowReadability(MainWindow window)
    {
        var list = Required<ItemsControl>(window, "AppList");
        var expectedHeight = Equals(window.Tag, "Comfortable") ? 56 : 38;
        var heights = Enumerable.Range(0, list.Items.Count)
            .Select(index => ((FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(index)).ActualHeight).ToArray();
        Require(heights.All(height => Math.Abs(height - expectedHeight) < .5),
            $"Application rows must keep equal {expectedHeight}px heights across mixed states: {string.Join(", ", heights)}.");

        foreach (var text in LayoutValidation.Descendants(list).OfType<TextBlock>())
        {
            var path = BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path?.Path;
            if (path is "DisplayName" or "RowStatusText")
                Require(text.FontSize >= 13, path + " text is too small.");
            if (path is "DisplayInstalledVersion" or "DisplayLatestVersion" or "RowExplanation")
                Require(text.FontSize >= 12, path + " text is too small.");
        }
        foreach (var button in LayoutValidation.Descendants(list).OfType<Button>().Where(IsPresented))
        {
            Require(button.ActualHeight >= 27.5, "Row action has a click target shorter than 28px.");
            if (button.Content is string label && label is not ("▶" or "■" or "📁" or "▾"))
                Require(button.FontSize >= 12, label + " action text is too small.");
        }
    }

    private static FrameworkElement SelectDetails(MainWindow window, CheckResult row)
    {
        window.SelectSyntheticDetails(row);
        DrainBindings();
        LayoutValidation.CheckWindowContent(window);
        var panel = Required<FrameworkElement>(window, "ApplicationDetails");
        Require(ReferenceEquals(panel.DataContext, row), "The details panel has the wrong application context.");
        Require(IsPresented(panel), "Selecting application details did not reveal its panel.");
        var rows = Required<ItemsControl>(window, "AppList").Items.Cast<CheckResult>().ToArray();
        Require(rows.Count(item => item.IsSelected) == 1 && row.IsSelected,
            "Exactly one application must be selected and match the details panel.");
        var heading = Required<TextBlock>(window, "DetailHeading");
        Require(heading.Text == row.DetailHeading && !string.IsNullOrWhiteSpace(heading.Text),
            "Details must identify the selected application.");
        var message = Required<TextBox>(window, "DetailMessage");
        Require(message.IsReadOnly && message.Focusable && message.IsTabStop &&
            message.Text == row.DetailText, "Full application details must remain readable and selectable.");
        CheckDetailsActionBounds(panel);
        return panel;
    }

    private static void CheckDetailsActionBounds(FrameworkElement panel)
    {
        foreach (var button in LayoutValidation.Descendants(panel).OfType<Button>().Where(IsPresented))
        {
            var bounds = Bounds(button, panel);
            Require(bounds.Width > 18 && bounds.Height >= 19.5 && bounds.Left >= -.5 && bounds.Top >= -.5 &&
                bounds.Right <= panel.ActualWidth + .5 && bounds.Bottom <= panel.ActualHeight + .5,
                $"{button.Name} is clipped or unreachable inside the application details panel: {bounds}, panel {panel.ActualWidth}×{panel.ActualHeight}.");
        }
    }

    private static void CheckFirstRunActions(MainWindow window)
    {
        var list = Required<ItemsControl>(window, "AppList");
        var euroscope = list.Items.Cast<CheckResult>().Single(row => row.HasEuroScopeManagement);
        var panel = SelectDetails(window, euroscope);
        var setup = Bound<Button>(panel, ContentControl.ContentProperty, "EuroScopeSetupText", euroscope);
        Require(IsPresented(setup) && Equals(setup.Content, euroscope.EuroScopeSetupText),
            "EuroScope setup/management must remain reachable in its details.");
        if (UiFixture.Blank) Require(euroscope.StatusText == "Not set up", "Unconfigured EuroScope needs an actionable first-run status.");

        foreach (var row in list.Items.Cast<CheckResult>().Where(row => row.HasFontsCheck || row.IsLocalUrl))
        {
            panel = SelectDetails(window, row);
            var configure = Required<Button>(window, "DetailConfigureButton");
            Require(IsPresented(configure) == (UiFixture.Blank && !row.HasSoftwareUpdate),
                "Package/plugin configuration action does not match the synthetic path state.");
            Require(!string.IsNullOrWhiteSpace(configure.ToolTip as string),
                "Configuration action needs folder-specific guidance.");
            var previous = row.SoftwareActionsAllowed;
            row.SoftwareActionsAllowed = false;
            DrainBindings();
            Require(!configure.IsEnabled, "Configuration must be disabled during another guarded operation.");
            if (row.HasFontsCheck)
            {
                var cleanup = Required<Button>(window, "DetailCleanupButton");
                Require(IsPresented(cleanup) == row.ShowGngMaintenance && !cleanup.IsEnabled && cleanup.Focusable && cleanup.IsTabStop,
                    "GNG cleanup needs a configured data folder and must obey the guarded-operation gate.");
                var update = Required<Button>(window, "DetailGngUpdateButton");
                Require(IsPresented(update) && !update.IsEnabled && update.Focusable && update.IsTabStop,
                    "GNG setup/update must remain discoverable and obey SoftwareActionsAllowed.");
                Require(!string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(update)),
                    "GNG setup/update must have an accessible action name.");
            }
            row.SoftwareActionsAllowed = previous;
            DrainBindings();
            if (row.HasFontsCheck)
                Require(Required<Button>(window, "DetailGngUpdateButton").IsEnabled == previous,
                    "GNG setup/update did not follow the released operation gate.");
        }
        foreach (var row in list.Items.Cast<CheckResult>().Where(row => row.HasSoftwareUpdate))
        {
            panel = SelectDetails(window, row);
            Require(IsPresented(Bound<Button>(panel, ContentControl.ContentProperty, "SoftwareSetupText", row)) == row.ShowSoftwareSetup,
                "The setup/management route must remain reachable except while that application is busy.");
            var message = Required<TextBox>(window, "DetailMessage");
            if (UiFixture.Blank)
                Require(message.Text.Contains("install", StringComparison.OrdinalIgnoreCase) &&
                    (message.Text.Contains("existing", StringComparison.OrdinalIgnoreCase) || message.Text.Contains("find", StringComparison.OrdinalIgnoreCase)),
                    "Missing-install guidance must explain existing-copy and fresh-install choices.");
        }
    }

    private static void CheckCompactDensity()
    {
        UiFixture.Blank = UiFixture.Minimum = false;
        UiFixture.Scale = 1;
        var window = new MainWindow { Width = 880, Height = 550, Tag = "Compact" };
        try
        {
            window.RefreshSyntheticDetailsLayout();
            LayoutValidation.CheckWindowContent(window);
            DrainBindings();
            LayoutValidation.CheckWindowContent(window);
            var list = Required<ItemsControl>(window, "AppList");
            var scroll = Required<ScrollViewer>(window, "ApplicationsScroll");
            var panel = Required<FrameworkElement>(window, "ApplicationDetails");
            Require(list.Items.Count == 8, "Density case must exercise all eight application rows.");
            Require(IsPresented(panel), "Compact mode must always expose its shared details panel.");
            Require(ReferenceEquals(panel.DataContext, list.Items[0]), "The initial details must identify the first application.");
            Require(scroll.ExtentHeight <= scroll.ViewportHeight + .5,
                $"Compact mixed-state rows require scrolling at 880×550: extent {scroll.ExtentHeight}, viewport {scroll.ViewportHeight}.");
            CheckRowReadability(window);
            CheckFooter(window);

            Required<Button>(window, "DensityToggleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            DrainBindings();
            LayoutValidation.CheckWindowContent(window);
            Require(Equals(window.Tag, "Comfortable") && !IsPresented(panel),
                "Expanded mode should initially show explanations without an explicitly opened details panel.");
            CheckRowReadability(window);
            Required<Button>(window, "DensityToggleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            DrainBindings();
            LayoutValidation.CheckWindowContent(window);

            foreach (var width in new[] { 760d, 880d })
            {
                window.Width = width;
                LayoutValidation.CheckWindowContent(window);
                CheckRowReadability(window);
                Require(scroll.ExtentWidth <= scroll.ViewportWidth + .5,
                    $"Application columns require horizontal scrolling at {width}px.");
                CheckDetailsSelection(window);
                CheckFooter(window);
            }

            Required<Button>(window, "DensityToggleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            DrainBindings();
            LayoutValidation.CheckWindowContent(window);
            Require(Equals(window.Tag, "Comfortable") && Equals(Required<Button>(window, "DensityToggleButton").Content, "Expanded"),
                "Density toggle does not expose its comfortable state.");
            Require(IsPresented(panel), "Changing density must retain application details the user explicitly opened.");
            CheckRowReadability(window);
            CheckDetailsSelection(window);
            var close = Required<Button>(window, "DetailsCloseButton");
            Require(IsPresented(close), "Comfortable details need a visible Close action.");
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            DrainBindings();
            Require(!IsPresented(panel), "Closing Comfortable details must return to the explanatory rows.");
            Required<Button>(window, "DensityToggleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            DrainBindings();
            LayoutValidation.CheckWindowContent(window);
            Require(IsPresented(panel) && !IsPresented(close), "Compact details must remain present without a Close action.");
            CheckRowReadability(window);
            CheckFooter(window);
        }
        finally { window.Close(); }
    }

    private static void CheckDetailsSelection(MainWindow window)
    {
        var list = Required<ItemsControl>(window, "AppList");
        var scroll = Required<ScrollViewer>(window, "ApplicationsScroll");
        foreach (var row in list.Items.Cast<CheckResult>())
        {
            var details = LayoutValidation.Descendants(list).OfType<Button>()
                .Single(button => ReferenceEquals(button.DataContext, row) && button.Name == "RowDetailsButton");
            Require(IsPresented(details) && details.IsEnabled && details.Focusable && details.IsTabStop,
                "Every application needs a visible, keyboard-focusable Details action.");
            details.BringIntoView();
            DrainBindings();
            ((FrameworkElement)window.Content).UpdateLayout();
            var bounds = Bounds(details, scroll);
            Require(bounds.Left >= -.5 && bounds.Right <= scroll.ViewportWidth + .5 &&
                bounds.Top >= -.5 && bounds.Bottom <= scroll.ViewportHeight + .5,
                "An application's Details action cannot be reached by scrolling.");
            details.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            DrainBindings();
            var panel = Required<FrameworkElement>(window, "ApplicationDetails");
            Require(IsPresented(panel) && ReferenceEquals(panel.DataContext, row) && row.IsSelected,
                "A row's actual Details action did not select that application.");
        }
    }

    private static void CheckSoftwareStates(MainWindow window)
    {
        var list = Required<ItemsControl>(window, "AppList");
        var row = list.Items.Cast<CheckResult>().Single(item => item.SoftwareApp == SoftwareApp.Vacs);
        var originalPath = row.LaunchPath;
        var originalState = row.SoftwareUpdate;
        var originalAllowed = row.SoftwareActionsAllowed;
        var originalMessage = row.StatusMessage;
        try
        {
            var panel = SelectDetails(window, row);
            foreach (var path in new[] { "", Path.Combine(AppContext.BaseDirectory, "fixture-data", "missing-never-created.exe") })
            {
                row.LaunchPath = path;
                row.SoftwareUpdate = new(SoftwareApp.Vacs, SoftwareUpdatePhase.Unavailable, "Synthetic absent installation");
                DrainBindings();
                Require(row.SoftwareNeedsSetup && !row.ShowSoftwareAction && row.ShowSoftwareSetup,
                    "Missing software must offer setup rather than an update/download action.");
                var action = Bound<Button>(panel, ContentControl.ContentProperty, "SoftwareActionText", row);
                var setup = Bound<Button>(panel, ContentControl.ContentProperty, "SoftwareSetupText", row);
                Require(!IsPresented(action) && IsPresented(setup), "Missing application details do not present a single setup route.");
            }

            row.LaunchPath = UiFixture.SampleExecutable();
            foreach (var (phase, percent, busy, cancellable) in new[]
            {
                (SoftwareUpdatePhase.Downloading, (double?)37, true, true),
                (SoftwareUpdatePhase.Downloading, (double?)null, true, true),
                (SoftwareUpdatePhase.BackingUp, (double?)null, true, true),
                (SoftwareUpdatePhase.Installing, (double?)null, true, false),
                (SoftwareUpdatePhase.Unavailable, (double?)null, false, false),
                (SoftwareUpdatePhase.Current, (double?)null, false, false),
                (SoftwareUpdatePhase.Error, (double?)null, false, false),
                (SoftwareUpdatePhase.Cancelled, (double?)null, false, false)
            })
            {
                var message = phase == SoftwareUpdatePhase.Unavailable
                    ? "Synthetic installation cannot be updated because its registration does not match."
                    : "Synthetic phase " + phase;
                row.SoftwareUpdate = new(SoftwareApp.Vacs, phase, message, ProgressPercent: percent);
                row.StatusMessage = message;
                DrainBindings();
                LayoutValidation.CheckWindowContent(window);
                var progress = Bound<ProgressBar>(list, ProgressBar.ValueProperty, "SoftwareProgressValue", row);
                var cancel = Required<Button>(window, "DetailSoftwareCancelButton");
                var detail = Required<TextBox>(window, "DetailMessage");
                Require(detail.Text.Contains(message, StringComparison.Ordinal) && IsPresented(detail),
                    phase + ": full diagnostics must remain visible in the selected application's details.");
                Require(IsPresented(progress) == busy, phase + ": progress visibility is wrong.");
                Require(progress.IsIndeterminate == (busy && percent is null), phase + ": progress mode is wrong.");
                if (percent.HasValue) Require(Math.Abs(progress.Value - percent.Value) < .01, "Download percentage did not update through model notifications.");
                Require(IsPresented(cancel) == cancellable, phase + ": cancellation is offered at the wrong boundary.");
                Require(row.LaunchEnabled == !busy, phase + ": launch is allowed while an update is busy.");
                CheckRowReadability(window);
                CheckDetailsActionBounds(panel);
            }
            row.SoftwareUpdate = new(SoftwareApp.Vacs, SoftwareUpdatePhase.Available, "Synthetic available update");
            row.SoftwareActionsAllowed = false;
            DrainBindings();
            Require(!Bound<Button>(panel, ContentControl.ContentProperty, "SoftwareActionText", row).IsEnabled,
                "Update action remained enabled during another guarded operation.");
            Require(!Bound<Button>(panel, ContentControl.ContentProperty, "SoftwareSetupText", row).IsEnabled,
                "Setup action remained enabled during another guarded operation.");

            var other = list.Items.Cast<CheckResult>().Single(item => item.SoftwareApp == SoftwareApp.TrackAudio);
            SelectDetails(window, other);
            row.SoftwareUpdate = new(SoftwareApp.Vacs, SoftwareUpdatePhase.Downloading, "Synthetic background download", ProgressPercent: 72);
            DrainBindings();
            Require(Bound<TextBlock>(list, TextBlock.TextProperty, "RowStatusText", row).Text.Contains("72", StringComparison.Ordinal),
                "A background update needs visible progress in its row while another application is selected.");

            var self = list.Items.Cast<CheckResult>().Single(item => item.HasSelfUpdate);
            panel = SelectDetails(window, self);
            self.SelfUpdateBusy = true;
            self.SelfUpdateProgress = null;
            DrainBindings();
            var selfBar = Bound<ProgressBar>(list, ProgressBar.ValueProperty, "SelfUpdateProgressValue", self);
            Require(IsPresented(selfBar) && selfBar.IsIndeterminate, "Self-update preparation needs indeterminate progress.");
            self.SelfUpdateProgress = 65;
            DrainBindings();
            Require(!selfBar.IsIndeterminate && Math.Abs(selfBar.Value - 65) < .01, "Self-update progress did not switch to percentage.");
            self.SelfUpdateBusy = false;
            self.SelfUpdateProgress = null;
        }
        finally { row.LaunchPath = originalPath; row.SoftwareUpdate = originalState; row.SoftwareActionsAllowed = originalAllowed; row.StatusMessage = originalMessage; }
    }

    private static void CheckLongStatusWidth(MainWindow window)
    {
        var list = Required<ItemsControl>(window, "AppList");
        var scroll = Required<ScrollViewer>(window, "ApplicationsScroll");
        var row = list.Items.Cast<CheckResult>().Single(item => item.SoftwareApp == SoftwareApp.Vacs);
        var originalPath = row.LaunchPath;
        var originalState = row.SoftwareUpdate;
        var originalMessage = row.StatusMessage;
        try
        {
            row.LaunchPath = UiFixture.SampleExecutable();
            row.SoftwareUpdate = new(SoftwareApp.Vacs, SoftwareUpdatePhase.Error, "Synthetic recovery message");
            row.StatusMessage = "Synthetic verification failure. Recovery export: C:/synthetic-recovery/" + new string('x', 1000) + "/restore-instructions.txt";
            var panel = SelectDetails(window, row);
            var message = Required<TextBox>(window, "DetailMessage");
            Require(scroll.ViewportWidth > 0 && list.ActualWidth > 0, "Long-status case has no measured table viewport.");
            Require(scroll.ExtentWidth <= scroll.ViewportWidth + 1,
                $"Long diagnostic expanded the table: extent {scroll.ExtentWidth}, viewport {scroll.ViewportWidth}.");
            Require(message.Text.Contains(row.StatusMessage, StringComparison.Ordinal) && message.ActualWidth < window.Width,
                "The complete recovery diagnostic must be retained within the shared details panel.");
            Require(message.TextWrapping != TextWrapping.NoWrap && message.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
                "Long recovery details must wrap and remain scrollable.");
            foreach (var path in new[] { "SoftwareActionText", "SoftwareSetupText" })
            {
                var button = Bound<Button>(panel, ContentControl.ContentProperty, path, row);
                var bounds = Bounds(button, panel);
                Require(IsPresented(button) && bounds.Width > 20 && bounds.Left >= -.5 && bounds.Right <= panel.ActualWidth + .5,
                    path + " became horizontally unreachable after a long recovery diagnostic.");
            }
            CheckRowReadability(window);
            CheckFooter(window);
        }
        finally { row.LaunchPath = originalPath; row.SoftwareUpdate = originalState; row.StatusMessage = originalMessage; }
    }
}
