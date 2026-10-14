using System.Windows;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

internal static class PresentationResources
{
    internal static void ApplyTextScale(ResourceDictionary resources, double requestedScale)
    {
        double scale = ApplicationPreferences.EffectiveTextScale(requestedScale);
        foreach (int size in Enumerable.Range(11, 14))
            resources["TextSize" + size] = size * scale;
        resources["CompactRowHeight"] = 36 + (scale - 1) * 32;
        resources["ExpandedRowHeight"] = Math.Round(56 + (scale - 1) * 42);
        resources["LaunchColumnWidth"] = new GridLength(118 + (scale - 1) * 48);
        resources["OptionsColumnWidth"] = new GridLength(84 + (scale - 1) * 24);
        // Match the six column minima so the rightmost actions remain scrollable.
        resources["ApplicationTableMinWidth"] = 724 + (scale - 1) * 72;
        resources[SystemFonts.MessageFontSizeKey] = 12 * scale;
    }
}
