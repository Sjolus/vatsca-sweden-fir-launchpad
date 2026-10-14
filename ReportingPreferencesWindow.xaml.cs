using System.Diagnostics;
using System.Windows;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

public partial class ReportingPreferencesWindow : Window
{
    public ReportingPreferencesWindow(bool firstRun = false)
    {
        InitializeComponent();
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, Height);
        MinWidth = Math.Min(MinWidth, Width);
        var preferences = ReportingService.State.Preferences;
        UsageChoice.IsChecked = !preferences.Confirmed || preferences.Usage;
        InventoryChoice.IsChecked = !preferences.Confirmed || preferences.Inventory;
        ErrorsChoice.IsChecked = !preferences.Confirmed || preferences.Errors;
        Heading.Text = firstRun ? "Help improve Launchpad" : "Privacy and support";
        DismissButton.Content = firstRun ? "Not now" : "Cancel";
        ChoiceExplanation.Text = preferences.Confirmed
            ? "Each switch is independent. Save choices applies all three. Changing your support ID or clearing unsent reports takes effect immediately."
            : "Nothing is sent until you save your choices. Each switch is independent. Uncheck any category to decline, or choose Not now to decide on a later launch.";
        RefreshIdentity();
    }

    private void RefreshIdentity()
    {
        var state = ReportingService.State;
        SupportIdText.Text = state.StorageAvailable ? state.SupportId.ToString("D") : "Unavailable";
        // Saving remains a safe retry after a transient write failure; the service refuses corrupt storage.
        SaveChoices.IsEnabled = true;
        CopyIdButton.IsEnabled = ResetIdButton.IsEnabled = ClearPendingButton.IsEnabled = state.StorageAvailable;
        PendingText.Text = $"{state.PendingCount} unsent automatic report(s). Clearing them keeps your ID and reporting choices.";
        if (state.SuppressedCount > 0) PendingText.Text += $" {state.SuppressedCount} repeated errors were combined during this session.";
        if (!state.StorageAvailable) Status.Text = "Reporting data could not be opened safely. Reporting is off; Launchpad can still be used.";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!ReportingService.TrySavePreferences(UsageChoice.IsChecked == true, InventoryChoice.IsChecked == true, ErrorsChoice.IsChecked == true))
        { Status.Text = "These choices could not be saved. Reporting remains off. Please try again."; return; }
        DialogResult = true;
    }

    private void CopyId_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(ReportingService.State.SupportId.ToString("D")); Status.Text = "Support ID copied."; }
        catch { Status.Text = "The clipboard is busy. You can select and copy the ID above."; }
    }

    private void ResetId_Click(object sender, RoutedEventArgs e)
    {
        IDisposable? lease = null;
        bool settingsHoldsGuard = Owner is SettingsWindow && MaintenanceLock.IsHeldByCurrentProcess;
        if (!settingsHoldsGuard && !MaintenanceLock.TryAcquire(out lease))
        { Status.Text = "Another Launchpad operation is running. Wait for it to finish before changing your support ID."; return; }
        using var guard = lease;
        if (MessageBox.Show(this, "Future reports will use a new support ID. Unsent reports and local report context will be cleared. " +
            "Reports already received remain under your old ID until they expire; they will not be linked to your new ID. Your reporting choices stay the same.",
            "Generate a new support ID?", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        Status.Text = ReportingService.TryRegenerateId() ? "A new support ID has been created." : "The ID could not be changed. Please try again.";
        RefreshIdentity();
    }

    private void ClearPending_Click(object sender, RoutedEventArgs e)
    {
        Status.Text = ReportingService.ClearPending() ? "Unsent automatic reports cleared. Your support ID is unchanged." : "Unsent reports could not be cleared. Please try again.";
        RefreshIdentity();
    }

    private void Privacy_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://launchpad.space.sjolus.org/privacy") { UseShellExecute = true }); }
        catch { Status.Text = "The browser could not open. Privacy details are at launchpad.space.sjolus.org/privacy."; }
    }
}
