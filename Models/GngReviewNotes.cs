using System.IO;

namespace VatscaUpdateChecker.Models;

public sealed record GngReviewAssessment(bool NeedsAttention, string Heading, IReadOnlyList<string> Points);

/// <summary>Separates routine package rules from notes about this particular installation.</summary>
public static class GngReviewNotes
{
    public static GngReviewAssessment Assess(GngUpdatePlan plan, string? selectedProfile = null)
    {
        var points = new List<string>();
        bool needsAttention = false;
        void Flag(string point) { needsAttention = true; points.Add(point); }
        var context = plan.ReviewContext;
        if (plan.IsTestingPackage) Flag("This is a testing package, not a published stable release.");
        if (context is null)
            Flag("Some installation checks could not be confirmed. Open View details before installing.");
        else
        {
            if (context.HasExistingFiles && !context.HasVerifiedBaseline)
                Flag("We can’t compare your current files with their original package. Custom changes may be replaced; originals will be backed up.");
            int customized = Math.Max(context.CustomizedReplacementCount, plan.Files.Count(file => file.Action == "Replace customized default"));
            if (customized > 0)
                Flag($"{customized} customized {(customized == 1 ? "package file will" : "package files will")} be replaced. Your originals will be backed up.");
            if (context.HasVerifiedBaseline && context.UnmatchedReplacementCount > 0)
                Flag($"{context.UnmatchedReplacementCount} existing {(context.UnmatchedReplacementCount == 1 ? "file could" : "files could")} not be compared with the original package and will be replaced. Your originals will be backed up.");
            AddPluginNotes(context.PreservedCustomPluginCount, context.PreservedCustomPlugins, false, Flag);
            if (context.HasVerifiedBaseline)
                AddPluginNotes(context.UnverifiedDependencyCount, context.UnverifiedDependencies, true, Flag);
            if (context.RdfOverrideCount > 0)
                Flag("Your local TopSky settings override the package's RDF setting. They will be kept.");
            if (!string.IsNullOrWhiteSpace(selectedProfile) && !plan.Files.Any(file =>
                Path.GetExtension(file.RelativePath).Equals(".prf", StringComparison.OrdinalIgnoreCase) && SamePath(file.RelativePath, selectedProfile, plan.DataFolder)))
                Flag("Your selected launch profile is not in this package and will stay unchanged.");
            if (context.ReinstallsGeneration && plan.Files.Any(file => file.WritesFile))
                Flag("This version is already present. Installing will repair missing files and refresh package defaults.");
            var unclassified = plan.Warnings.Except(context.ClassifiedWarnings, StringComparer.Ordinal).ToArray();
            foreach (var warning in unclassified.Take(3))
                Flag(Compact(warning, 300));
            if (unclassified.Length > 3)
                Flag($"{unclassified.Length - 3} more notes need review. Open View details → Technical notes before installing.");
            int addedProfiles = plan.Files.Count(file => file.Action == "Add profile");
            if (addedProfiles > 0)
                points.Add($"{addedProfiles} new EuroScope {(addedProfiles == 1 ? "profile will" : "profiles will")} need your controller details.");
        }
        if (!plan.PreserveListLayout) Flag("List positions and visibility will reset to the package defaults.");
        if (!needsAttention)
            points.Insert(0, "Installation checks passed. No issues found.");
        return new(needsAttention, needsAttention ? "Check before installing" : "Ready to install", points);
    }

    private static void AddPluginNotes(int count, IReadOnlyList<GngReviewPlugin> plugins, bool unverifiedOriginal, Action<string> flag)
    {
        if (count <= 0) return;
        foreach (var plugin in plugins.Take(4))
        {
            var name = Compact(plugin.FileName, 100);
            var profiles = plugin.Profiles.Take(2).Select(profile => Compact(Path.GetFileName(profile), 75)).ToArray();
            var profileText = profiles.Length == 0 ? "your EuroScope profiles" : string.Join(" and ", profiles) + (plugin.Profiles.Count > 2 ? " and other profiles" : "");
            var reason = unverifiedOriginal
                ? "Its installed copy could not be matched to the original package."
                : plugin.SuppliedByPackage ? "GNG supplies its DLL, but does not load it in the package profiles."
                : "This GNG package does not include or update it.";
            var next = plugin.FileName.Equals("VatEFS.dll", StringComparison.OrdinalIgnoreCase)
                ? "Check VatEFS in Apps and its release notes before installing GNG."
                : "Check its release notes or ask the navteam before installing.";
            flag($"{name} stays in the plugin list for {profileText}. {reason} Launchpad cannot check that it works with this GNG version. {next}");
        }
        int remaining = count - Math.Min(4, plugins.Count);
        if (remaining > 0)
            flag($"{remaining}{(plugins.Count > 0 ? " more" : "")} {(unverifiedOriginal ? "unverified" : "extra")} {(remaining == 1 ? "plugin will" : "plugins will")} be kept. " +
                (count > plugins.Count ? "Review the plugin lists in your EuroScope profiles before installing."
                    : "Open View details → Technical notes to identify them and check their compatibility before installing."));
    }

    private static string Compact(string text, int limit)
    {
        var cleaned = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return cleaned.Length <= limit ? cleaned : cleaned[..(limit - 1)] + "…";
    }

    private static bool SamePath(string first, string second, string root)
    {
        try { return string.Equals(Path.GetFullPath(first, root), Path.GetFullPath(second, root), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException) { return false; }
    }

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
