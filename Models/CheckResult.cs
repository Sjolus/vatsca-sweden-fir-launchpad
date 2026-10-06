using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker.Models;

public enum CheckStatus { Unknown, Checking, UpToDate, UpdateAvailable, Unsupported, NotConfigured, Error, WebApp, Installed }

public class CheckResult : INotifyPropertyChanged
{
    private ProfileOption? _selectedProfile;
    private string _installedVersion = "—";
    private string _latestVersion = "—";
    private CheckStatus _status = CheckStatus.Unknown;
    private string _statusMessage = string.Empty;
    private string _downloadUrl = string.Empty;
    private string _launchPath = string.Empty;
    private bool _isRunning;
    private bool _isSelected;

    public string AppName { get; init; } = string.Empty;
    public string Description => ApplicationDescriptions.For(AppName);
    public string DisplayName => HasFontsCheck ? "Swedish GNG package" : AppName;

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    // These labels describe the existing state; they never decide whether an operation is allowed.
    public string DisplayInstalledVersion => IsWebApp && !IsLocalUrl ? "Not applicable" :
        InstalledVersion == "Installed" ? "Not reported" :
        InstalledVersion is "Executable not found" or "Folder not found" or "Not installed" ? "Not found" :
        IsVersionDiagnostic(InstalledVersion) ? "Not reported" :
        !IsEmptyVersion(InstalledVersion) ? InstalledVersion :
        Status == CheckStatus.NotConfigured || HasSoftwareUpdate && SoftwareNeedsSetup ? "Not set up" :
        Status is CheckStatus.Unknown or CheckStatus.Checking ? "Not checked" : "Not reported";

    public string DisplayLatestVersion => IsWebApp && !IsLocalUrl ? "Not applicable" :
        IsVersionDiagnostic(LatestVersion) ? "Not available" :
        !IsEmptyVersion(LatestVersion) ? LatestVersion :
        SelfUpdatesNotPublished ? "Not published" : "Not checked";

    private bool SelfUpdatesNotPublished => HasSelfUpdate && SelfUpdateSummary == "Updates not published yet";

    public string LatestVersionCaption => HasEuroScopeManagement ? "Sweden supported" :
        HasFontsCheck ? "Latest package" : IsLocalUrl ? "No update source" :
        IsWebApp ? "Web service" :
        SoftwareApp == global::VatscaUpdateChecker.Models.SoftwareApp.Vatis ? "Compatible release" : "Latest stable";

    public string RowStatusText
    {
        get
        {
            if (HasSoftwareUpdate)
            {
                if (SoftwareNeedsSetup && !SoftwareUpdateBusy)
                    return string.IsNullOrWhiteSpace(LaunchPath) ? "Not set up" : "File not found";
                return SoftwareUpdate?.Phase switch
                {
                    SoftwareUpdatePhase.Checking => "Checking…",
                    SoftwareUpdatePhase.Available => "Update available",
                    SoftwareUpdatePhase.Current => "Up to date",
                    SoftwareUpdatePhase.Downloading => SoftwareUpdate.ProgressPercent.HasValue
                        ? $"Downloading {SoftwareUpdate.ProgressPercent:0}%" : "Downloading…",
                    SoftwareUpdatePhase.Verifying => "Verifying download…",
                    SoftwareUpdatePhase.BackingUp => "Backing up…",
                    SoftwareUpdatePhase.Installing => "Installing…",
                    SoftwareUpdatePhase.Completed => "Updated",
                    SoftwareUpdatePhase.Unavailable => "Needs review",
                    SoftwareUpdatePhase.Cancelled => "Update cancelled",
                    SoftwareUpdatePhase.Error => "Needs attention",
                    _ => "Not checked"
                };
            }
            if (HasSelfUpdate)
                return SelfUpdateSummary switch
                {
                    "Updates not published yet" => "Updates not published",
                    "Manual updates" => "Installer needed",
                    "Error" => "Needs attention",
                    "—" or "" => "Not checked",
                    _ => SelfUpdateSummary
                };
            if (HasFontsCheck && FontsState == FontsState.NeedsAction && Status is CheckStatus.UpToDate or CheckStatus.Installed or CheckStatus.Unknown)
                return "Fonts need attention";
            return Status switch
            {
                CheckStatus.UpToDate => HasEuroScopeManagement ? "Supported version" : "Up to date",
                CheckStatus.UpdateAvailable => "Update available",
                CheckStatus.Unsupported => "Unsupported version",
                CheckStatus.NotConfigured => "Not set up",
                CheckStatus.Checking => "Checking…",
                CheckStatus.Error => "Needs attention",
                CheckStatus.WebApp => "Web app",
                CheckStatus.Installed => "Plugin detected",
                _ => "Not checked"
            };
        }
    }

