namespace VatscaUpdateChecker.Models;

/// <summary>Separates routine package rules from notes about this particular installation.</summary>
public static class GngReviewNotes
{
    private static readonly string[] RoutinePrefixes =
    [
        "Package defaults, radar screens and shared plugin/settings files will be refreshed.",
        "Existing LastSession sign-in fields, personal Local/Hoppie files, LoginProfiles and custom plugins are kept.",
        "When the package ships RDF, profiles use its RDF DLL and remove old AFV/RDF entries",
        "Every replaced file is backed up before installation.",
        "List positions and whether each list is shown are kept where their settings can be matched safely.",
        "List layout preservation is off.",
        "The matching Full Package will be cached intact as a reference; only the Update-Only ZIP is installed."
    ];

    // Unknown/new warning text is important by default. Nothing is removed from the full review.
    public static IReadOnlyList<string> ForAttention(IEnumerable<string> warnings) =>
        warnings.Where(warning => !RoutinePrefixes.Any(prefix => warning.StartsWith(prefix, StringComparison.Ordinal))).ToArray();
}
