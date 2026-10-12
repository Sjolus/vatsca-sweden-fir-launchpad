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
        resources["LaunchColumnWidth"] = new GridLength(82 + (scale - 1) * 36);
        resources["OptionsColumnWidth"] = new GridLength(84 + (scale - 1) * 24);
        resources[SystemFonts.MessageFontSizeKey] = 12 * scale;
    }
}
