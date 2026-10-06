using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Windows;
using Microsoft.Win32;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class EuroScopeInstallWindow : Window
{
    private readonly AppSettings _settings;
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(20) };
    private readonly EuroScopeInstallService _service;
    private EuroScopeInstallPlan? _plan;
    private CancellationTokenSource? _cancellation;
    private bool _busy;
    private bool _finished;
    public EuroScopeInstallResult? Result { get; private set; }
    public bool RestartRequired { get; private set; }
    public bool RemovalRequested { get; private set; }
    public bool AdoptionRequested { get; private set; }

    public EuroScopeInstallWindow(AppSettings settings)
    {
        InitializeComponent();
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        _settings = settings;
        _service = new(_http);
        SupportedText.Text = "Supported version: " + EuroScopePolicy.SupportedVersion + " · pinned for Sweden";
        ConfiguredPath.Text = string.IsNullOrWhiteSpace(settings.EuroscopeExePath)
            ? "No executable configured. If EuroScope is already installed, use Find existing installations in Settings first."
            : "Configured executable: " + settings.EuroscopeExePath;
    }

    private void InvalidateReview()
    {
        _plan = null;
        ConfirmChange.IsChecked = false;
        ConfirmChange.IsEnabled = false;
        ApplyButton.IsEnabled = false;
    }

    private void ChooseBackup_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose a private EuroScope recovery folder" };
        if (picker.ShowDialog(this) != true) return;
        BackupPath.Text = picker.FolderName;
        InvalidateReview();
        ReviewText.Text = "Recovery folder changed. Review the change again.";
    }

    private async void Review_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _finished) return;
        InvalidateReview();
        SetBusy(true, "Inspecting the configured installation and recovery files…");
        try
        {
            var destination = string.IsNullOrWhiteSpace(BackupPath.Text) ? null : BackupPath.Text;
            _plan = await Task.Run(() => _service.Preview(_settings, destination));
            ReviewText.Text = _plan.Review;
            ApplyButton.Content = _plan.Action + " supported version";
            ConfirmChange.IsEnabled = true;
            StatusText.Text = "Review the complete plan. No download or installation has started.";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { SetBusy(false); }
        if (_plan != null) ShowReviewedPlan();
    }

    private void ShowReviewedPlan() => ReviewScrollHelper.Show(ContentScroll, ReviewPanel, ReviewText,
        () => !_busy && _plan != null && IsVisible);

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _finished || _plan == null || ConfirmChange.IsChecked != true) return;
        var plan = _plan;
        _cancellation = new();
        SetBusy(true, "Preparing the supported EuroScope installer…");
        try
        {
            var progress = new Progress<EuroScopeInstallProgress>(p =>
            {
                if (!_busy) return;
                StatusText.Text = p.Message;
                Activity.IsIndeterminate = !p.Percent.HasValue;
                Activity.Value = p.Percent ?? 0;
                CancelOperationButton.Visibility = p.CanCancel ? Visibility.Visible : Visibility.Collapsed;
            });
            Result = await Task.Run(() => _service.ApplyAsync(plan, progress, _cancellation.Token));
            RestartRequired |= Result.RestartRequired;
            _finished = true;
            StatusText.Text = (Result.InstallationCompleted
                ? "EuroScope " + EuroScopePolicy.SupportedVersion + " verified. The client remains closed."
                : "The previous EuroScope installation was removed, but the supported version has not been installed. Restart Windows, then review installation again; adjust the missing executable path in Settings if needed.") +
                (Result.RestartRequired ? " Windows restart required before further changes." : "") +
                (Result.BackupFolder == null ? "" : " Recovery export: " + Result.BackupFolder);
        }
        catch (OperationCanceledException) { StatusText.Text = "Preparation cancelled. Review again before retrying." + BackupNotice(); }
        catch (Exception ex) { StatusText.Text = "Stopped: " + ex.Message + BackupNotice(); }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            InvalidateReview();
            KeepRestartRequirement();
            SetBusy(false);
        }
    }

    private string BackupNotice() => _service.LastBackupFolder == null ? "" : " Recovery export: " + _service.LastBackupFolder;
    private void KeepRestartRequirement()
    {
        RestartRequired |= _service.RestartRequired;
        if (!RestartRequired) return;
        _finished = true;
        StatusText.Text += " Restart Windows before further installation, removal or profile changes.";
    }
    private void ConfirmationChanged(object sender, RoutedEventArgs e) => ApplyButton.IsEnabled = !_busy && !_finished && _plan != null && ConfirmChange.IsChecked == true;
    private void CancelOperation_Click(object sender, RoutedEventArgs e)
    {
        if (_service.CanCancel) _cancellation?.Cancel();
    }
    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        OptionsPanel.IsEnabled = ReviewButton.IsEnabled = !busy && !_finished;
        ConfirmChange.IsEnabled = !busy && !_finished && _plan != null;
        ApplyButton.IsEnabled = !busy && !_finished && _plan != null && ConfirmChange.IsChecked == true;
        CloseButton.IsEnabled = !busy;
        Activity.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Activity.IsIndeterminate = true;
        CancelOperationButton.Visibility = Visibility.Collapsed;
        if (message != null) StatusText.Text = message;
    }
    private void ReleaseNotes_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(EuroScopePolicy.ReleaseNotesUrl) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = "Could not open release notes: " + ex.Message; }
    }
    private async void Prerequisite_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _finished) return;
        InvalidateReview();
        SetBusy(true, "Checking the Visual C++ x86 prerequisite…");
        try
        {
            var problem = await Task.Run(EuroScopePrerequisiteService.GetProblem);
            if (problem == null) { StatusText.Text = "The verified Visual C++ x86 baseline is already installed."; return; }
            if (MessageBox.Show(this, problem + "\n\nDownload and install Microsoft's signed Visual C++ x86 runtime now? Windows may request administrator approval. This is a shared Windows component and will be kept when Launchpad is removed.",
                "EuroScope prerequisite", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
            { StatusText.Text = "Runtime installation was not requested."; return; }
            var progress = new Progress<string>(message => { if (_busy) StatusText.Text = message; });
            var result = await Task.Run(() => EuroScopePrerequisiteService.InstallAsync(progress, onRestartRequired: () => RestartRequired = true));
            RestartRequired |= result.RestartRequired;
            StatusText.Text = result.RestartRequired
                ? "Visual C++ installed. Restart Windows before installing EuroScope."
                : "Visual C++ x86 is ready. Review the EuroScope change when you are ready.";
            if (result.RestartRequired) _finished = true;
        }
        catch (Exception ex) { StatusText.Text = "Visual C++ preparation stopped: " + ex.Message; }
        finally { KeepRestartRequirement(); SetBusy(false); }
    }
    private void Removal_Click(object sender, RoutedEventArgs e) { RemovalRequested = true; Close(); }
    private void Adopt_Click(object sender, RoutedEventArgs e) { AdoptionRequested = true; Close(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_busy) { e.Cancel = true; StatusText.Text = "Wait for completion, or cancel preparation while that action is available."; }
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e)
    {
        _http.Dispose();
        base.OnClosed(e);
    }
}
