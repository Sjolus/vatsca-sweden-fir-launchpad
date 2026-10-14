using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

internal static class LayoutSnapshots
{
    internal static void CaptureApplicationControls(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        try
        {
            UiFixture.Blank = false;
            UiFixture.Scale = 1;
            foreach (bool dark in new[] { false, true })
            foreach (double scale in new[] { 1d, 1.25d, 1.5d })
            foreach (bool minimum in new[] { false, true })
            foreach (string density in new[] { "Compact", "Comfortable" })
            {
                if (UiFixture.Dark != dark) UiFixture.ToggleTheme();
                UiFixture.SetTextScale(scale);
                UiFixture.Minimum = minimum;
                var window = new MainWindow { Tag = density };
                try
                {
                    window.RefreshSyntheticDetailsLayout();
                    UiFixture.SetRunningScenario(window, RunningFixtureState.Mixed);
                    ((TextBlock)window.FindName("ApplicationsSummary")).Text = "Applications · 2 updates · 1 to review";
                    Save(window, Path.Combine(outputDirectory, $"{(dark ? "dark" : "light")}-controls-{density.ToLowerInvariant()}-{scale * 100:0}-{(minimum ? "minimum" : "default")}.png"));
                }
                finally { window.Close(); }
                if (density == "Compact")
                foreach (var action in Enum.GetValues<ApplicationBatchAction>())
                {
                    var review = UiFixture.CreateBulkReview(action, minimum);
                    try { Save(review, Path.Combine(outputDirectory, $"{(dark ? "dark" : "light")}-bulk-{action.ToString().ToLowerInvariant()}-{scale * 100:0}-{(minimum ? "minimum" : "default")}.png")); }
                    finally { review.Close(); }
                }
            }
        }
        finally { UiFixture.SetTextScale(1); UiFixture.Minimum = false; }
    }