    public string RowStatusKind
    {
        get
        {
            if (HasSoftwareUpdate)
                return SoftwareNeedsSetup && !SoftwareUpdateBusy ? "Warning" : SoftwareUpdate?.Phase switch
                {
                    SoftwareUpdatePhase.Unavailable or SoftwareUpdatePhase.Error or SoftwareUpdatePhase.Cancelled => "Warning",
                    SoftwareUpdatePhase.Available => "Update",
                    SoftwareUpdatePhase.Current or SoftwareUpdatePhase.Completed => "Success",
                    _ => "Neutral"
                };
            if (HasSelfUpdate && SelfUpdateSummary is "Restart failed" or "Update check failed") return "Warning";
            if (HasFontsCheck && FontsState == FontsState.NeedsAction && Status is CheckStatus.UpToDate or CheckStatus.Installed or CheckStatus.Unknown)
                return "Warning";
            return Status switch
            {
                CheckStatus.Unsupported or CheckStatus.Error or CheckStatus.NotConfigured => "Warning",
                CheckStatus.UpdateAvailable => "Update",
                CheckStatus.UpToDate or CheckStatus.Installed => "Success",
                _ => "Neutral"
            };
        }
    }

    public string RowStatusSymbol => RowStatusKind switch
    {
        "Warning" => "!",
        "Update" => "↓",
        "Success" => "✓",
        _ => "·"
    };

    public string RowExplanation
    {
        get
        {
            if (HasSoftwareUpdate)
            {
                if (SoftwareNeedsSetup && !SoftwareUpdateBusy)
                    return string.IsNullOrWhiteSpace(LaunchPath)
                        ? "Find an existing copy or install"
                        : "Review the saved application path";
                return SoftwareUpdate?.Phase switch
                {
                    SoftwareUpdatePhase.Checking => "Checking this copy and releases",
                    SoftwareUpdatePhase.Available => IsRunning ? "Close the app to update" : "Update when ready",
                    SoftwareUpdatePhase.Current => "No newer compatible release",
                    SoftwareUpdatePhase.Downloading => "Downloading in the background",
                    SoftwareUpdatePhase.Verifying => "Checking the downloaded installer",
                    SoftwareUpdatePhase.BackingUp => "Saving a recovery copy",
                    SoftwareUpdatePhase.Installing => "Keep Launchpad open",
                    SoftwareUpdatePhase.Completed => "Ready to launch",
                    SoftwareUpdatePhase.Unavailable => "Review this installation",
                    SoftwareUpdatePhase.Cancelled => "Check again when ready",
                    SoftwareUpdatePhase.Error => "Review the error and recovery steps",
                    _ => "Check this installation for updates"
                };
            }
            if (HasSelfUpdate)
            {
                if (SelfUpdateBusy) return "Progress is shown in this row";
                if (SelfUpdatesNotPublished) return "Use the releases page for now";
                if (SelfUpdateSummary == "Ready to restart") return "Downloaded; restart to apply";
                if (SelfUpdateSummary == "Manual updates") return "Install to enable in-app updates";
                return Status switch
                {
                    CheckStatus.UpToDate => "No newer Launchpad update found",
                    CheckStatus.UpdateAvailable => "Download when ready",
                    CheckStatus.Error => "Review the error before retrying",
                    _ => "Updates start only when you choose"
                };
            }
            if (Status == CheckStatus.Error) return "Review the failed check";
            if (Status == CheckStatus.Checking) return "Checking location and version";
            if (Status == CheckStatus.NotConfigured)
                return HasFontsCheck ? "Choose your GNG package folder" :
                    IsLocalUrl ? "Choose the VatEFS.dll folder" : "Find an existing copy or install";
            if (HasEuroScopeManagement && !IsEmptyVersion(LatestVersion))
                return $"Sweden supports {LatestVersion.TrimStart('v', 'V')}";
            if (HasFontsCheck)
                return FontsState == FontsState.NeedsAction ? "Review the required GNG fonts" :
                    Status == CheckStatus.UpdateAvailable ? "Download from AeroNav" : "Swedish airspace data for EuroScope";
            if (IsLocalUrl) return IsLocalUrlReachable
                ? "Local flight-strip service detected" : "Start the plugin in EuroScope";
            if (IsWebApp) return "Opens in a browser window";
            return Status == CheckStatus.UpToDate ? "No newer version found" : "Check for updates";
        }
    }

