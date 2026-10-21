namespace VatscaUpdateChecker.Models;

public class AppSettings
{
    public bool CheckOnStartup { get; set; } = true;
    public bool IsDarkMode { get; set; } = false;
    public bool CompactLayout { get; set; } = true;
    public bool SetupWizardCompleted { get; set; }
    public bool SetupWizardDismissed { get; set; }
    public int SetupWizardStep { get; set; }
    public string SetupWizardRoute { get; set; } = "Existing";
    public bool SetupGuideActive { get; set; }
    public string SetupGuideSection { get; set; } = "Applications";
    public string NotUsedApplications { get; set; } = string.Empty;
    public double TextScale { get; set; } = 1;
    public bool AlwaysOnTop { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public string WindowMonitor { get; set; } = string.Empty;
    public double DetailPanelHeight { get; set; } = 100;
    public string EuroscopeExePath { get; set; } = string.Empty;
    public string EuroscopeDataPath { get; set; } = string.Empty;
    public string TrackAudioExePath { get; set; } = string.Empty;
    public string VacsExePath { get; set; } = string.Empty;
    public string VatisExePath { get; set; } = string.Empty;
    public string VatEfsPath { get; set; } = string.Empty;
    public bool PatchVatEfs { get; set; } = false;
    public string SaamanDllPath { get; set; } = string.Empty;
    public string SaamanProfilePath { get; set; } = string.Empty;
    public bool PatchSaaman { get; set; }
    // An untouched, unchecked option must not remove a manually configured plugin.
    public bool SaamanProfileManaged { get; set; }

    // EuroScope profile fields (password is stored separately in Windows Credential Manager)
    public string VatsimName { get; set; } = string.Empty;
    public int VatsimRating { get; set; } = -1;
    public string VatsimCid { get; set; } = string.Empty;
    public string ObsCallsign { get; set; } = string.Empty;
    public string LastEuroscopeProfile { get; set; } = string.Empty;

    // Settings currently contain only value types and immutable strings. Keep every
    // preference when a dialog makes an editable copy, including future additions.
    public AppSettings Copy() => (AppSettings)MemberwiseClone();
}
