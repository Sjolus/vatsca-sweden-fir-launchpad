using System.IO;

namespace VatscaUpdateChecker.Models;

public enum SetupWizardRoute { Existing, Fresh, Manual }
public enum SetupWizardApplication { EuroScope, Vacs, Vatis, TrackAudio, VatEfs }
public enum SetupWizardGuide { EuroScopeGng, TrackAudio, Vatis }

/// <summary>
/// Pure wizard state. Only explicit child-dialog saves and verified installation results
/// survive dismissal. Preferences edited in the wizard remain tentative until Finish.
/// No discovery, file access, credentials or process actions belong here.
/// </summary>
public sealed class SetupWizardState
{
    private readonly AppSettings _saved;
    private readonly List<string> _actions = [];
    public AppSettings Draft { get; }
    public SetupWizardRoute Route { get; set; } = SetupWizardRoute.Existing;
    public bool HasSavedActions { get; private set; }
    public bool RestartRequired { get; private set; }
    public IReadOnlyList<string> Actions => _actions.AsReadOnly();

    // This enables only opening the review. The installer still proves that no
    // existing application root or unreviewed data would be overwritten.
    public bool CanReviewFreshSetup(bool executableExists) => !RestartRequired && !executableExists;

    public SetupWizardState(AppSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        _saved = current.Copy();
        Draft = current.Copy();
    }

    public void AcceptSettings(AppSettings settings)
    {
        // Settings does not edit theme/density or controller identity. Copy only its
        // editable values, so opening it cannot implicitly save pending wizard choices.
        foreach (var target in new[] { Draft, _saved })
        {
            target.EuroscopeExePath = settings.EuroscopeExePath;
            target.EuroscopeDataPath = settings.EuroscopeDataPath;
            target.TrackAudioExePath = settings.TrackAudioExePath;
            target.VacsExePath = settings.VacsExePath;
            target.VatisExePath = settings.VatisExePath;
            target.VatEfsPath = settings.VatEfsPath;
            target.CheckOnStartup = settings.CheckOnStartup;
        }
        HasSavedActions = true;
        _actions.Add("Program and folder locations, plus the startup check preference, were saved in Settings. Existing program settings were left in place.");
    }

    public void AcceptControllerProfile(AppSettings settings)
    {
        foreach (var target in new[] { Draft, _saved })
        {
            target.VatsimName = settings.VatsimName;
            target.VatsimRating = settings.VatsimRating;
            target.VatsimCid = settings.VatsimCid;
            target.ObsCallsign = settings.ObsCallsign;
            target.PatchVatEfs = settings.PatchVatEfs;
        }
        HasSavedActions = true;
        _actions.Add("Controller profile saved. Any file changes reported by that dialog have already taken effect.");
    }