    public string DetailHeading => $"{DisplayName} · {RowStatusText}";
    public string DetailText
    {
        get
        {
            var paragraphs = new List<string>();
            AddDetail(paragraphs, StatusMessage);
            if (HasSoftwareUpdate) AddDetail(paragraphs, SoftwareUpdate?.Message);
            if (SoftwareUpdate?.BackupFolder is { Length: > 0 } backup)
                AddDetail(paragraphs, "Recovery copy: " + backup);
            if (IsVersionDiagnostic(InstalledVersion)) AddDetail(paragraphs, "Installed version check: " + InstalledVersion);
            if (IsVersionDiagnostic(LatestVersion)) AddDetail(paragraphs, "Available version check: " + LatestVersion);
            if (HasFontsCheck && FontsState == FontsState.NeedsAction)
                AddDetail(paragraphs, string.IsNullOrWhiteSpace(FontsTooltip)
                    ? "Required GNG fonts need attention. Choose Fonts to review them."
                    : FontsTooltip);
            if (HasFontsCheck && Status == CheckStatus.UpdateAvailable)
                AddDetail(paragraphs, "Download the package from AeroNav and install it manually. GNG installation is not handled by this version of Launchpad.");
            if (SoftwareNeedsSetup && !SoftwareUpdateBusy) AddDetail(paragraphs, RowExplanation);
            if (paragraphs.Count == 0)
            {
                if (HasSelfUpdate) AddDetail(paragraphs, SelfUpdateNotice);
                if (paragraphs.Count == 0) AddDetail(paragraphs, RowExplanation);
                if (Status is CheckStatus.UpToDate or CheckStatus.WebApp or CheckStatus.Installed)
                    AddDetail(paragraphs, Description);
            }
            return string.Join("\n\n", paragraphs);
        }
    }

    public string LaunchActionText => IsFolder ? "Folder" : IsRunning ? "Stop" : IsWebApp ? "Open" : "Launch";
    public string RowOptionsText => "Details…";

    private static bool IsEmptyVersion(string? value) => string.IsNullOrWhiteSpace(value) ||
        value is "—" or "-" or "N/A" or "unknown" or "vunknown";

    private static bool IsVersionDiagnostic(string value) => value is
        "Executable not found" or "Folder not found" or "No SCT file found" or "Cannot parse filename" or "Parse error" or "Cannot fetch";

