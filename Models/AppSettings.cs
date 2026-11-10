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

    // Named EuroScope launch sets. Each starts its ordered instances from one Launch press.
    // No active set keeps the single-profile launch from LastEuroscopeProfile.
    public List<EuroScopeLaunchSetDefinition> EuroScopeLaunchSets { get; set; } = [];
    public Guid? ActiveEuroScopeLaunchSetId { get; set; }

    // Value types and strings are copied by MemberwiseClone. Lists hold immutable records,
    // so a new list keeps editors from sharing the caller's collection.
    public AppSettings Copy()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.EuroScopeLaunchSets = [.. EuroScopeLaunchSets ?? []];
        return copy;
    }
}

/// <summary>A named, ordered group of EuroScope instances.</summary>
public sealed record EuroScopeLaunchSetDefinition(Guid Id, string Name, IReadOnlyList<EuroScopeLaunchEntry> Entries)
{
    public const int MaxSets = 20;
    public const int MaxNameLength = 40;
}

/// <summary>One instance in the EuroScope launch set. DelaySeconds is the wait before this entry starts.</summary>
public sealed record EuroScopeLaunchEntry(Guid Id, string ProfilePath, int DelaySeconds, bool Enabled)
{
    public const int MaxDelaySeconds = 300;
    public const int MaxEntries = 16;
}
