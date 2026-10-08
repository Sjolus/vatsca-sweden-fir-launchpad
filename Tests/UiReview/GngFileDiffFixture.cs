using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

internal enum GngFileDiffFixtureState { Changes, BeforeAfter, ListLayout, PluginListLayout, Binary, Personal, Large, WithoutDiff }

internal static class GngFileDiffFixture
{
    internal const string Before = "Settings\tSector\tESAA-Sweden_previous.sct\n" +
        "Settings\tName\tGöteborg kontroll\nLastSession\tPassword\t[masked]\n" +
        "Plugins\tPlugin0\tESAA\\Plugins\\AFVBridge.dll\n" +
        "Plugins\tPlugin1\tC:\\Synthetic\\Custom plugins with a long descriptive directory name\\Controller-specific display integration\\A deliberately long plugin filename.dll\n" +
        "Settings\tAselKey\tA\nSettings\tFreqKey\tF\n";
    internal const string After = "Settings\tSector\tESAA-Sweden_current.sct\n" +
        "Settings\tName\tGöteborg kontroll\nLastSession\tPassword\t[masked]\n" +
        "Plugins\tPlugin0\tESAA\\Plugins\\RDF.dll\n" +
        "Plugins\tPlugin1\tC:\\Synthetic\\Custom plugins with a long descriptive directory name\\Controller-specific display integration\\A deliberately long plugin filename.dll\n" +
        "Settings\tAselKey\tA\nSettings\tFreqKey\tF\n";

    internal const string ListBefore = "SIL\r\nm_Visible:0\r\nm_X:120\r\nm_Y:240\r\nm_LineNumber:10\r\n" +
        "m_Column: C/S:10:0:10021:6:0:1:TopSky plugin:TopSky plugin::0:0.0\r\nEND\r\n";
    internal const string ListAfter = "SIL\r\nm_Visible:0\r\nm_X:120\r\nm_Y:240\r\nm_LineNumber:10\r\n" +
        "m_Column: C/S:14:0:10022:6:0:1:TopSky plugin:TopSky plugin::0:0.0\r\nEND\r\n";
    internal const string PluginListBefore = "PLUGINS\r\nTopSky plugin:ACList/ETWR List/m_Visible:0\r\n" +
        "TopSky plugin:ACList/ETWR List/m_X:120\r\nTopSky plugin:ACList/ETWR List/m_Y:240\r\n" +
        "TopSky plugin:ACList/ETWR List/m_Column_0:Sort:15:0:10906:0:0:0:TopSky plugin::\r\nEND\r\n";
    internal const string PluginListAfter = "PLUGINS\r\nTopSky plugin:ACList/ETWR List/m_Visible:0\r\n" +
        "TopSky plugin:ACList/ETWR List/m_X:120\r\nTopSky plugin:ACList/ETWR List/m_Y:240\r\n" +
        "TopSky plugin:ACList/ETWR List/m_Column_0:Sort:18:0:10907:0:0:0:TopSky plugin::\r\nEND\r\n";

    internal static bool IsListLayout(GngFileDiffFixtureState state) =>
        state is GngFileDiffFixtureState.ListLayout or GngFileDiffFixtureState.PluginListLayout;

    internal static string CurrentText(GngFileDiffFixtureState state) => state switch
    {
        GngFileDiffFixtureState.ListLayout => ListBefore,
        GngFileDiffFixtureState.PluginListLayout => PluginListBefore,
        _ => Before
    };

    internal static string PlannedText(GngFileDiffFixtureState state) => state switch
    {
        GngFileDiffFixtureState.ListLayout => ListAfter,
        GngFileDiffFixtureState.PluginListLayout => PluginListAfter,
        _ => After
    };

    private static GngFilePreview ListPreview(GngFileDiffFixtureState state)
    {
        var before = CurrentText(state);
        var after = PlannedText(state);
        var beforeBytes = Encoding.ASCII.GetBytes(before);
        var afterBytes = Encoding.ASCII.GetBytes(after);
        return new GngFilePreview("ESAA/Settings/" + (state == GngFileDiffFixtureState.ListLayout ? "Lists.txt" : "Plugin.txt"),
            "Merge list layout",
            "Planned source: package defaults merged with retained list positions and whole-list visibility.\n" +
            $"Before: {beforeBytes.Length} bytes; SHA-256 {Convert.ToHexString(SHA256.HashData(beforeBytes))}.\n" +
            $"After: {afterBytes.Length} bytes; SHA-256 {Convert.ToHexString(SHA256.HashData(afterBytes))}.\n" +
            "The file bytes will change; the preview shows the exact proposed merged text.\n" +
            "Before encoding: Windows-1252. After encoding: Windows-1252.\n" +
            $"Before newlines: {before.Count(character => character == '\n')} CRLF, 0 LF, 0 CR. After newlines: {after.Count(character => character == '\n')} CRLF, 0 LF, 0 CR.\n" +
            "Sensitive fields are masked in both text views; these synthetic list records contain none.\n" +
            "Synthetic data only. No external settings file was read.", before, after);
    }

