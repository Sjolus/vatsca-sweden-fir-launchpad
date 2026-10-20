using System.IO;
using System.Windows;
using System.Windows.Controls;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

// Production XAML with in-memory examples only. No installer, network or profile service is linked.
public partial class SaamanWindow : Window
{
    public SaamanWindow()
    {
        InitializeComponent();
        DllPathText.Text = @"C:\Synthetic\EuroScope\ESAA\Plugins\SAAMAN\SAAMAN.dll";
        ProfilePathText.Text = @"C:\Synthetic\EuroScope\ESAA APP ACC.prf";
        RuntimeButton.Content = "WebView2 is available";
        RuntimeButton.IsEnabled = false;
    }
    internal void SetScenario(bool existing, bool busy, bool blocked)
    {
        var release = new SaamanRelease("1.3.37", "v1.3.37", [
            new("SAAMAN.dll", new Uri("https://github.com/Kaskobiii/SAAMAN/releases/download/v1.3.37/SAAMAN.dll"), 1604608, new string('a', 64))
        ]);
        var plan = new SaamanInstallPlan(DllPathText.Text, release,
            existing ? new(DllPathText.Text, "1.3.36", 1500000, new string('b', 64)) : null, []);
        ReviewText.Text = SaamanReviewPresentation.Format(plan, runtimeAvailable: true);
        StatusText.Text = blocked ? "Close every EuroScope instance before installing or updating SAAMAN."
            : busy ? "Downloading SAAMAN.dll — 64%" : "Release checked. Review the changes before installing.";
        Activity.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Activity.Value = 64;
        ConfirmChange.IsEnabled = !busy && !blocked;
        ConfirmChange.IsChecked = false;
        ApplyButton.IsEnabled = false;
        ApplyButton.Content = existing ? "Install reviewed update" : "Install reviewed release";
        OptionsPanel.IsEnabled = !busy;
        ReviewButton.IsEnabled = !busy;
        CancelOperationButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Review_Click(object sender, RoutedEventArgs e) => SetScenario(true, false, false);
    private void Apply_Click(object sender, RoutedEventArgs e) { }
    private void CancelOperation_Click(object sender, RoutedEventArgs e) => SetScenario(true, false, false);
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Adopt_Click(object sender, RoutedEventArgs e) { }
    private void ReleaseNotes_Click(object sender, RoutedEventArgs e) { }
    private void Runtime_Click(object sender, RoutedEventArgs e) { }
    private void ChooseProfile_Click(object sender, RoutedEventArgs e) { }
    private void ConfirmationChanged(object sender, RoutedEventArgs e) { }
    private void Recovery_Click(object sender, RoutedEventArgs e) { }
    private void Restore_Click(object sender, RoutedEventArgs e) { }
}

internal static class SaamanLayoutReview
{
    public static string Run(string? output)
    {
        if (output != null) Directory.CreateDirectory(output);
        int count = 0;
        foreach (bool dark in new[] { true, false })
        {
            if (UiFixture.Dark != dark) UiFixture.ToggleTheme();
            foreach (double textScale in new[] { 1d, 1.25d, 1.5d })
            foreach (bool minimum in new[] { false, true })
            foreach (var scenario in new[] { "install", "update", "downloading", "blocked" })
            {
                UiFixture.SetTextScale(textScale);
                var window = new SaamanWindow();
                window.SetScenario(scenario != "install", scenario == "downloading", scenario == "blocked");
                if (minimum) { window.Width = window.MinWidth; window.Height = window.MinHeight; }
                try
                {
                    LayoutValidation.CheckWindowContent(window);
                    var content = (FrameworkElement)window.Content;
                    foreach (var name in new[] { "ReviewButton", "ApplyButton", "CloseButton", "ConfirmChange" })
                    {
                        var control = (FrameworkElement)window.FindName(name);
                        var bounds = control.TransformToAncestor(content).TransformBounds(new Rect(control.RenderSize));
                        if (bounds.Width < 20 || bounds.Height < (name == "ConfirmChange" ? 14 : 20) || bounds.Left < -1 || bounds.Top < -1 ||
                            bounds.Right > content.ActualWidth + 1 || bounds.Bottom > content.ActualHeight + 1)
                            throw new InvalidDataException($"SAAMAN {scenario}: {name} clipped at {textScale:P0}, minimum={minimum}.");
                    }
                    if (((Button)window.FindName("ApplyButton")).IsEnabled)
                        throw new InvalidDataException("The unconfirmed SAAMAN review must not enable installation.");
                    if (scenario == "blocked" && ((CheckBox)window.FindName("ConfirmChange")).IsEnabled)
                        throw new InvalidDataException("Blocked SAAMAN review must require a new confirmation after EuroScope closes.");
                    if (output != null && textScale == 1 && !minimum)
                        LayoutSnapshots.SaveSaamanWindow(window, Path.Combine(output, $"{(dark ? "dark" : "light")}-saaman-{scenario}.png"));
                    count++;
                }
                finally { window.Close(); }
            }
            UiFixture.SetTextScale(1);
            UiFixture.Minimum = false;
            UiFixture.Blank = false;
            UiFixture.Scale = 1;
            var main = new MainWindow { Width = 1000, Height = 620, Tag = "Compact" };
            try
            {
                var rows = ((ItemsControl)main.FindName("AppList")).Items.Cast<CheckResult>().ToArray();
                var row = rows.Single(r => r.HasSaamanManagement);
                var launchpad = rows.Single(r => r.HasSelfUpdate);
                launchpad.InstalledVersion = "2.5.0 preview";
                launchpad.LatestVersion = "2.4.0";
                launchpad.ShowSelfUpdateAction = false;
                launchpad.SelfUpdateSummary = "Development preview";
                ((FrameworkElement)main.FindName("ApplicationDetails")).Height = 140;
                main.SelectSyntheticDetails(row);
                LayoutValidation.CheckWindowContent(main);
                if (row.ShowLaunch || row.ShowRunningActions) throw new InvalidDataException("SAAMAN must not expose a standalone process action.");
                foreach (string name in new[] { "DetailSaamanButton", "DetailSaamanProfileButton", "DetailSaamanGuideButton" })
                    if (((Button)main.FindName(name)).Visibility != Visibility.Visible)
                        throw new InvalidDataException("SAAMAN action missing: " + name);
                if (output != null) LayoutSnapshots.SaveSaamanWindow(main, Path.Combine(output, $"{(dark ? "dark" : "light")}-saaman-main.png"));
                count++;
            }
            finally { main.Close(); }
            var profile = new AppConfigWindow();
            try
            {
                ((TextBox)profile.FindName("TargetPathText")).Text = @"C:\Synthetic\EuroScope";
                ((TextBlock)profile.FindName("SyncText")).Text = "Saved profile differs from EuroScope. Preview before applying.";
                var choice = (CheckBox)profile.FindName("PatchSaamanBox");
                if (choice.IsChecked == true) throw new InvalidDataException("SAAMAN must start unchecked.");
                choice.IsChecked = true; // Show a user's selection, before applying.
                LayoutValidation.CheckWindowContent(profile);
                ((ScrollViewer)profile.FindName("ProfileFormScroll")).ScrollToBottom();
                if (output != null) LayoutSnapshots.SaveSaamanWindow(profile, Path.Combine(output, $"{(dark ? "dark" : "light")}-saaman-profile.png"));
                count++;
            }
            finally { profile.Close(); }
            var wizard = new SetupWizardWindow();
            try
            {
                wizard.SyntheticNext(); wizard.SyntheticNext(); wizard.SyntheticNext();
                LayoutValidation.CheckWindowContent(wizard);
                if (output != null) LayoutSnapshots.SaveSaamanWindow(wizard, Path.Combine(output, $"{(dark ? "dark" : "light")}-saaman-setup.png"));
                count++;
            }
            finally { wizard.Close(); }
        }
        return $"{count} SAAMAN layouts passed; production XAML with synthetic data, no windows shown.";
    }
}

internal static partial class LayoutSnapshots
{
    internal static void SaveSaamanWindow(Window window, string path) => Save(window, path);
}
