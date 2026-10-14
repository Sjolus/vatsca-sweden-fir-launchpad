using System.Windows;
using System.Windows.Controls;

namespace VatscaUpdateChecker;

// Presentation-only production XAML. No reporting service, storage, HTTP or clipboard actions.
public partial class ReportingPreferencesWindow : Window
{
    public ReportingPreferencesWindow()
    {
        InitializeComponent();
        UiFixture.Initialize(this);
        Heading.Text = UiFixture.Blank ? "Help improve Launchpad" : "Privacy and support";
        UsageChoice.IsChecked = InventoryChoice.IsChecked = ErrorsChoice.IsChecked = UiFixture.Blank;
        DetailedPluginsChoice.IsChecked = UiFixture.Blank;
        DetailedPluginsChoice.IsEnabled = InventoryChoice.IsChecked == true;
        SupportIdText.Text = "90b0d09c-3ef9-4d13-8838-ddbe38e25c24";
        ChoiceExplanation.Text = "Nothing is sent until you save your choices. Uncheck any category or DLL details to decline, or choose Not now to decide on a later launch. DLL details require version sharing.";
        PendingText.Text = "20 unsent automatic reports. Clearing them keeps your ID and reporting choices.";
        Status.Text = UiFixture.Blank ? "" : "Reporting data could not be saved. Reporting remains off; Launchpad can still be used.";
    }
    private void Save_Click(object sender, RoutedEventArgs e) { }
    private void InventoryChoice_Changed(object sender, RoutedEventArgs e)
    { if (DetailedPluginsChoice is not null) DetailedPluginsChoice.IsEnabled = InventoryChoice.IsChecked == true; }
    private void CopyId_Click(object sender, RoutedEventArgs e) { }
    private void ResetId_Click(object sender, RoutedEventArgs e) { }
    private void ClearPending_Click(object sender, RoutedEventArgs e) { }
    private void Privacy_Click(object sender, RoutedEventArgs e) { }
    private void History_Click(object sender, RoutedEventArgs e) { }
}

public partial class ReportProblemWindow : Window
{
    private sealed record Choice(string Label) { public override string ToString() => Label; }
    public ReportProblemWindow(bool received = false)
    {
        InitializeComponent();
        UiFixture.Initialize(this);
        ApplicationChoice.ItemsSource = new[] { new Choice("Swedish GNG package") };
        CategoryChoice.ItemsSource = new[] { new Choice("Installing an application") };
        ApplicationChoice.SelectedIndex = CategoryChoice.SelectedIndex = 0;
        Preview.Text = "{\n  \"schema_version\": 1,\n  \"support_id\": \"90b0d09c-3ef9-4d13-8838-ddbe38e25c24\",\n  \"application\": \"gng\",\n  \"category\": \"installation\"\n}";
        Status.Text = UiFixture.Blank ? "Nothing has been sent. Send submits only the report shown above."
            : "No receipt was confirmed. Retry or save the report locally. This simulated response performs no network request.";
        if (received)
        {
            Status.Text = "Report received. Share this reference with the maintainer if you want to discuss the problem.";
            ReferenceText.Text = "LP-0123456789abcdef01234567";
            ReferenceText.Visibility = CopyReferenceButton.Visibility = Visibility.Visible;
            SendButton.IsEnabled = false;
        }
    }
    private void Choice_Changed(object sender, SelectionChangedEventArgs e) { }
    private void Context_Changed(object sender, RoutedEventArgs e) { }
    private void CopyReference_Click(object sender, RoutedEventArgs e) { }
    private void SaveLocal_Click(object sender, RoutedEventArgs e) { }
    private void Send_Click(object sender, RoutedEventArgs e) { }
}