    private static void AddDetail(List<string> paragraphs, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text) && !paragraphs.Contains(text)) paragraphs.Add(text);
    }

    /// <summary>Launch profile options — populated only for EuroScope.</summary>
    public ObservableCollection<ProfileOption> Profiles { get; }

    public CheckResult()
    {
        Profiles = new ObservableCollection<ProfileOption>();
        Profiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasProfiles));
            OnPropertyChanged(nameof(ShowSimpleLaunch));
            OnPropertyChanged(nameof(ShowSplitLaunch));
        };
    }

    public ProfileOption? SelectedProfile
    {
        get => _selectedProfile;
        set => Set(ref _selectedProfile, value);
    }

    public bool HasProfiles => Profiles.Count > 0;

    /// <summary>True for rows that open a folder rather than launch an exe (e.g. GNG Pack).</summary>
    public bool IsFolder { get; init; }

    /// <summary>True for web app rows that are launched via Edge --app= (e.g. VATIRIS).</summary>
    public bool IsWebApp { get; init; }

    /// <summary>True when the row should render the fonts-check sub-button (currently only the GNG Pack row).</summary>
    public bool HasFontsCheck { get; init; }

    /// <summary>Uses the dedicated inline Launchpad update action in the existing status column.</summary>
    public bool HasSelfUpdate { get; init; }
    public bool HasEuroScopeManagement { get; init; }
    public string EuroScopeSetupText => string.IsNullOrWhiteSpace(LaunchPath) || !File.Exists(LaunchPath) ? "Set up…" : "Manage…";
    public bool ShowConfigure => (HasFontsCheck && string.IsNullOrWhiteSpace(LaunchPath)) ||
        (IsLocalUrl && Status is CheckStatus.Unknown or CheckStatus.NotConfigured);
    public string ConfigureTooltip => HasFontsCheck
        ? "Choose your installed GNG package in App settings → Program files and data folders → EuroScope folder for Swedish GNG data. GNG installation is manual in this version."
        : "Optional EuroScope plugin: choose the folder containing VatEFS.dll in App settings → Program files and data folders, then enable it in Controller profile.";
    public SoftwareApp? SoftwareApp { get; init; }
    public bool HasSoftwareUpdate => SoftwareApp.HasValue;
    public bool ShowStandardStatus => !HasSelfUpdate && !HasSoftwareUpdate;

    private SoftwareUpdateState? _softwareUpdate;
    public SoftwareUpdateState? SoftwareUpdate
    {
        get => _softwareUpdate;
        set { Set(ref _softwareUpdate, value); NotifySoftwareUpdate(); }
    }

    private bool _softwareActionsAllowed = true;
    public bool SoftwareActionsAllowed
    {
        get => _softwareActionsAllowed;
        set
        {
            Set(ref _softwareActionsAllowed, value);
            OnPropertyChanged(nameof(SoftwareActionEnabled));
            OnPropertyChanged(nameof(LaunchEnabled));
        }
    }

    public bool SoftwareUpdateBusy => SoftwareUpdate?.IsBusy == true;
    public bool SoftwareCanCancel => SoftwareUpdate?.CanCancel == true;
    public double SoftwareProgressValue => SoftwareUpdate?.ProgressPercent ?? 0;
    public bool SoftwareProgressIndeterminate => SoftwareUpdateBusy && SoftwareUpdate?.ProgressPercent == null;
    public bool LaunchEnabled => !HasSoftwareUpdate || SoftwareActionsAllowed && !SoftwareUpdateBusy;
    public bool SoftwareActionEnabled => SoftwareActionsAllowed && !(IsRunning && SoftwareUpdate?.CanUpdate == true);
    public bool SoftwareNeedsSetup => HasSoftwareUpdate && (string.IsNullOrWhiteSpace(LaunchPath) || !File.Exists(LaunchPath));
    public bool ShowSoftwareSetup => HasSoftwareUpdate && !SoftwareUpdateBusy;
    public string SoftwareSetupText => SoftwareNeedsSetup ? "Set up…" : "Manage…";
    public string SoftwareDetail => SoftwareNeedsSetup && SoftwareUpdate?.Phase is null or SoftwareUpdatePhase.Idle or SoftwareUpdatePhase.Unavailable
        ? "Use Set up to find an existing copy or install this app."
        : StatusMessage;
    public bool HasSoftwareDetail => !string.IsNullOrWhiteSpace(SoftwareDetail);
    public bool ShowSoftwareDownloads => !SoftwareNeedsSetup && SoftwareUpdate?.Phase == SoftwareUpdatePhase.Error;
    public bool ShowSoftwareAction => HasSoftwareUpdate && !SoftwareNeedsSetup && !SoftwareUpdateBusy &&
        SoftwareUpdate?.Phase is not (SoftwareUpdatePhase.Current or SoftwareUpdatePhase.Completed);
    public string SoftwareActionText => SoftwareUpdate?.Phase switch
    {
        SoftwareUpdatePhase.Available => "Update",
        SoftwareUpdatePhase.Unavailable => "Open downloads ↗",
        SoftwareUpdatePhase.Error or SoftwareUpdatePhase.Cancelled => "Check again",
        _ => "Check for updates"
    };
    public string SoftwareSummary => SoftwareNeedsSetup && !SoftwareUpdateBusy
        ? string.IsNullOrWhiteSpace(LaunchPath) ? "Not set up" : "Configured file not found"
        : SoftwareUpdate?.Phase switch
    {
        SoftwareUpdatePhase.Checking => "Checking…",
        SoftwareUpdatePhase.Available => IsRunning ? "Close the app to update" : "Update available",
        SoftwareUpdatePhase.Current => "Up to date",
        SoftwareUpdatePhase.Downloading => SoftwareUpdate.ProgressPercent.HasValue
            ? $"Downloading {SoftwareUpdate.ProgressPercent:0}%" : "Downloading…",
        SoftwareUpdatePhase.Verifying => "Verifying download…",
        SoftwareUpdatePhase.BackingUp => "Creating recovery backup…",
        SoftwareUpdatePhase.Installing => "Installing — please wait…",
        SoftwareUpdatePhase.Completed => "Updated · ready to launch",
        SoftwareUpdatePhase.Unavailable => "Manual update",
        SoftwareUpdatePhase.Cancelled => "Update cancelled",
        SoftwareUpdatePhase.Error => "Update needs attention",
        _ => "Not checked"
    };

    private void NotifySoftwareUpdate()
    {
        foreach (var name in new[] { nameof(SoftwareUpdateBusy), nameof(SoftwareCanCancel),
            nameof(SoftwareProgressValue), nameof(SoftwareProgressIndeterminate), nameof(LaunchEnabled),
            nameof(SoftwareActionEnabled), nameof(ShowSoftwareAction), nameof(ShowSoftwareDownloads), nameof(SoftwareActionText), nameof(SoftwareSummary),
            nameof(SoftwareNeedsSetup), nameof(ShowSoftwareSetup), nameof(SoftwareSetupText), nameof(SoftwareDetail), nameof(HasSoftwareDetail) })
            OnPropertyChanged(name);
    }

    private string _selfUpdateSummary = "Not checked";
    public string SelfUpdateSummary
    {
        get => _selfUpdateSummary;
        set => Set(ref _selfUpdateSummary, value);
    }

    private string _selfUpdateNotice = string.Empty;
    public string SelfUpdateNotice
    {
        get => _selfUpdateNotice;
        set
        {
            Set(ref _selfUpdateNotice, value);
            OnPropertyChanged(nameof(HasSelfUpdateNotice));
        }
    }
    public bool HasSelfUpdateNotice => !string.IsNullOrEmpty(SelfUpdateNotice);

    private string _selfUpdateActionText = "Check for updates";
    public string SelfUpdateActionText
    {
        get => _selfUpdateActionText;
        set => Set(ref _selfUpdateActionText, value);
    }

    private bool _showSelfUpdateAction;
    public bool ShowSelfUpdateAction
    {
        get => _showSelfUpdateAction;
        set
        {
            Set(ref _showSelfUpdateAction, value);
            OnPropertyChanged(nameof(ShowSelfUpdateStatus));
        }
    }
    public bool ShowSelfUpdateStatus => HasSelfUpdate && !ShowSelfUpdateAction;

    private bool _selfUpdateActionEnabled = true;
    public bool SelfUpdateActionEnabled
    {
        get => _selfUpdateActionEnabled;
        set => Set(ref _selfUpdateActionEnabled, value);
    }

    private bool _selfUpdateBusy;
    public bool SelfUpdateBusy
    {
        get => _selfUpdateBusy;
        set
        {
            Set(ref _selfUpdateBusy, value);
            OnPropertyChanged(nameof(SelfUpdateProgressIndeterminate));
        }
    }

    private int? _selfUpdateProgress;
    public int? SelfUpdateProgress
    {
        get => _selfUpdateProgress;
        set
        {
            Set(ref _selfUpdateProgress, value);
            OnPropertyChanged(nameof(SelfUpdateProgressValue));
            OnPropertyChanged(nameof(SelfUpdateProgressIndeterminate));
        }
    }
    public int SelfUpdateProgressValue => SelfUpdateProgress ?? 0;
    public bool SelfUpdateProgressIndeterminate => SelfUpdateBusy && SelfUpdateProgress == null;

    private FontsState _fontsState = FontsState.Unknown;
    public FontsState FontsState
    {
        get => _fontsState;
        set => Set(ref _fontsState, value);
    }

    private string _fontsTooltip = string.Empty;
    public string FontsTooltip
    {
        get => _fontsTooltip;
        set => Set(ref _fontsTooltip, value);
    }

    /// <summary>True when LaunchPath is a local-server URL (e.g. http://localhost:17770) — the
    /// Launch button only shows while <see cref="IsLocalUrlReachable"/> is true.</summary>
    public bool IsLocalUrl { get; init; }

    private bool _isLocalUrlReachable;
    public bool IsLocalUrlReachable
    {
        get => _isLocalUrlReachable;
        set
        {
            Set(ref _isLocalUrlReachable, value);
            OnPropertyChanged(nameof(ShowLaunch));
            OnPropertyChanged(nameof(ShowSimpleLaunch));
            OnPropertyChanged(nameof(ShowSplitLaunch));
        }
    }

    public string LaunchPath
    {
        get => _launchPath;
        set
        {
            Set(ref _launchPath, value);
            OnPropertyChanged(nameof(ShowLaunch));
            OnPropertyChanged(nameof(ShowSimpleLaunch));
            OnPropertyChanged(nameof(ShowSplitLaunch));
            OnPropertyChanged(nameof(EuroScopeSetupText));
            OnPropertyChanged(nameof(ShowConfigure));
            NotifySoftwareUpdate();
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            Set(ref _isRunning, value);
            OnPropertyChanged(nameof(LaunchTooltip));
            OnPropertyChanged(nameof(SoftwareActionEnabled));
            OnPropertyChanged(nameof(SoftwareSummary));
        }
    }

    public bool ShowLaunch =>
        !string.IsNullOrEmpty(LaunchPath) &&
        (IsLocalUrl ? IsLocalUrlReachable :
         IsWebApp   ? true :
         IsFolder   ? Directory.Exists(LaunchPath) :
                      File.Exists(LaunchPath));

    /// <summary>Launch button for apps without a profile picker.</summary>
    public bool ShowSimpleLaunch => ShowLaunch && !HasProfiles;

    /// <summary>Split launch+profile button for EuroScope.</summary>
    public bool ShowSplitLaunch => ShowLaunch && HasProfiles;

    public string LaunchTooltip => IsFolder ? "Open the GNG package folder." : IsRunning
        ? $"Force-stop {DisplayName}. Unsaved work may be lost."
        : IsWebApp ? $"Open {DisplayName} in its browser window." : $"Launch {DisplayName}.";

    public string InstalledVersion
    {
        get => _installedVersion;
        set => Set(ref _installedVersion, value);
    }

    public string LatestVersion
    {
        get => _latestVersion;
        set => Set(ref _latestVersion, value);
    }

    public CheckStatus Status
    {
        get => _status;
        set
        {
            Set(ref _status, value);
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(ShowDownload));
            OnPropertyChanged(nameof(ShowConfigure));
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set { Set(ref _statusMessage, value); OnPropertyChanged(nameof(SoftwareDetail)); OnPropertyChanged(nameof(HasSoftwareDetail)); }
    }

    public string DownloadUrl
    {
        get => _downloadUrl;
        set
        {
            Set(ref _downloadUrl, value);
            OnPropertyChanged(nameof(ShowDownload));
        }
    }

    public string StatusText => Status switch
    {
        CheckStatus.UpToDate        => "Up to date",
        CheckStatus.UpdateAvailable => "Update available",
        CheckStatus.Unsupported     => "Unsupported",
        CheckStatus.NotConfigured   => HasEuroScopeManagement ? "Not set up" : "Not configured",
        CheckStatus.Checking        => "Checking...",
        CheckStatus.Error           => "Error",
        CheckStatus.WebApp          => "Web app",
        CheckStatus.Installed       => "Installed",
        _                           => "—"
    };

    public bool ShowDownload =>
        !HasEuroScopeManagement && Status == CheckStatus.UpdateAvailable && !string.IsNullOrEmpty(DownloadUrl);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            OnPropertyChanged(name);
            if (name != nameof(IsSelected)) NotifyPresentation();
        }
    }

    private void NotifyPresentation()
    {
        // Use direct notifications: derived labels must not recursively invalidate one another.
        foreach (var name in new[] { nameof(DisplayInstalledVersion), nameof(DisplayLatestVersion),
            nameof(LatestVersionCaption), nameof(RowStatusText), nameof(RowStatusKind), nameof(RowStatusSymbol),
            nameof(RowExplanation), nameof(DetailHeading), nameof(DetailText), nameof(LaunchActionText) })
            OnPropertyChanged(name);
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
