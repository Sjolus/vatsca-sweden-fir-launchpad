using System.ComponentModel;
using System.Windows;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

public partial class BulkApplicationActionWindow : Window
{
    private readonly ApplicationBatchAction _action;
    public IReadOnlyList<ApplicationActionChoice> Choices { get; }

    public BulkApplicationActionWindow(ApplicationBatchAction action, IReadOnlyList<ApplicationActionChoice> choices)
    {
        InitializeComponent();
        _action = action; Choices = choices;
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, Height); MinWidth = Math.Min(MinWidth, Width);
        Title = Heading.Text = action == ApplicationBatchAction.Start ? "Start your applications" : "Close your applications";
        Explanation.Text = action == ApplicationBatchAction.Start
            ? "Choose which applications to start. Already running applications stay open. Nothing is installed or updated."
            : "Choose which application windows to close. Active network connections may end. Applications can ask you to save or confirm; Launchpad will not force them to stop.";
        ApplicationChoices.ItemsSource = choices;
        foreach (var choice in choices) choice.PropertyChanged += ChoiceChanged;
        Closed += (_, _) => { foreach (var choice in choices) choice.PropertyChanged -= ChoiceChanged; };
        RefreshSelection();
    }

    private void ChoiceChanged(object? sender, PropertyChangedEventArgs e) => RefreshSelection();
    private void RefreshSelection()
    {
        int selected = Choices.Count(choice => choice.CanSelect && choice.IsSelected);
        SelectionSummary.Text = selected == 0 ? "Select an available application to continue." :
            $"{selected} application{(selected == 1 ? "" : "s")} selected. You can uncheck any application above.";
        ApplyButton.Content = _action == ApplicationBatchAction.Start ? "Start selected" : "Close selected";
        ApplyButton.IsEnabled = selected > 0;
    }
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (Choices.Any(choice => choice.CanSelect && choice.IsSelected)) DialogResult = true;
    }
}
