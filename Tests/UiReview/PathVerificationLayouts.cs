using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace VatscaUpdateChecker;

internal static partial class LayoutMatrix
{
    private static int CheckPathVerificationLayouts()
    {
        UiFixture.Blank = false;
        UiFixture.Scale = 1;
        foreach (bool minimum in new[] { false, true })
        {
            UiFixture.Minimum = minimum;
            var settings = new SettingsWindow();
            try
            {
                var content = (FrameworkElement)settings.Content;
                var expander = Required<Expander>(settings, "ApplicationPaths");
                expander.IsExpanded = true;
                UiFixture.ShowSyntheticVerificationWarnings(settings);
                LayoutValidation.CheckWindowContent(settings);
                CheckFooter(settings);
                var scroll = LayoutValidation.Descendants(content).OfType<ScrollViewer>().First(item => IsWithin(expander, item));
                Require(scroll.ViewportHeight > 40, "Path feedback leaves no usable settings viewport.");
                Require(scroll.ExtentWidth <= scroll.ViewportWidth + 1, "Long verification paths widened the Settings form.");
                foreach (var (_, name) in UiFixture.VerificationFields)
                {
                    var status = Required<TextBlock>(settings, name);
                    Require(status.Text.Length > 20 && status.TextWrapping != TextWrapping.NoWrap,
                        name + " needs a wrapping explanation, not color alone.");
                    RevealVerification(status, scroll, content);
                    Require(status.Foreground is SolidColorBrush foreground && settings.Background is SolidColorBrush background &&
                        Contrast(foreground.Color, background.Color) >= 4.5, name + " is not readable against the form background.");
                }
                var summary = Required<TextBlock>(settings, "ValidationSummary");
                var keep = Required<CheckBox>(settings, "KeepUnverified");
                Require(summary.Text.Length > 20 && summary.TextWrapping != TextWrapping.NoWrap,
                    "The footer must explain why a path needs attention.");
                Require(IsPresented(keep) && keep.IsChecked != true, "Keeping an unrecognized location must remain an explicit choice.");
                RequireFullyInside(summary, content, "Path validation summary");
                RequireFullyInside(keep, content, "Keep-unrecognized choice");
                CheckFooter(settings);

                // Exercise the linked XAML's TextChanged hook using synthetic presentation only.
                // This establishes that the fixture notices edits, not production path verification.
                keep.IsChecked = true;
                Required<TextBox>(settings, "VacsPath").Text += ".changed";
                DrainBindings();
                Require(Required<TextBlock>(settings, "VacsStatus").Text.Contains("pending", StringComparison.OrdinalIgnoreCase),
                    "Editing a synthetic path left its old verification presentation in place.");
                Require(keep.IsChecked != true, "The fixture must clear its prior custom-path acknowledgement after editing.");
                Required<TextBox>(settings, "VacsPath").Text = "";
                DrainBindings();
                Require(!Required<TextBlock>(settings, "VacsStatus").Text.Contains("pending", StringComparison.OrdinalIgnoreCase),
                    "A blank synthetic path retained the pending-verification state.");
            }
            finally { settings.Close(); }
        }

        UiFixture.Minimum = true;
        var discovery = new InstallationDiscoveryWindow();
        try
        {
            LayoutValidation.CheckWindowContent(discovery);
            var content = (FrameworkElement)discovery.Content;
            var list = Required<ItemsControl>(discovery, "CandidatesList");
            var scroll = Required<ScrollViewer>(discovery, "DiscoveryResults");
            Require(scroll.ViewportHeight > 40 && scroll.ExtentWidth <= scroll.ViewportWidth + 1,
                "Discovery feedback must stay within a usable, horizontally bounded viewport.");
            var rows = list.Items.Cast<UiFixture.DiscoveryRow>().ToArray();
            Require(rows.Any(row => row.CanSelect) && rows.Any(row => !row.CanSelect), "Verification fixtures lack selectable and invalid examples.");
            foreach (var row in rows)
            {
                var checkbox = LayoutValidation.Descendants(list).OfType<CheckBox>().Single(element => ReferenceEquals(element.DataContext, row));
                Require(checkbox.IsEnabled == row.CanSelect && checkbox.IsChecked != true,
                    "Discovery selection must follow verification and start unchecked.");
                var reason = LayoutValidation.Descendants(list).OfType<TextBlock>().Single(element =>
                    ReferenceEquals(element.DataContext, row) && BindingOperations.GetBinding(element, TextBlock.TextProperty)?.Path?.Path == "VerificationText");
                Require(reason.Text == row.VerificationText && reason.Text.Length > 20 && reason.TextWrapping != TextWrapping.NoWrap,
                    "A discovery verification explanation is missing or cannot wrap.");
                RevealVerification(reason, scroll, content);
                Require(reason.IsEnabled, "Disabling an invalid choice must not also disable its explanation.");
                Require(reason.Foreground is SolidColorBrush foreground && Application.Current.FindResource("RowBg") is SolidColorBrush background &&
                    Contrast(foreground.Color, background.Color) >= 4.5, "Discovery verification feedback has insufficient contrast.");
            }
            Require(rows.Single(row => row.Candidate.AppName == "VACS").CanSelect,
                "A valid custom copy must remain selectable even without automatic management.");
            RequireFullyInside(Required<Button>(discovery, "UseButton"), content, "Use selected locations");
        }
        finally { discovery.Close(); }
        return 3;
    }

    private static void RevealVerification(FrameworkElement element, ScrollViewer scroll, FrameworkElement content)
    {
        element.BringIntoView();
        DrainBindings();
        content.UpdateLayout();
        var rectangle = Bounds(element, scroll);
        Require(rectangle.Width > 20 && rectangle.Height > 10 && rectangle.Left >= -.5 &&
            rectangle.Right <= scroll.ActualWidth + .5 && rectangle.Top >= -.5 && rectangle.Bottom <= scroll.ActualHeight + .5,
            "Verification explanation cannot be fully reached by scrolling: " + element.Name + ".");
    }

    private static void RequireFullyInside(FrameworkElement element, FrameworkElement content, string label)
    {
        var rectangle = Bounds(element, content);
        Require(rectangle.Width > 20 && rectangle.Height > 10 && rectangle.Left >= -.5 && rectangle.Top >= -.5 &&
            rectangle.Right <= content.ActualWidth + .5 && rectangle.Bottom <= content.ActualHeight + .5,
            label + " extends beyond its available window area.");
    }
}
