using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

// Presentation only. The complete immutable plan remains the authority for every operation.
internal static class ReviewSummaries
{
    internal static string Format(string changes, string kept, string recovery, string? attention = null) =>
        "What changes\n" + changes + "\n\nWhat stays\n" + kept + "\n\nRecovery\n" + recovery +
        (string.IsNullOrWhiteSpace(attention) ? "" : "\n\nBefore you continue\n" + attention);

    internal static string Fresh(FreshSoftwareInstallPlan plan)
    {
        var name = plan.App switch { SoftwareApp.Vacs => "VACS", SoftwareApp.Vatis => "vATIS", SoftwareApp.VatEfs => "VatEFS", _ => "TrackAudio" };
        var channel = plan.Release.IsPrerelease || plan.Release.Version.Contains('-') ? " (prerelease)" : "";
        return Format($"Install {name} {plan.Release.Version}{channel}\n{plan.InstallRoot}\n" +
            (plan.Scope == "AllUsers" ? "For everyone on this PC. Windows will ask for administrator approval." : "For your Windows account."),
            "Existing known settings stay in place. Other applications are not changed. The new application stays closed.",
            plan.BackupDestination == null ? "No existing known settings need a recovery copy." :
                $"Copy {plan.BackupFiles.Files.Count:N0} existing settings files before installation to:\n{plan.BackupDestination}\nKeep this folder private; it may contain passwords.",
            plan.App == SoftwareApp.VatEfs ? "Close EuroScope and the VatEFS backend. After installation, use Controller profile to enable the plugin; installing it does not change your profiles." :
                "Required Microsoft components are installed through the separate runtime action. Once installation starts, wait for it to finish.");
    }

    internal static string EuroScope(EuroScopeInstallPlan plan) => Format(
        (plan.IsDowngrade ? $"Switch from newer EuroScope {plan.InstalledVersion} to Sweden's supported {plan.TargetVersion}. This removes the newer build first." :
            plan.Action == EuroScopeInstallAction.Repair ? $"Repair EuroScope {plan.TargetVersion}. Its bundled files may be replaced." :
            plan.Action == EuroScopeInstallAction.Replace ? $"Replace EuroScope {plan.InstalledVersion} with the supported {plan.TargetVersion}." : $"Install EuroScope {plan.TargetVersion}.") +
        "\n" + plan.InstallRoot + (plan.Scope == "AllUsers" ? "\nWindows will ask for administrator approval." : ""),
        "Other applications stay in place. EuroScope stays closed. Personal changes to EuroScope's bundled files may need restoring from your recovery copy.",
        plan.BackupDestination == null ? "No existing files need a recovery copy." :
            $"Copy {plan.BackupFiles.Files.Count:N0} reviewed files before changes to:\n{plan.BackupDestination}\nRestore personal files manually if needed; keep this folder private.",
        "The installer also manages its standard EuroScope data, shortcuts and font. This uses the supported official package, not the newest public EuroScope release.");

    internal static string Removal(AtcMaintenancePlan plan, bool removeLaunchpad)
    {
        var changes = new List<string>();
        var kept = new List<string>();
        var attention = new List<string>();
        foreach (var choice in plan.Selections)
        {
            string application = choice.Target.Id == AtcRemovalApp.VatEfs
                ? choice.RemoveApplication ? "remove the configured EuroScope plugin DLL" : "keep the EuroScope plugin DLL"
                : choice.RemoveApplication ? "remove application/package" : "keep application";
            string data = choice.RemoveData
                ? choice.Target.Id is AtcRemovalApp.VatEfs or AtcRemovalApp.Vatiris ? "; reset Launchpad's browser session." : "; delete its settings and local data."
                : "; keep a copy of its settings.";
            changes.Add(choice.Target.Name + ": " + application + data);
            if (!choice.RemoveData)
                kept.Add(choice.Target.Name + (choice.RemoveApplication && (choice.Target.Id == AtcRemovalApp.Gng || choice.Target.VendorSpec?.RemovesData == true)
                    ? " settings are kept in the recovery export and need manual restoration." : " settings stay in place."));
            attention.AddRange(choice.Target.Warnings);
        }
        var local = plan.LaunchpadData;
        if (removeLaunchpad) changes.Add("Uninstall Launchpad last, after the other selected work finishes.");
        var localItems = new List<string>();
        if (local.Settings) localItems.Add("preferences and logs");
        if (local.BrowserSessions) localItems.Add("browser sign-ins");
        if (local.Credentials) localItems.Add("saved credentials");
        if (local.Downloads) localItems.Add("cached downloads");
        if (local.Backups) localItems.Add("existing recovery backups");
        if (local.Reporting) localItems.Add("support ID, reporting choices and unsent reports");
        if (localItems.Count != 0) changes.Add("Delete Launchpad's " + string.Join(", ", localItems) + ".");
        kept.Add("Unselected applications and data stay in place. Online accounts stay active.");
        if (!removeLaunchpad) kept.Add("Launchpad stays installed.");
        if (local.Credentials) attention.Add("Saved credentials cannot be exported. Copies in unselected profiles or backups are not erased.");
        attention.Add("Vendor uninstallers may remove their own components and fonts. Reinstallation is a separate step.");
        return Format(string.Join("\n", changes), string.Join("\n", kept),
            plan.BackupDestination == null ? "No recovery export is selected. Removed files and data cannot be restored through Launchpad." :
                $"Copy {plan.BackupFiles.Files.Count:N0} reviewed files before removal to:\n{plan.BackupDestination}\nRecovery is manual; read RESTORE.txt in the completed export. Keep it private.",
            string.Join("\n", attention.Distinct()));
    }

    internal static string Gng(GngUpdatePlan plan, string backupRoot) => Format(
        $"Install Swedish GNG {plan.Version}: {plan.Files.Count(file => file.WritesFile):N0} files will change. Shared radar screens, package defaults and plugin settings may replace your customisations. When supplied, RDF replaces old AFV/RDF profile entries.",
        "Existing sign-in details, supported personal settings, local/Hoppie files and other custom plugins are kept." +
        (plan.PreserveListLayout ? " List positions and whole-list visibility are kept; columns, items and sorting follow the package." : " List positions and visibility use the package defaults."),
        "Changed files are backed up here before installation:\n" + backupRoot,
        "EuroScope must be closed. New profiles need Controller profile setup; old profiles absent from the package are not upgraded. Review the files below and the package notes; View… shows the expected result.");
}
