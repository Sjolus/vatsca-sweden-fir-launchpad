using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shell;
using System.Xml.Linq;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

internal static class LayoutValidation
{
    private static readonly string[] ProgressPaths =
        [nameof(CheckResult.SoftwareProgressValue), nameof(CheckResult.SelfUpdateProgressValue)];

    public static void CheckProgressBindingSource()
    {
        // The embedded source is the same production file compiled as the linked WPF Page.
        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream("UiReview.MainWindowSource.xaml")
            ?? throw new InvalidDataException("Production MainWindow XAML source was not embedded.");
        var document = XDocument.Load(source);
        foreach (var path in ProgressPaths)
        {
            Require(typeof(CheckResult).GetProperty(path)?.CanWrite == false, path + " must exercise the real read-only model property.");
            var values = document.Descendants().Where(element => element.Name.LocalName == "ProgressBar")
                .Select(element => (string?)element.Attribute("Value")).Where(value => value?.Contains(path, StringComparison.Ordinal) == true).ToArray();
            Require(values.Length > 0, "Expected a production progress binding for " + path + ".");
            Require(values.All(value => Regex.IsMatch(value!, @"\bMode\s*=\s*OneWay\s*[,}]")),
                path + " needs explicit Mode=OneWay: ProgressBar.Value's default binding mode can write to its source.");
        }
    }

    public static void CheckWindowContent(Window window)
    {
        if (window.Content is not FrameworkElement content)
            throw new InvalidDataException(window.GetType().Name + " has no FrameworkElement content.");

        // A never-shown Window is Collapsed. Measuring that Window silently skips its children.
        // Measure its actual visible content directly, then explicitly realize every item template.
        // Caption-free WindowChrome still has resize borders but no standard title bar.
        // This is a conservative content budget, not a native/DPI measurement.
        var chrome = WindowChrome.GetWindowChrome(window);
        var size = new Size(window.Width - (window.WindowStyle == WindowStyle.None ? 0 : 16),
            window.Height - (window.WindowStyle == WindowStyle.None ? 0 : chrome?.CaptionHeight == 0 ? 16 : 40));
        content.ApplyTemplate();
        content.Measure(size);
        content.Arrange(new Rect(new Point(), size));
        content.UpdateLayout();
        RealizeTemplates(content);
        content.Measure(size);
        content.Arrange(new Rect(new Point(), size));
        content.UpdateLayout();

        // Closed ComboBox/other Selector popups intentionally have no visible item containers.
        // The production row lists are plain ItemsControl instances, with no virtualization.
        foreach (var items in Descendants(content).OfType<ItemsControl>().Where(items => items.GetType() == typeof(ItemsControl) && items.Items.Count > 0))
        {
            for (var index = 0; index < items.Items.Count; index++)
            {
                var container = items.ItemContainerGenerator.ContainerFromIndex(index);
                Require(container is not null, window.GetType().Name + ": item " + index + " was not realized.");
                RealizeTemplates(container!);
            }
        }
        if (window is MainWindow) CheckRealizedProgressBindings(content);
    }

    private static void RealizeTemplates(DependencyObject node)
    {
        if (node is FrameworkElement element) element.ApplyTemplate();
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            RealizeTemplates(VisualTreeHelper.GetChild(node, index));
    }

    internal static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        yield return node;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            foreach (var descendant in Descendants(VisualTreeHelper.GetChild(node, index))) yield return descendant;
    }

    private static void CheckRealizedProgressBindings(FrameworkElement content)
    {
        var bars = Descendants(content).OfType<ProgressBar>().ToArray();
        foreach (var path in ProgressPaths)
        {
            var matching = bars.Where(bar => BindingOperations.GetBinding(bar, ProgressBar.ValueProperty)?.Path?.Path == path).ToArray();
            Require(matching.Length > 0, "No realized progress bars found for " + path + ".");
            foreach (var bar in matching)
            {
                var expression = BindingOperations.GetBindingExpression(bar, ProgressBar.ValueProperty)
                    ?? throw new InvalidDataException("No live binding expression for " + path + ".");
                Require(expression.ParentBinding.Mode == BindingMode.OneWay, path + " has a writable runtime binding.");
                Require(bar.DataContext is CheckResult, path + " does not use the real presentation model.");
                expression.UpdateTarget();
                Require(!expression.HasError, path + " failed to bind.");
            }

            // The shared panel may currently display any application. State and notification
            // regressions explicitly select the relevant application in MainLayoutMatrix.
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