    internal static GngFileDiffWindow Create(GngFileDiffFixtureState state, bool minimum)
    {
        var preview = state switch
        {
            GngFileDiffFixtureState.ListLayout or GngFileDiffFixtureState.PluginListLayout => ListPreview(state),
            GngFileDiffFixtureState.Binary => new GngFilePreview("ESAA/Plugins/TopSky.dll", "Promote plugin",
                "Binary file: text comparison is unavailable. The package DLL will replace the current file.\nCurrent: 1,250,000 bytes · SHA-256 " + new string('a', 64) +
                "\nAfter installation: 1,300,000 bytes · SHA-256 " + new string('b', 64), null, null),
            GngFileDiffFixtureState.Personal => new GngFilePreview("ESAA/Plugins/TopSkyCPDLChoppieCode.txt", "Keep personal file",
                "This personal file will be kept without changes. Its contents are not displayed because it may contain sensitive values.\nCurrent and after installation: 48 bytes. Synthetic metadata only.", null, null),
            GngFileDiffFixtureState.Large => new GngFilePreview("ESAA/Settings/A deliberately long synthetic package directory/Another descriptive directory/A large shared settings file.txt", "Replace package default",
                "Text comparison is unavailable because this file exceeds the preview size limit.\nCurrent: 8,000,000 bytes. After installation: 8,100,000 bytes.\n" +
                string.Join("\n", Enumerable.Repeat("Synthetic diagnostic: the current file is backed up before replacement. Review the proposed package default before continuing.", 12)), null, null),
            _ => new GngFilePreview("ESAA APP ACC.prf", "Merge profile",
                "Source: synthetic reviewed package with retained personal fields merged into the proposed profile.\n" +
                "Current: 512 bytes · SHA-256 " + new string('a', 64) + "\n" +
                "After installation: 560 bytes · SHA-256 " + new string('b', 64) + "\n" +
                "The file bytes will change; this preview shows the exact planned merged text.\n" +
                "Current encoding: Windows-1252. After installation: Windows-1252.\n" +
                "Current newlines: 7 CRLF, 0 LF, 0 CR. After installation: 7 CRLF, 0 LF, 0 CR.\n" +
                "Sensitive fields are masked in both text views. The underlying personal values are preserved.\n" +
                "Synthetic data only. No external profile or settings file was read.", Before, After)
        };
        var diff = preview.HasText && state != GngFileDiffFixtureState.WithoutDiff
            ? GngTextDiff.Create(preview.BeforeText!, preview.AfterText!) : null;
        var window = new GngFileDiffWindow(preview, diff);
        if (minimum) { window.Width = window.MinWidth; window.Height = window.MinHeight; }
        if (state == GngFileDiffFixtureState.BeforeAfter)
            ((TabItem)window.FindName("BeforeAfterTab")).IsSelected = true;
        return window;
    }
}

