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

    public string AppName { get; init; } = string.Empty;

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
    public bool ShowStandardStatus => !HasSelfUpdate;

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
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            Set(ref _isRunning, value);
            OnPropertyChanged(nameof(LaunchTooltip));
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

    public string LaunchTooltip => IsFolder ? "Open Folder" : (IsRunning ? "Kill" : "Launch");

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
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => Set(ref _statusMessage, value);
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
        CheckStatus.NotConfigured   => "Not configured",
        CheckStatus.Checking        => "Checking...",
        CheckStatus.Error           => "Error",
        CheckStatus.WebApp          => "Web app",
        CheckStatus.Installed       => "Installed",
        _                           => "—"
    };

    public bool ShowDownload =>
        Status == CheckStatus.UpdateAvailable && !string.IsNullOrEmpty(DownloadUrl);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            OnPropertyChanged(name);
        }
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
