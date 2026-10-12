using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace VatscaUpdateChecker;

// Receives prepared text only; revealing credentials remains an explicit user action.
internal sealed class ProfilePreviewWindow : Window
{
    internal ProfilePreviewWindow(string summaryText, string maskedText, Func<string> revealText)
    {
        ArgumentNullException.ThrowIfNull(revealText);
        Title = "Controller profile — preview file changes";
        Width = Math.Min(720, SystemParameters.WorkArea.Width);
        Height = Math.Min(620, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(440, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(400, SystemParameters.WorkArea.Height);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBg");
        SetResourceReference(ForegroundProperty, "InputFg");
        SetResourceReference(FontSizeProperty, "TextSize12");
        NameScope.SetNameScope(this, new NameScope());

        var summary = new TextBox
        {
            Text = summaryText,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        summary.SetResourceReference(BackgroundProperty, "AppBg");
        summary.SetResourceReference(ForegroundProperty, "InputFg");
        RegisterName("PreviewSummaryText", summary);
        AutomationProperties.SetName(summary, "Controller profile changes, preservation and recovery");

        var preview = new TextBox
        {
            Text = maskedText,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            FontFamily = new FontFamily("Consolas"),
            Margin = new Thickness(0, 8, 0, 0),
            BorderThickness = new Thickness(0),
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 140,
            MaxHeight = 320,
        };
        preview.SetResourceReference(FontSizeProperty, "TextSize12");
        preview.SetResourceReference(BackgroundProperty, "InputBg");
        preview.SetResourceReference(ForegroundProperty, "InputFg");
        RegisterName("PreviewDetailsText", preview);
        AutomationProperties.SetName(preview, "Expected profile file changes");

        var details = new Expander
        {
            Header = "File-by-file changes (credentials hidden)",
            Content = preview,
            Margin = new Thickness(0, 12, 0, 0),
        };
        details.SetResourceReference(ForegroundProperty, "InputFg");
        RegisterName("ProfileDetails", details);

        var toggle = new Button
        {
            Content = "Show credentials",
            Margin = new Thickness(0, 0, 8, 4),
            Padding = new Thickness(12, 6, 12, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        RegisterName("ToggleCredentialsButton", toggle);
        bool showing = false;
        string? clearText = null;
        toggle.Click += (_, _) =>
        {
            if (!showing) clearText ??= revealText();
            showing = !showing;
            preview.Text = showing ? clearText : maskedText;
            toggle.Content = showing ? "Hide credentials" : "Show credentials";
            details.Header = showing ? "File-by-file changes (credentials visible)" : "File-by-file changes (credentials hidden)";
            details.IsExpanded = true;
            details.BringIntoView();
        };

        var back = new Button
        {
            Content = "Back to profile",
            IsCancel = true,
            Margin = new Thickness(0, 0, 0, 4),
            Padding = new Thickness(12, 6, 12, 6),
        };
        back.Click += (_, _) => Close();
        RegisterName("BackButton", back);

        var actions = new WrapPanel
        {
            Margin = new Thickness(16, 0, 16, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        actions.Children.Add(toggle);
        actions.Children.Add(back);

        var content = new StackPanel { Margin = new Thickness(16) };
        content.Children.Add(summary);
        content.Children.Add(details);
        var scroll = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        RegisterName("PreviewContentScroll", scroll);

        var dock = new DockPanel();
        DockPanel.SetDock(actions, Dock.Bottom);
        dock.Children.Add(actions);
        dock.Children.Add(scroll);
        Content = dock;
    }
}
