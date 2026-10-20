namespace VatscaUpdateChecker.Models;

/// <summary>Optional tools are a presentation choice; their paths and data are retained.</summary>
public static class ApplicationPreferences
{
    public static readonly string[] OptionalApplications =
        ["EuroScope", "EuroScope (GNG Pack)", "TrackAudio", "VACS", "vATIS", "VatEFS", "SAAMAN", "VATIRIS"];

    public static bool IsNotUsed(AppSettings settings, string appName) =>
        OptionalApplications.Contains(appName, StringComparer.Ordinal) &&
        (settings.NotUsedApplications ?? "").Split('|').Contains(appName, StringComparer.Ordinal);

    public static void SetNotUsed(AppSettings settings, string appName, bool notUsed)
    {
        if (!OptionalApplications.Contains(appName, StringComparer.Ordinal)) return;
        var selected = OptionalApplications.Where(name =>
            name == appName ? notUsed : IsNotUsed(settings, name));
        settings.NotUsedApplications = string.Join('|', selected);
    }

    public static double EffectiveTextScale(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 1, 1.5) : 1;
}
