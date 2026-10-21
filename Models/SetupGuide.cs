namespace VatscaUpdateChecker.Models;

public sealed record SetupGuidePresentation(string Heading, string Message, string NextLabel);
public sealed record SetupGuideTransition(bool Changed, string Destination);

/// <summary>Optional inline guidance. It records navigation, never installation or configuration readiness.</summary>
public static class SetupGuide
{
    public static bool Initialize(AppSettings settings, bool firstLaunch)
    {
        if (settings.SetupGuideActive)
        {
            string section = Section(settings);
            if (settings.SetupGuideSection == section) return false;
            settings.SetupGuideSection = section;
            return true;
        }
        // Older installations without the new marker keep their normal Apps startup.
        return firstLaunch && !settings.SetupWizardCompleted && !settings.SetupWizardDismissed && Start(settings);
    }

    public static bool Start(AppSettings settings)
    {
        string section = settings.SetupWizardCompleted ? "Applications" : Section(settings);
        bool changed = !settings.SetupGuideActive || settings.SetupWizardCompleted || settings.SetupWizardDismissed || settings.SetupGuideSection != section;
        settings.SetupGuideActive = true;
        settings.SetupWizardCompleted = settings.SetupWizardDismissed = false;
        settings.SetupGuideSection = section;
        return changed;
    }

    public static string Section(AppSettings settings) => IsGuidedSection(settings.SetupGuideSection) ? settings.SetupGuideSection : "Applications";

    public static bool RememberSection(AppSettings settings, string section)
    {
        if (!settings.SetupGuideActive || !IsGuidedSection(section) || settings.SetupGuideSection == section) return false;
        settings.SetupGuideSection = section;
        return true;
    }

    public static SetupGuideTransition Next(AppSettings settings, string visibleSection)
    {
        if (!settings.SetupGuideActive) return new(false, "Apps");
        if (!IsGuidedSection(visibleSection)) return new(false, Section(settings));
        if (visibleSection != "Profile")
        {
            string next = visibleSection == "Applications" ? "Gng" : "Profile";
            bool changed = settings.SetupGuideSection != next;
            settings.SetupGuideSection = next;
            return new(changed, next);
        }
        settings.SetupGuideActive = false;
        settings.SetupGuideSection = "Profile";
        settings.SetupWizardCompleted = true;
        settings.SetupWizardDismissed = false;
        settings.SetupWizardStep = 0;
        return new(true, "Apps");
    }

    public static bool Later(AppSettings settings)
    {
        if (!settings.SetupGuideActive) return false;
        settings.SetupGuideActive = false;
        settings.SetupWizardCompleted = false;
        settings.SetupWizardDismissed = true;
        return true;
    }

    public static SetupGuidePresentation? Present(AppSettings settings, string visibleSection)
    {
        if (!settings.SetupGuideActive) return null;
        string section = Section(settings);
        if (!IsGuidedSection(visibleSection))
        {
            string name = section == "Gng" ? "Swedish GNG" : section == "Profile" ? "Controller profile" : "Applications";
            return new("Getting started", "Continue with " + name + " when you're ready.", "Continue: " + name);
        }
        return visibleSection switch
        {
            "Gng" => new("2 · Swedish GNG", "Use or install the Swedish package, then check its fonts. Optional plugins are in Applications.", "Next: Controller profile"),
            "Profile" => new("3 · Controller profile", "Save your controller details when ready. You can leave any part of setup for later.", "Finish setup"),
            _ => new("1 · Applications", "Already have the tools? Find installed apps. Otherwise, install what you need.", "Next: Swedish GNG")
        };
    }

    private static bool IsGuidedSection(string? section) => section is "Applications" or "Gng" or "Profile";
}
