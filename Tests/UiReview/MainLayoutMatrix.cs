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
        var expectedHeight = (double)Application.Current.FindResource(Equals(window.Tag, "Comfortable") ? "ExpandedRowHeight" : "CompactRowHeight");
        var heights = Enumerable.Range(0, list.Items.Count)
            .Select(index => ((FrameworkElement)list.ItemContainerGenerator.ContainerFromIndex(index)).ActualHeight).ToArray();
        Require(heights.All(height => Math.Abs(height - expectedHeight) < .5),
            $"Application rows must keep equal {expectedHeight}px heights across mixed states: {string.Join(", ", heights)}.");
        foreach (var row in list.Items.Cast<CheckResult>())
        {
            var label = LayoutValidation.Descendants(list).OfType<StackPanel>()
                .Single(panel => panel.Name == "ApplicationLabel" && ReferenceEquals(panel.DataContext, row));
            Require(Math.Abs(label.Margin.Left - (row.IsEuroScopeChild ? 12 : 0)) < .5,
                row.DisplayName + " must use the shared EuroScope-child indent.");
        }

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

    private static Button RowButton(MainWindow window, CheckResult row, string name) =>
        LayoutValidation.Descendants(Required<ItemsControl>(window, "AppList")).OfType<Button>()
            .Single(button => ReferenceEquals(button.DataContext, row) && button.Name == name);

    private static TextBox RowStatus(MainWindow window, CheckResult row) =>
        LayoutValidation.Descendants(Required<ItemsControl>(window, "AppList")).OfType<TextBox>()
            .Single(status => ReferenceEquals(status.DataContext, row) && status.Name == "RowStatusMessage");

    private static void CheckRowDiagnostics(MainWindow window, CheckResult row)
    {
        var status = RowStatus(window, row);
        Require(status.Text == row.RowStatusText && status.IsReadOnly && status.Focusable && status.IsTabStop,
            "Application status must remain selectable and keyboard reachable.");
        Require(System.Windows.Automation.AutomationProperties.GetHelpText(status) == row.DetailText,
            "Assistive technology must retain the complete diagnostic text.");
        var tooltip = status.ToolTip as ToolTip ?? throw new InvalidDataException("Missing status tooltip.");
        tooltip.DataContext = row;
        var tooltipText = tooltip.Content as TextBlock ?? throw new InvalidDataException("Missing diagnostic text.");
        tooltipText.GetBindingExpression(TextBlock.TextProperty)?.UpdateTarget();
        Require(tooltipText.Text.Contains(row.DetailText, StringComparison.Ordinal) && tooltipText.Text.Contains("Ctrl+C", StringComparison.Ordinal),
            "Status tooltip must retain the complete diagnostic text and explain copying it.");
        Require(tooltipText.TextWrapping == TextWrapping.Wrap && tooltip.MaxWidth <= 440 && status.FontSize >= 13,
            "Status diagnostics need readable, bounded presentation.");
        System.Windows.Input.ApplicationCommands.Copy.Execute(null, status);
        Require(ReferenceEquals(window.RequestedStatusCopy, row), "Ctrl+C must route the requested application to the inert copy handler.");
    }

    private static void CheckRowActions(MainWindow window)
    {
        var list = Required<ItemsControl>(window, "AppList");
        var scroll = Required<ScrollViewer>(window, "ApplicationsScroll");
        Require(window.FindName("ApplicationDetails") is null, "The removed details panel must not reserve space.");
        Require(!LayoutValidation.Descendants(list).OfType<Button>().Any(button => button.Name == "RowMoreButton" || Equals(button.Content, "⋯")),
            "The application list must not reintroduce per-row overflow menus.");
        foreach (var row in list.Items.Cast<CheckResult>())
        {
            var status = RowStatus(window, row);
            status.BringIntoView(); DrainBindings(); LayoutValidation.CheckWindowContent(window);
            Require(IsPresented(status) && status.IsEnabled, "Each row needs reachable status text.");
            var bounds = Bounds(status, scroll);
            Require(bounds.Left >= -.5 && bounds.Right <= scroll.ViewportWidth + .5 && bounds.Top >= -.5 && bounds.Bottom <= scroll.ViewportHeight + .5,
                "Row status cannot be reached by scrolling.");
            var action = RowButton(window, row, "RowActionButton");
            Require(IsPresented(action) == row.ShowRowAction && action.IsEnabled == row.RowActionEnabled && Equals(action.Content, row.RowActionText),
                row.DisplayName + " action does not follow its model state.");
            foreach (var button in new[] { action }.Where(IsPresented))
            {
                Require(button.Focusable && button.IsTabStop, "Row actions must remain keyboard reachable.");
                Require(System.Windows.Automation.AutomationProperties.GetName(button).Contains(row.DisplayName, StringComparison.Ordinal), "Row actions need application-specific accessible names.");
                CheckButtonCaption(button);
            }
            CheckRowDiagnostics(window, row);
        }
    }

    private static void CheckFirstRunActions(MainWindow window)
    {
        CheckRowActions(window);
        foreach (var row in Required<ItemsControl>(window, "AppList").Items.Cast<CheckResult>())
        {
            var previous = row.SoftwareActionsAllowed;
            row.SoftwareActionsAllowed = false; DrainBindings();
            if (!row.SoftwareCanCancel && (row.HasSoftwareUpdate || row.HasFontsCheck || row.HasEuroScopeManagement || row.HasSaamanManagement))
                Require(!RowButton(window, row, "RowActionButton").IsEnabled, "Guarded actions must disable during another operation.");
            row.SoftwareActionsAllowed = previous; DrainBindings();
            if (UiFixture.Blank && row.HasSoftwareUpdate)
                Require(row.SoftwareNeedsSetup && row.ShowRowAction && row.RowActionText.Contains("Set", StringComparison.OrdinalIgnoreCase), "Missing software needs a direct setup route.");
        }
    }

    private static void CheckCompactDensity()
    {
        UiFixture.Blank = UiFixture.Minimum = false; UiFixture.Scale = 1;
        var window = new MainWindow { Width = 880, Height = 590, Tag = "Compact" };
        try
        {
            LayoutValidation.CheckWindowContent(window);
            var list = Required<ItemsControl>(window, "AppList");
            var scroll = Required<ScrollViewer>(window, "ApplicationsScroll");
            CheckPersistentSelectionStyling(window);
            Require(list.Items.Count == 9, "Density case must include all nine rows.");
            Require(scroll.ExtentHeight <= scroll.ViewportHeight + .5, "Compact rows must fit at 880x590 without vertical scrolling.");
            foreach (var width in new[] { 760d, 880d })
            {
                window.Width = width; LayoutValidation.CheckWindowContent(window);
                Require(scroll.ExtentWidth <= scroll.ViewportWidth + .5,
                    $"Columns require horizontal scrolling at {width}px: extent {scroll.ExtentWidth:0.0}, viewport {scroll.ViewportWidth:0.0}.");
                CheckRowReadability(window); CheckRowActions(window); CheckFooter(window);
            }
            window.Tag = "Comfortable";
            DrainBindings(); LayoutValidation.CheckWindowContent(window);
            Require(Equals(window.Tag, "Comfortable"), "Density must switch to expanded rows.");
            CheckRowReadability(window); CheckRowActions(window);
            window.Tag = "Compact";
            DrainBindings(); LayoutValidation.CheckWindowContent(window);
            Require(Equals(window.Tag, "Compact"), "Density must return to compact rows without opening a panel.");
            CheckRowReadability(window); CheckFooter(window);
        }
        finally { window.Close(); }
    }

    private static void CheckPersistentSelectionStyling(MainWindow window)
    {
        var list = Required<ItemsControl>(window, "AppList");
        var row = list.Items.Cast<CheckResult>().Single(item => item.SoftwareApp == SoftwareApp.VatEfs);
        var shellStyle = window.FindResource("RowShell");
        Border Shell(CheckResult item) => LayoutValidation.Descendants(list).OfType<Border>()
            .Single(border => ReferenceEquals(border.DataContext, item) && ReferenceEquals(border.Style, shellStyle));
        var shell = Shell(row);
        var background = shell.Background;
        var original = row.IsSelected;
        try
        {
            row.IsSelected = true;
            DrainBindings();
            LayoutValidation.CheckWindowContent(window);
            Require(Equals(shell.Background, background), "An earlier application action must not leave its row highlighted.");
            foreach (var sibling in list.Items.Cast<CheckResult>().Where(item => item.IsEuroScopeChild))
                Require(Equals(shell.Background, Shell(sibling).Background), "EuroScope child rows must keep the same background after selection.");
        }
        finally { row.IsSelected = original; DrainBindings(); }
    }

    private static void CheckSoftwareStates(MainWindow window)
    {
        var list = Required<ItemsControl>(window, "AppList");
        var row = list.Items.Cast<CheckResult>().Single(item => item.SoftwareApp == SoftwareApp.Vacs);
        var originalPath = row.LaunchPath; var originalState = row.SoftwareUpdate;
        var originalAllowed = row.SoftwareActionsAllowed; var originalMessage = row.StatusMessage;
        try
        {
            foreach (var path in new[] { "", Path.Combine(AppContext.BaseDirectory, "fixture-data", "missing-never-created.exe") })
            {
                row.LaunchPath = path; row.SoftwareUpdate = new(SoftwareApp.Vacs, SoftwareUpdatePhase.Unavailable, "Synthetic absent installation");
                DrainBindings();
                Require(row.SoftwareNeedsSetup && row.ShowRowAction && row.RowActionText.Contains("Set", StringComparison.OrdinalIgnoreCase), "Missing software must offer setup, not update/download.");
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
                var message = "Synthetic phase " + phase;
                row.SoftwareUpdate = new(SoftwareApp.Vacs, phase, message, ProgressPercent: percent); row.StatusMessage = message;
                DrainBindings(); LayoutValidation.CheckWindowContent(window);
                var progress = Bound<ProgressBar>(list, ProgressBar.ValueProperty, "SoftwareProgressValue", row);
                var action = RowButton(window, row, "RowActionButton");
                Require(IsPresented(progress) == busy && progress.IsIndeterminate == (busy && percent is null), phase + ": incorrect progress.");
                if (percent.HasValue) Require(Math.Abs(progress.Value - percent.Value) < .01, "Download percentage did not update.");
                Require((IsPresented(action) && Equals(action.Content, "Cancel")) == cancellable, phase + ": cancellation is offered at the wrong boundary.");
                Require(row.LaunchEnabled == !busy, phase + ": launch allowed while busy.");
                Require(row.DetailText.Contains(message, StringComparison.Ordinal), "State diagnostics were lost.");
                CheckRowReadability(window); CheckRowDiagnostics(window, row);
            }
            row.SoftwareUpdate = new(SoftwareApp.Vacs, SoftwareUpdatePhase.Available, "Synthetic available update"); row.SoftwareActionsAllowed = false;
            DrainBindings(); Require(!RowButton(window, row, "RowActionButton").IsEnabled, "Update remained enabled during another operation.");
            row.SoftwareActionsAllowed = originalAllowed;
            window.FocusSyntheticRow(list.Items.Cast<CheckResult>().Single(item => item.SoftwareApp == SoftwareApp.TrackAudio));
            row.SoftwareUpdate = new(SoftwareApp.Vacs, SoftwareUpdatePhase.Downloading, "Synthetic background download", ProgressPercent: 72); DrainBindings();
            Require(RowStatus(window, row).Text.Contains("72", StringComparison.Ordinal), "Background progress must be visible without selection.");
            var self = list.Items.Cast<CheckResult>().Single(item => item.HasSelfUpdate);
            self.SelfUpdateBusy = true; self.SelfUpdateProgress = null; DrainBindings();
            var selfBar = Bound<ProgressBar>(list, ProgressBar.ValueProperty, "SelfUpdateProgressValue", self);
            Require(IsPresented(selfBar) && selfBar.IsIndeterminate, "Self-update preparation needs indeterminate progress.");
            self.SelfUpdateProgress = 65; DrainBindings();
            Require(!selfBar.IsIndeterminate && Math.Abs(selfBar.Value - 65) < .01, "Self-update progress did not switch to percentage.");
            self.SelfUpdateBusy = false; self.SelfUpdateProgress = null;
        }
        finally { row.LaunchPath = originalPath; row.SoftwareUpdate = originalState; row.SoftwareActionsAllowed = originalAllowed; row.StatusMessage = originalMessage; }
    }

    private static void CheckLongStatusWidth(MainWindow window)
    {
        var list = Required<ItemsControl>(window, "AppList"); var scroll = Required<ScrollViewer>(window, "ApplicationsScroll");
        var row = list.Items.Cast<CheckResult>().Single(item => item.SoftwareApp == SoftwareApp.Vacs);
        var originalPath = row.LaunchPath; var originalState = row.SoftwareUpdate; var originalMessage = row.StatusMessage;
        try
        {
            row.LaunchPath = UiFixture.SampleExecutable(); row.SoftwareUpdate = new(SoftwareApp.Vacs, SoftwareUpdatePhase.Error, "Synthetic recovery message");
            row.StatusMessage = "Synthetic verification failure. Recovery export: C:/synthetic-recovery/" + new string('x', 1000) + "/restore-instructions.txt";
            DrainBindings(); LayoutValidation.CheckWindowContent(window);
            Require(scroll.ViewportWidth > 0 && scroll.ExtentWidth <= scroll.ViewportWidth + 1,
                $"Long diagnostics must not expand the table: window {window.Width}x{window.Height}, extent {scroll.ExtentWidth:0.0}, viewport {scroll.ViewportWidth:0.0}.");
            Require(row.DetailText.Contains(row.StatusMessage, StringComparison.Ordinal), "Complete recovery text was lost.");
            CheckRowDiagnostics(window, row); CheckRowActions(window); CheckRowReadability(window); CheckFooter(window);
        }
        finally { row.LaunchPath = originalPath; row.SoftwareUpdate = originalState; row.StatusMessage = originalMessage; }
    }
}