    public void RecordInstallation(SetupWizardApplication app, string executablePath,
        bool restartRequired = false, string? backupFolder = null)
    {
        if (!Enum.IsDefined(app)) throw new ArgumentOutOfRangeException(nameof(app));
        if (string.IsNullOrWhiteSpace(executablePath)) throw new ArgumentException("A verified executable path is required.", nameof(executablePath));
        string? vatEfsFolder = null;
        if (app == SetupWizardApplication.VatEfs)
        {
            vatEfsFolder = Path.GetDirectoryName(executablePath);
            if (string.IsNullOrWhiteSpace(vatEfsFolder) || !Path.GetFileName(executablePath).Equals("efs.exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("A verified VatEFS executable path is required.", nameof(executablePath));
        }
        foreach (var target in new[] { Draft, _saved })
        {
            switch (app)
            {
                case SetupWizardApplication.EuroScope: target.EuroscopeExePath = executablePath; break;
                case SetupWizardApplication.Vacs: target.VacsExePath = executablePath; break;
                case SetupWizardApplication.Vatis: target.VatisExePath = executablePath; break;
                case SetupWizardApplication.TrackAudio: target.TrackAudioExePath = executablePath; break;
                case SetupWizardApplication.VatEfs: target.VatEfsPath = vatEfsFolder!; break;
            }
        }
        HasSavedActions = true;
        _actions.Add(Name(app) + " installation completed and was verified. Its program location will be kept.");
        if (!string.IsNullOrWhiteSpace(backupFolder)) _actions.Add("Recovery export: " + backupFolder);
        if (restartRequired) RequireRestart();
    }

    public void RecordIncompleteSetup(string application)
    {
        _actions.Add(application + " setup closed without a confirmed installation. Follow any restart or recovery instructions shown in that window.");
    }

    public void RequireRestart()
    {
        if (!RestartRequired) _actions.Add("Windows restart required. Restart Windows before installing more software or changing controller profiles.");
        RestartRequired = true;
    }

    public AppSettings Finish() => Draft.Copy();
    public AppSettings Dismiss() => _saved.Copy();

    public IReadOnlyList<SetupWizardGuide> NextSteps()
    {
        var guides = new List<SetupWizardGuide>();
        if (!string.IsNullOrWhiteSpace(Draft.EuroscopeExePath) || !string.IsNullOrWhiteSpace(Draft.EuroscopeDataPath))
            guides.Add(SetupWizardGuide.EuroScopeGng);
        if (!string.IsNullOrWhiteSpace(Draft.TrackAudioExePath)) guides.Add(SetupWizardGuide.TrackAudio);
        if (!string.IsNullOrWhiteSpace(Draft.VatisExePath)) guides.Add(SetupWizardGuide.Vatis);
        return guides.AsReadOnly();
    }

    // Keep browser destinations independent of settings, paths and arbitrary UI tags.
    public static string GuideUrl(SetupWizardGuide guide) => guide switch
    {
        SetupWizardGuide.EuroScopeGng => "https://wiki.vatsim-scandinavia.org/books/general/page/euroscope-and-gng-package-installation",
        SetupWizardGuide.TrackAudio => "https://wiki.vatsim-scandinavia.org/books/general/page/observers-guide",
        SetupWizardGuide.Vatis => "https://wiki.vatsim-scandinavia.org/books/general/page/vatis",
        _ => throw new ArgumentOutOfRangeException(nameof(guide))
    };

    public IReadOnlyList<string> Summary()
    {
        var lines = new List<string>
        {
            "Your chosen program files and data folders",
            PathLine("EuroScope", Draft.EuroscopeExePath),
            PathLine("VACS", Draft.VacsExePath),
            PathLine("vATIS", Draft.VatisExePath),
            PathLine("TrackAudio", Draft.TrackAudioExePath),
            PathLine("EuroScope / GNG data folder", Draft.EuroscopeDataPath),
            PathLine("VatEFS folder", Draft.VatEfsPath),
            "GNG setup is available after this guide. Downloading and installing a package need separate actions.",
            "Theme: " + (Draft.IsDarkMode ? "dark" : "light") + "; application list: " + (Draft.CompactLayout ? "compact" : "expanded") + ".",
            "Startup update checks: " + (Draft.CheckOnStartup ? "on (checks only)." : "off."),
        };
        if (_actions.Count == 0) lines.Add("No installation or controller-profile save was completed through this wizard.");
        else { lines.Add("Actions during this setup:"); lines.AddRange(_actions); }
        return lines.AsReadOnly();
    }

    private static string PathLine(string name, string path) => name + ": " +
        (string.IsNullOrWhiteSpace(path) ? "not chosen — optional; add later if needed." : "chosen location — " + path);
    private static string Name(SetupWizardApplication app) => app switch
    {
        SetupWizardApplication.EuroScope => "EuroScope",
        SetupWizardApplication.Vacs => "VACS",
        SetupWizardApplication.Vatis => "vATIS",
        SetupWizardApplication.VatEfs => "VatEFS",
        _ => "TrackAudio"
    };
}