    // Render only the already measured synthetic content; never create or show an HWND.
    internal static void Capture(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        foreach (var dark in new[] { false, true })
        foreach (var width in new[] { 760d, 880d })
        foreach (var compact in new[] { false, true })
        {
            if (UiFixture.Dark != dark) UiFixture.ToggleTheme();
            UiFixture.Blank = UiFixture.Minimum = false;
            UiFixture.Scale = 1;
            var window = new MainWindow { Width = width, Height = 550, Tag = compact ? "Compact" : "Comfortable" };
            try
            {
                window.RefreshSyntheticDetailsLayout();
                LayoutValidation.CheckWindowContent(window);
                Save(window, Path.Combine(outputDirectory, $"{(dark ? "dark" : "light")}-{(compact ? "compact" : "expanded")}-{width}.png"));
                if (compact && width == 880)
                {
                    var list = (ItemsControl)window.FindName("AppList");
                    var row = list.Items.Cast<CheckResult>().Single(item => item.SoftwareApp == SoftwareApp.TrackAudio);
                    row.SoftwareUpdate = new(SoftwareApp.TrackAudio, SoftwareUpdatePhase.Unavailable,
                        "Synthetic example: the executable version and Windows registration disagree. Repair using the official installer before updating through Launchpad.");
                    row.StatusMessage = row.SoftwareUpdate.Message;
                    window.SelectSyntheticDetails(row);
                    LayoutValidation.CheckWindowContent(window);
                    Save(window, Path.Combine(outputDirectory, $"{(dark ? "dark" : "light")}-compact-review-880.png"));
                }
                if (compact)
                {
                    var list = (ItemsControl)window.FindName("AppList");
                    window.SelectSyntheticDetails(list.Items.Cast<CheckResult>().Single(item => item.HasFontsCheck));
                    LayoutValidation.CheckWindowContent(window);
                    Save(window, Path.Combine(outputDirectory, $"{(dark ? "dark" : "light")}-compact-gng-{width}.png"));
                }
            }
            finally { window.Close(); }
        }

        foreach (var dark in new[] { false, true })
        foreach (var minimum in new[] { false, true })
        {
            if (UiFixture.Dark != dark) UiFixture.ToggleTheme();
            UiFixture.Blank = false;
            UiFixture.Minimum = minimum;
            UiFixture.Scale = 1;
            var window = new GngCleanupWindow();
            try
            {
                LayoutValidation.CheckWindowContent(window);
                Save(window, Path.Combine(outputDirectory,
                    $"{(dark ? "dark" : "light")}-gng-cleanup-{(minimum ? "minimum" : "default")}.png"));
            }
            finally { window.Close(); }
        }

        foreach (var dark in new[] { false, true })
        foreach (var minimum in new[] { false, true })
        foreach (var state in Enum.GetValues<GngUpdateFixtureState>())
        {
            if (UiFixture.Dark != dark) UiFixture.ToggleTheme();
            UiFixture.Blank = false;
            UiFixture.Minimum = minimum;
            UiFixture.Scale = 1;
            var window = new GngUpdateWindow();
            try
            {
                window.SetSyntheticState(state);
                LayoutValidation.CheckWindowContent(window);
                Save(window, Path.Combine(outputDirectory,
                    $"{(dark ? "dark" : "light")}-gng-update-{state.ToString().ToLowerInvariant()}-{(minimum ? "minimum" : "default")}.png"));
                if (state == GngUpdateFixtureState.Review)
                {
                    ((CheckBox)window.FindName("ShowUnchangedFiles")).IsChecked = true;
                    Save(window, Path.Combine(outputDirectory,
                        $"{(dark ? "dark" : "light")}-gng-update-reviewall-{(minimum ? "minimum" : "default")}.png"));
                    window.SetSyntheticReviewRows(GngUpdateWindow.UnchangedRows());
                    Save(window, Path.Combine(outputDirectory,
                        $"{(dark ? "dark" : "light")}-gng-update-reviewzero-{(minimum ? "minimum" : "default")}.png"));
                    window.SetSyntheticReviewRows(GngUpdateWindow.ExampleRows());
                    window.SetSyntheticReviewProgress();
                    Save(window, Path.Combine(outputDirectory,
                        $"{(dark ? "dark" : "light")}-gng-update-reviewbusy-{(minimum ? "minimum" : "default")}.png"));
                    window.SetSyntheticState(GngUpdateFixtureState.Review);
                    var files = (DataGrid)window.FindName("FilesGrid");
                    files.ScrollIntoView(files.Items.Cast<GngUpdateFile>().Single(file => file.Action == "Merge list layout"));
                    Save(window, Path.Combine(outputDirectory,
                        $"{(dark ? "dark" : "light")}-gng-update-review-listlayout-{(minimum ? "minimum" : "default")}.png"));
                    ((CheckBox)window.FindName("PreserveListLayout")).IsChecked = false;
                    Save(window, Path.Combine(outputDirectory,
                        $"{(dark ? "dark" : "light")}-gng-update-review-listlayout-off-{(minimum ? "minimum" : "default")}.png"));
                }
            }
            finally { window.Close(); }
        }

        foreach (var dark in new[] { false, true })
        foreach (var minimum in new[] { false, true })
        foreach (var state in Enum.GetValues<GngFileDiffFixtureState>())
        {
            if (UiFixture.Dark != dark) UiFixture.ToggleTheme();
            var window = GngFileDiffFixture.Create(state, minimum);
            try
            {
                Save(window, Path.Combine(outputDirectory,
                    $"{(dark ? "dark" : "light")}-gng-file-preview-{state.ToString().ToLowerInvariant()}-{(minimum ? "minimum" : "default")}.png"));
                if (GngFileDiffFixture.IsListLayout(state))
                {
                    ((TabItem)window.FindName("BeforeAfterTab")).IsSelected = true;
                    Save(window, Path.Combine(outputDirectory,
                        $"{(dark ? "dark" : "light")}-gng-file-preview-{state.ToString().ToLowerInvariant()}-beforeafter-{(minimum ? "minimum" : "default")}.png"));
                }
            }
            finally { window.Close(); }
        }

        CaptureWorkflowScreens(outputDirectory);
        UiFixture.SetTextScale(1);
        UiFixture.Blank = UiFixture.Minimum = UiFixture.RestartRequired = false;
        UiFixture.Scale = 1;
    }

