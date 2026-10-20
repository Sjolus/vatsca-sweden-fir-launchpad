using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class ReportProblemWindow : Window
{
    private sealed record ApplicationOption(ReportingApplication Value, string Label) { public override string ToString() => Label; }
    private sealed record CategoryOption(ReportingCategory Value, string Label) { public override string ToString() => Label; }
    private PreparedReport? _draft;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _ready, _sending, _closed, _sent;
    private string? _reference;

    public ReportProblemWindow(ReportingApplication application = ReportingApplication.Launchpad, ReportingCategory category = ReportingCategory.Other)
    {
        InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, Height); MinWidth = Math.Min(MinWidth, Width);
        ApplicationChoice.ItemsSource = Enum.GetValues<ReportingApplication>().Select(a => new ApplicationOption(a, a switch
        {
            ReportingApplication.Euroscope => "EuroScope", ReportingApplication.Gng => "Swedish GNG package",
            ReportingApplication.Trackaudio => "TrackAudio", ReportingApplication.Vacs => "VACS",
            ReportingApplication.Vatis => "vATIS", ReportingApplication.Vatefs => "VatEFS", ReportingApplication.Vatiris => "VATIRIS",
            ReportingApplication.Saaman => "SAAMAN",
            ReportingApplication.Maintenance => "Removal and recovery", _ => "Launchpad"
        })).ToArray();
        CategoryChoice.ItemsSource = new[] {
            new CategoryOption(ReportingCategory.Setup, "Getting started / setup"),
            new CategoryOption(ReportingCategory.Installation, "Installing an application"),
            new CategoryOption(ReportingCategory.Update, "Updating an application"),
            new CategoryOption(ReportingCategory.Gng, "Swedish GNG package"),
            new CategoryOption(ReportingCategory.Launch, "Opening an application"),
            new CategoryOption(ReportingCategory.Appearance, "Layout or appearance"),
            new CategoryOption(ReportingCategory.Other, "Something else") };
        ApplicationChoice.SelectedItem = ApplicationChoice.Items.Cast<ApplicationOption>().First(a => a.Value == application);
        CategoryChoice.SelectedItem = CategoryChoice.Items.Cast<CategoryOption>().First(c => c.Value == category);
        _ready = true;
        RefreshPreview();
        Closed += (_, _) => { _closed = true; _lifetime.Cancel(); };
    }

    private void Choice_Changed(object sender, SelectionChangedEventArgs e) => RefreshPreview();
    private void Context_Changed(object sender, RoutedEventArgs e) => RefreshPreview();
    private void RefreshPreview()
    {
        if (!_ready || _sending) return;
        _sent = false; _reference = null; CopyReferenceButton.Visibility = Visibility.Collapsed;
        ReferenceText.Visibility = Visibility.Collapsed;
        try
        {
            _draft = ReportingService.PrepareReport(((ApplicationOption)ApplicationChoice.SelectedItem).Value,
                ((CategoryOption)CategoryChoice.SelectedItem).Value, IncludeContext.IsChecked == true);
            Preview.Text = _draft.Json;
            Status.Text = "Nothing has been sent. Send submits only the report shown above.";
        }
        catch
        { _draft = null; Preview.Text = ""; Status.Text = "A report could not be prepared. Check Privacy and support in App settings."; }
        UpdateControls();
    }

    private void UpdateControls()
    {
        SendButton.IsEnabled = !_sending && !_sent && _draft is not null;
        SaveReportButton.IsEnabled = !_sending && _draft is not null;
        ApplicationChoice.IsEnabled = CategoryChoice.IsEnabled = IncludeContext.IsEnabled = !_sending;
        SendButton.Content = _sending ? "Sending…" : "Send report";
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_sending || _sent || _draft is null) return;
        _sending = true; UpdateControls(); Status.Text = "Sending your reviewed report…";
        try
        {
            var result = await ReportingService.SendReportAsync(_draft, _lifetime.Token);
            if (_closed) return;
            _sent = result.Succeeded; _reference = result.Reference;
            Status.Text = result.Succeeded ? result.Message + " Reference: " + result.Reference : result.Message;
            CopyReferenceButton.Visibility = result.Succeeded ? Visibility.Visible : Visibility.Collapsed;
            ReferenceText.Text = result.Reference ?? "";
            ReferenceText.Visibility = result.Succeeded ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) { if (!_closed) Status.Text = "Sending stopped. No receipt was confirmed; retrying the same report will not create a duplicate."; }
        catch { if (!_closed) Status.Text = "No receipt was confirmed. Retry or save the report locally."; }
        finally { _sending = false; if (!_closed) UpdateControls(); }
    }

    private void SaveLocal_Click(object sender, RoutedEventArgs e)
    {
        if (_draft is null || _sending) return;
        var picker = new SaveFileDialog { Title = "Save reviewed problem report", FileName = "Launchpad-report.json", Filter = "JSON report (*.json)|*.json" };
        if (picker.ShowDialog(this) != true) return;
        try { File.WriteAllText(picker.FileName, _draft.Json); Status.Text = "Report saved locally. Saving does not send it."; }
        catch { Status.Text = "The report could not be saved there. Choose another location."; }
    }

    private void CopyReference_Click(object sender, RoutedEventArgs e)
    {
        if (_reference is null) return;
        try { Clipboard.SetText(_reference); Status.Text = "Report reference copied."; }
        catch { Status.Text = "The clipboard is busy. You can select and copy the reference above."; }
    }
}
