using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using VatscaUpdateChecker.Models;

namespace VatscaUpdateChecker;

internal static partial class LayoutMatrix
{
    private static int CheckLaunchSetEditors(double scale, bool minimum)
    {
        int cases = 0;
        foreach (var state in Enum.GetValues<LaunchSetFixtureState>())
        {
            var editor = UiFixture.CreateLaunchSetEditor(state, minimum);
            try { CheckLaunchSetEditor(editor, state); cases++; }
            catch (Exception error) { throw new InvalidDataException($"Launch sets {state}, {scale:P0}, {(minimum ? "minimum" : "default")}: {error.Message}", error); }
            finally { editor.Close(); }
        }
        return cases;
    }

    private static void CheckLaunchSetEditor(EuroScopeLaunchSetWindow window, LaunchSetFixtureState state)
    {
        LayoutValidation.CheckWindowContent(window);

        var content = (FrameworkElement)window.Content;
        var buttons = LayoutValidation.Descendants(content).OfType<Button>().ToArray();
        var save = buttons.Single(button => button.IsDefault);
        var cancel = buttons.Single(button => button.IsCancel);
        Require(Equals(save.Content, "Save") && !save.IsCancel, "The editor's default button must be Save.");

        var newSet = Required<Button>(window, "NewSetButton");
        var duplicate = Required<Button>(window, "DuplicateSetButton");
        var delete = Required<Button>(window, "DeleteSetButton");
        foreach (var button in new[] { save, cancel, newSet, duplicate, delete })
        {
            CheckControlBounds(button, content);
            CheckButtonCaption(button);
            Require(button.Focusable && button.IsTabStop, (button.Content ?? button.Name) + " must be keyboard reachable.");
        }

        var list = Required<ListBox>(window, "SetList");
        var editor = Required<DockPanel>(window, "SetEditor");
        var noSet = Required<TextBlock>(window, "NoSetText");
        Require(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(list)), "The set list needs an accessible name.");

        if (state == LaunchSetFixtureState.NoSets)
        {
            Require(list.Items.Count == 0 && IsPresented(noSet) && !IsPresented(editor), "An empty editor must explain how to create a set.");
            Require(newSet.IsEnabled && !duplicate.IsEnabled && !delete.IsEnabled, "Without a selected set only New set may be enabled.");
            Require(window.Sets.Count == 0 && window.ActiveSetId is null, "Opening the editor must not produce saved sets.");
            Require(!window.IsVisible, "Launch-set layout tests must never show native windows.");
            return;
        }

        Require(list.Items.Count == 3, "The fixture's three sets must be listed.");
        var active = (EuroScopeLaunchSetWindow.LaunchSetItem)list.SelectedItem;
        Require(active.Id == UiFixture.LaunchSetActiveId && active.IsActive && active.ListDetail.Contains("used by Launch", StringComparison.Ordinal),
            "The Launch target must open selected and marked.");
        Require(IsPresented(editor) && !IsPresented(noSet) && duplicate.IsEnabled && delete.IsEnabled, "A selected set must show its editor.");

        var name = Required<TextBox>(window, "NameBox");
        CheckControlBounds(name, content);
        Require(name.Text == active.Name && name.MaxLength == EuroScopeLaunchSetDefinition.MaxNameLength, "The set name field must bind to the selected set.");

        var entries = Required<ItemsControl>(window, "EntryList");
        Require(entries.Items.Count == 3 && !IsPresented(Required<TextBlock>(window, "EmptyText")), "Every saved instance must be listed.");
        var add = Required<Button>(window, "AddButton");

        foreach (var row in entries.Items.Cast<EuroScopeLaunchSetWindow.LaunchSetRow>())
        {
            // Template parts (such as the ComboBox toggle) share the row context; check the declared controls.
            var controls = LayoutValidation.Descendants(entries).OfType<Control>().Where(control => ReferenceEquals(control.DataContext, row) &&
                BindingOperations.GetBinding(control, AutomationProperties.NameProperty) is not null).ToArray();
            Require(controls.Length == 6, $"Instance {row.OrderText} must expose its switch, profile, delay and three move/remove buttons.");

            var delay = controls.OfType<TextBox>().Single();
            var profile = controls.OfType<ComboBox>().Single();

            delay.BringIntoView(); DrainBindings(); LayoutValidation.CheckWindowContent(window);
            foreach (var control in controls.Where(control => control is ButtonBase or ComboBox or TextBox))
            {
                CheckControlBounds(control, content);
                var accessible = AutomationProperties.GetName(control);
                Require(accessible.Contains(row.OrderText, StringComparison.Ordinal), $"Instance {row.OrderText} control lacks a numbered accessible name: '{accessible}'.");
            }

            Require(delay.IsEnabled == (row.Position > 0), "Only later instances may edit their start delay.");
            Require(profile.SelectedItem is ProfileOption, $"Instance {row.OrderText} must show its saved profile.");
        }

        var moved = (ProfileOption)LayoutValidation.Descendants(entries).OfType<ComboBox>().Last().SelectedItem;
        Require(moved.DisplayName.Contains("not in the data folder", StringComparison.Ordinal), "A moved profile must stay visible and be explained.");

        add.BringIntoView(); DrainBindings(); LayoutValidation.CheckWindowContent(window);
        CheckControlBounds(add, content);
        CheckButtonCaption(add);
        Require(add.IsEnabled, "A set below the instance limit must allow adding.");

        list.SelectedItem = list.Items.Cast<EuroScopeLaunchSetWindow.LaunchSetItem>().Single(set => set.Id == UiFixture.LaunchSetEmptyId);
        DrainBindings(); LayoutValidation.CheckWindowContent(window);
        Require(entries.Items.Count == 0 && IsPresented(Required<TextBlock>(window, "EmptyText")), "An empty set must explain that it has no instances.");

        // Validation only: a successful Save sets DialogResult, which needs a shown dialog.
        list.SelectedItem = active;
        DrainBindings();

        active.Rows[1].DelayText = "soon";
        save.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        DrainBindings(); LayoutValidation.CheckWindowContent(window);

        var validation = Required<TextBlock>(window, "ValidationText");
        Require(IsPresented(validation) && validation.Text.Contains("instance 2", StringComparison.Ordinal), "An invalid delay must be explained beside Save.");
        CheckControlBounds(validation, content);
        CheckControlBounds(save, content);
        Require(window.Sets.Count == 0, "Invalid input must not produce saved sets.");
        Require(!window.IsVisible, "Launch-set layout tests must never show native windows.");
    }
}