    private static void CaptureWorkflowScreens(string outputDirectory)
    {
        foreach (var dark in new[] { false, true })
        foreach (var (textScale, minimum) in new[] { (1d, false), (1d, true), (1.25d, false), (1.5d, false), (1.5d, true) })
        {
            if (UiFixture.Dark != dark) UiFixture.ToggleTheme();
            UiFixture.Blank = UiFixture.RestartRequired = false;
            UiFixture.Minimum = minimum;
            UiFixture.Scale = 1;
            UiFixture.SetTextScale(textScale);
            var prefix = $"{(dark ? "dark" : "light")}-text{(int)(textScale * 100)}-{(minimum ? "minimum" : "default")}";
            void Snapshot(Window window, string name) => Save(window, Path.Combine(outputDirectory, $"{prefix}-{name}.png"));

            var main = new MainWindow { Tag = "Compact" };
            try
            {
                main.RefreshSyntheticDetailsLayout();
                Snapshot(main, "main-compact");
                ((Button)main.FindName("DetailsSizeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Snapshot(main, "main-details-expanded");
            }
            finally { main.Close(); }

            var wizard = new SetupWizardWindow();
            try
            {
                var pageNames = new[] { "welcome", "applications", "gng", "profile", "fonts", "finish" };
                for (var page = 0; page < pageNames.Length; page++)
                {
                    Snapshot(wizard, "wizard-" + pageNames[page]);
                    if (page is 1 or 5)
                    {
                        ((ScrollViewer)wizard.FindName("ContentScroll")).ScrollToEnd();
                        Snapshot(wizard, "wizard-" + pageNames[page] + "-bottom");
                    }
                    if (page < pageNames.Length - 1) wizard.SyntheticNext();
                }
            }
            finally { wizard.Close(); }

            var settings = new SettingsWindow();
            try
            {
                var choices = (StackPanel)settings.FindName("NotUsedChoices");
                foreach (var name in ApplicationPreferences.OptionalApplications)
                {
                    // Match the production-generated controls, using only synthetic selections.
                    var choice = new CheckBox
                    {
                        Content = name == "EuroScope (GNG Pack)" ? "Swedish GNG package" : name,
                        Tag = name, IsChecked = name is "VACS" or "VATIRIS", Margin = new Thickness(0, 0, 0, 5),
                    };
                    choice.SetResourceReference(Control.ForegroundProperty, "PrimaryText");
                    choices.Children.Add(choice);
                }
                Snapshot(settings, "settings");
                for (DependencyObject? parent = choices; parent is not null; parent = LogicalTreeHelper.GetParent(parent))
                    if (parent is Expander expander) { expander.IsExpanded = true; break; }
                Snapshot(settings, "settings-notused");
                choices.BringIntoView();
                Snapshot(settings, "settings-notused-bottom");
            }
            finally { settings.Close(); }

            var activity = new ActivityWindow();
            try
            {
                var grid = (DataGrid)activity.FindName("HistoryGrid");
                for (var index = 0; index < grid.Items.Count; index++)
                {
                    grid.SelectedIndex = index;
                    Snapshot(activity, "activity-" + new[] { "gng", "vendor", "cancelled" }[index]);
                    if (index == 0)
                    {
                        ((FrameworkElement)activity.FindName("GngRecoveryButton")).BringIntoView();
                        Snapshot(activity, "activity-gng-recovery");
                    }
                }
            }
            finally { activity.Close(); }

            foreach (var (name, factory) in new (string, Func<Window>)[]
            {
                ("maintenance", () => new MaintenanceWindow()),
                ("reporting-choices", () => new ReportingPreferencesWindow()),
                ("reporting-last-sent", () => ReportingHistoryFixture.Create()),
                ("reporting-last-inventory", () => ReportingHistoryFixture.Create(Models.ReportingStream.Inventory)),
                ("report-problem", () => new ReportProblemWindow()),
                ("report-received", () => new ReportProblemWindow(received: true)),
                ("euroscope", () => new EuroScopeInstallWindow()),
                ("fresh-install", () => new FreshSoftwareInstallWindow()),
            })
            {
                var review = factory();
                try
                {
                    LayoutValidation.CheckWindowContent(review);
                    UiFixture.Handle(review, review, new RoutedEventArgs(), "Review_Click");
                    Snapshot(review, name + "-review");
                }
                finally { review.Close(); }
            }

            var profile = new AppConfigWindow();
            try { Snapshot(profile, "controller-profile"); }
            finally { profile.Close(); }

            var profilePreview = UiFixture.CreateProfilePreview();
            try
            {
                Snapshot(profilePreview, "controller-profile-preview");
                ((Expander)profilePreview.FindName("ProfileDetails")).IsExpanded = true;
                ((FrameworkElement)profilePreview.FindName("PreviewDetailsText")).BringIntoView();
                Snapshot(profilePreview, "controller-profile-preview-details");
            }
            finally { profilePreview.Close(); }
        }
    }

    private static void Save(Window window, string path)
    {
        // DataGrid star columns finish sizing through a deferred dispatcher callback.
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        LayoutValidation.CheckWindowContent(window);
        var content = (FrameworkElement)window.Content;
        var width = (int)Math.Ceiling(content.ActualWidth + content.Margin.Left + content.Margin.Right);
        var height = (int)Math.Ceiling(content.ActualHeight + content.Margin.Top + content.Margin.Bottom);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
        bitmap.Render(background);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }
}
