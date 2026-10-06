using System.ComponentModel;
using System.Net.Http;
using System.Windows;
using Microsoft.Win32;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class FreshSoftwareInstallWindow : Window
{
    private readonly SoftwareApp _app;
    private readonly AppSettings _settings;
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(20) };
    private readonly FreshSoftwareInstallService _service;
    private FreshSoftwareInstallPlan? _plan;
    private CancellationTokenSource? _cancellation;
    private bool _busy, _previewing, _finished;
    public FreshSoftwareInstallResult? Result { get; private set; }
    public bool RestartRequired { get; private set; }
    public bool AdoptionRequested { get; private set; }
    public bool RemovalRequested { get; private set; }

    public FreshSoftwareInstallWindow(SoftwareApp app, AppSettings settings)
    {
        InitializeComponent();
        MinHeight = Math.Min(MinHeight, SystemParameters.WorkArea.Height);
        MinWidth = Math.Min(MinWidth, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        _app = app; _settings = settings; _service = new(_http);
        string name = app switch { SoftwareApp.Vacs => "VACS", SoftwareApp.Vatis => "vATIS", _ => "TrackAudio" };
        Title = Heading.Text = "Set up " + name;
        ApplyButton.Content = "Install " + name;
        AllowVatisBeta.Visibility = app == SoftwareApp.Vatis ? Visibility.Visible : Visibility.Collapsed;
        var path = app switch { SoftwareApp.Vacs => settings.VacsExePath, SoftwareApp.Vatis => settings.VatisExePath, _ => settings.TrackAudioExePath };
        ConfiguredPath.Text = string.IsNullOrWhiteSpace(path) ? "No executable configured in Launchpad." : "Currently configured: " + path;
    }

    private void InvalidateReview()
    {
        _plan = null; ConfirmInstall.IsChecked = false; ConfirmInstall.IsEnabled = false; ApplyButton.IsEnabled = false;
    }
    private void ChooseBackup_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose a private recovery folder outside application data" };
        if (picker.ShowDialog(this) != true) return;
        BackupPath.Text = picker.FolderName; InvalidateReview();
        ReviewText.Text = "Recovery folder changed. Review the fresh install again.";
    }
    private async void Review_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _finished) return;
        InvalidateReview(); _cancellation = new(); _previewing = true;
        SetBusy(true, "Checking known installations, existing data and the official release…");
        CancelOperationButton.Visibility = Visibility.Visible;
        try
        {
            string? backup = string.IsNullOrWhiteSpace(BackupPath.Text) ? null : BackupPath.Text;
            bool allowBeta = AllowVatisBeta.IsChecked == true;
            _plan = await Task.Run(() => _service.PreviewAsync(_app, _settings, backup, allowBeta, _cancellation.Token));
            ReviewText.Text = _plan.Review;
            if (_app == SoftwareApp.Vatis) ApplyButton.Content = _plan.Release.Version.Contains('-') ? "Install vATIS beta" : "Install vATIS";
            StatusText.Text = "Review the full plan. No application package has been downloaded or installed.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Review cancelled. Nothing was installed."; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { _previewing = false; _cancellation.Dispose(); _cancellation = null; SetBusy(false); }
        if (_plan != null) ShowReviewedPlan();
    }
    private void ShowReviewedPlan() => ReviewScrollHelper.Show(ContentScroll, ReviewPanel, ReviewText,
        () => !_busy && _plan != null && IsVisible);
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _finished || _plan == null || ConfirmInstall.IsChecked != true) return;
        var plan = _plan; _cancellation = new();
        SetBusy(true, "Preparing the reviewed installation…");
        try
        {
            var progress = new Progress<FreshSoftwareInstallProgress>(p =>
            {
                if (!_busy) return;
                StatusText.Text = p.Message;
                Activity.IsIndeterminate = !p.Percent.HasValue; Activity.Value = p.Percent ?? 0;
                CancelOperationButton.Visibility = p.CanCancel ? Visibility.Visible : Visibility.Collapsed;
            });
            Result = await Task.Run(() => _service.ApplyAsync(plan, progress, _cancellation.Token));
            RestartRequired |= Result.RestartRequired;
            _finished = true;
            StatusText.Text = "Installation verified. The application remains closed." +
                (Result.RestartRequired ? " Restart Windows before further changes." : "") + BackupNotice();
        }
        catch (OperationCanceledException) { StatusText.Text = "Preparation cancelled. Review again before retrying." + BackupNotice(); }
        catch (Exception ex) { StatusText.Text = "Installation stopped: " + ex.Message + BackupNotice(); }
        finally { _cancellation.Dispose(); _cancellation = null; InvalidateReview(); KeepRestartRequirement(); SetBusy(false); }
    }
    private string BackupNotice() => _service.LastBackupFolder == null ? "" : " Recovery export: " + _service.LastBackupFolder;
    private void KeepRestartRequirement()
    {
        RestartRequired |= _service.RestartRequired;
        if (!RestartRequired) return;
        _finished = true;
        StatusText.Text += " Restart Windows before further installation, removal or profile changes.";
    }
    private async void Prerequisite_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _finished) return;
        InvalidateReview(); SetBusy(true, "Checking required runtime…");
        try
        {
            var problem = await Task.Run(() => SoftwarePrerequisiteService.GetProblem(_app));
            if (problem == null) { StatusText.Text = "No missing prerequisite was found."; return; }
            if (MessageBox.Show(this, problem + "\n\nDownload and install the required Microsoft runtime now? Windows may request administrator approval. Shared runtimes are kept when Launchpad is removed.",
                "Application prerequisite", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
            { StatusText.Text = "Runtime installation was not requested."; return; }
            var progress = new Progress<string>(message => { if (_busy) StatusText.Text = message; });
            var result = await Task.Run(() => SoftwarePrerequisiteService.InstallAsync(_app, progress, onRestartRequired: () => RestartRequired = true));
            RestartRequired |= result.RestartRequired;
            StatusText.Text = result.RestartRequired ? "Runtime installed. Restart Windows before continuing." : "Runtime ready. Review the application installation when ready.";
            if (result.RestartRequired) _finished = true;
        }
        catch (Exception ex) { StatusText.Text = "Runtime setup stopped: " + ex.Message; }
        finally { KeepRestartRequirement(); SetBusy(false); }
    }
    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy; OptionsPanel.IsEnabled = ReviewButton.IsEnabled = !busy && !_finished;
        ConfirmInstall.IsEnabled = !busy && !_finished && _plan != null;
        ApplyButton.IsEnabled = !busy && !_finished && _plan != null && ConfirmInstall.IsChecked == true;
        CloseButton.IsEnabled = !busy; Activity.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Activity.IsIndeterminate = true; CancelOperationButton.Visibility = Visibility.Collapsed;
        if (message != null) StatusText.Text = message;
    }
    private void ConfirmationChanged(object sender, RoutedEventArgs e) => ApplyButton.IsEnabled = !_busy && !_finished && _plan != null && ConfirmInstall.IsChecked == true;
    private void BetaChanged(object sender, RoutedEventArgs e)
    {
        InvalidateReview();
        ReviewText.Text = "Release preference changed. Review the installation again.";
    }
    private void CancelOperation_Click(object sender, RoutedEventArgs e) { if (_previewing || _service.CanCancel) _cancellation?.Cancel(); }
    private void Adopt_Click(object sender, RoutedEventArgs e) { AdoptionRequested = true; Close(); }
    private void Removal_Click(object sender, RoutedEventArgs e) { RemovalRequested = true; Close(); }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_busy) { e.Cancel = true; StatusText.Text = "Wait for completion, or cancel preparation while that action is available."; }
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e) { _http.Dispose(); base.OnClosed(e); }
}