internal static partial class LayoutMatrix
{
    private static int CheckGngFileDiffLayouts()
    {
        var count = 0;
        foreach (var minimum in new[] { false, true })
        foreach (var state in Enum.GetValues<GngFileDiffFixtureState>())
        {
            var window = GngFileDiffFixture.Create(state, minimum);
            try
            {
                LayoutValidation.CheckWindowContent(window); DrainBindings(); LayoutValidation.CheckWindowContent(window);
                CheckFooter(window);
                foreach (var name in new[] { "FilePathText", "SummaryText" })
                {
                    var text = Required<TextBox>(window, name);
                    Require(text.IsReadOnly && text.IsReadOnlyCaretVisible && text.Focusable && text.TextWrapping == TextWrapping.Wrap,
                        "The file preview must keep its path and summary selectable and wrapped: " + name);
                    Require(!string.IsNullOrWhiteSpace(text.Text), "Missing file-preview metadata: " + name);
                }
                Require(IsPresented(Required<TextBlock>(window, "MaskingNotice")), "The file preview must explain masked values.");
                var tabs = Required<TabControl>(window, "PreviewTabs");
                var hasText = GngFileDiffFixture.IsListLayout(state) || state is GngFileDiffFixtureState.Changes or GngFileDiffFixtureState.BeforeAfter or GngFileDiffFixtureState.WithoutDiff;
                Require(IsPresented(tabs) == hasText, "Summary-only previews must not expose blank text views.");
                if (hasText)
                {
                    var summary = Required<TextBox>(window, "SummaryText");
                    var summaryScroll = LayoutValidation.Descendants(summary).OfType<ScrollViewer>().Single();
                    Require(summary.ActualHeight <= 92.5 && summaryScroll.ScrollableHeight > 0,
                        "Realistic preview metadata must scroll without consuming the text comparison area.");
                    Require(Required<TextBox>(window, "CurrentText").Text == GngFileDiffFixture.CurrentText(state) &&
                        Required<TextBox>(window, "AfterText").Text == GngFileDiffFixture.PlannedText(state),
                        "The preview must display the exact sanitized current and proposed text.");
                    if (state != GngFileDiffFixtureState.WithoutDiff)
                    {
                        Required<TabItem>(window, "ChangesTab").IsSelected = true;
                        LayoutValidation.CheckWindowContent(window);
                        var diff = Required<TextBox>(window, "DiffText");
                        CheckGngPreviewText(diff, window);
                        Require(diff.Text.Contains("@@", StringComparison.Ordinal) && diff.Text.Contains(GngFileDiffFixture.IsListLayout(state) ? "m_Column" : "RDF.dll", StringComparison.Ordinal),
                            "The change view must show the production unified diff and line markers.");
                        if (GngFileDiffFixture.IsListLayout(state))
                        {
                            var changes = diff.Text.Split('\n').Where(line => line.StartsWith('+') || line.StartsWith('-')).ToArray();
                            Require(changes.Length == 2 && changes.All(line => line.Contains("m_Column", StringComparison.Ordinal)) &&
                                !changes.Any(line => line.Contains("m_X:", StringComparison.Ordinal) || line.Contains("m_Y:", StringComparison.Ordinal) || line.Contains("m_Visible:", StringComparison.Ordinal)),
                                "Merged list previews must show refreshed columns without a position or whole-list visibility reversion.");
                            foreach (var value in new[] { "m_X:120", "m_Y:240", "m_Visible:0" })
                                Require(GngFileDiffFixture.CurrentText(state).Contains(value, StringComparison.Ordinal) &&
                                    GngFileDiffFixture.PlannedText(state).Contains(value, StringComparison.Ordinal),
                                    "The paired list views must retain the synthetic local layout field: " + value);
                        }
                    }
                    Required<TabItem>(window, "BeforeAfterTab").IsSelected = true;
                    LayoutValidation.CheckWindowContent(window);
                    foreach (var name in new[] { "CurrentText", "AfterText" }) CheckGngPreviewText(Required<TextBox>(window, name), window);
                    Require(!LayoutValidation.Descendants((FrameworkElement)window.Content).Contains(Required<TextBox>(window, "DiffText")),
                        "Switching views must remove the diff pane from the visual tree.");
                }
                else
                {
                    Require(window.Height <= 360 && window.MinHeight == 300, "Summary-only previews should use a compact window.");
                    Require(Required<TextBox>(window, "CurrentText").Text.Length == 0 && Required<TextBox>(window, "AfterText").Text.Length == 0 &&
                        Required<TextBox>(window, "DiffText").Text.Length == 0, "Summary-only previews must not populate hidden file contents.");
                    if (state == GngFileDiffFixtureState.Large)
                    {
                        var scroll = Required<ScrollViewer>(window, "HeaderScroll");
                        scroll.ScrollToEnd(); DrainBindings();
                        Require(scroll.ScrollableHeight > 0 && scroll.VerticalOffset >= scroll.ScrollableHeight - 1,
                            "Long summary-only diagnostics must remain scrollable to their end.");
                    }
                }
                Require(!window.IsVisible, "A file-diff fixture showed a native window.");
                CheckFooter(window);
                count++;
            }
            catch (Exception error) { throw new System.IO.InvalidDataException($"GNG file preview {state}, {(UiFixture.Dark ? "dark" : "light")}, {(minimum ? "minimum" : "default")}: {error.Message}", error); }
            finally { window.Close(); }
        }
        return count;
    }

    private static void CheckGngPreviewText(TextBox text, Window window)
    {
        Require(IsPresented(text) && text.IsReadOnly && text.IsReadOnlyCaretVisible && text.Focusable && text.FontFamily.Source == "Consolas" &&
            text.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto && text.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
            "File preview text must be read-only, selectable, monospace and scrollable: " + text.Name);
        Require(text.ActualWidth >= 250 && text.ActualHeight >= 100, "The file preview text has too little space: " + text.Name);
        var content = (FrameworkElement)window.Content;
        var bounds = Bounds(text, content);
        Require(bounds.Left >= 0 && bounds.Right <= content.ActualWidth + .5 && bounds.Bottom <= content.ActualHeight + .5,
            "The file preview text extends beyond the window: " + text.Name);
        text.SelectAll();
        Require(text.SelectedText == text.Text, "The preview text cannot be selected for inspection.");
        text.Select(0, 0);
    }
}
