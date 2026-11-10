using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using VatscaUpdateChecker.Models;
using VatscaUpdateChecker.Services;

namespace VatscaUpdateChecker;

/// <summary>Edits the named EuroScope launch sets. It does not start, inspect or close processes.</summary>
public partial class EuroScopeLaunchSetWindow : Window
{
    public ObservableCollection<ProfileOption> Profiles { get; } = [];
    private readonly ObservableCollection<LaunchSetItem> _sets = [];
    private readonly ProfileOption? _defaultProfile;

    public IReadOnlyList<EuroScopeLaunchSetDefinition> Sets { get; private set; } = [];
    public Guid? ActiveSetId { get; private set; }

    public EuroScopeLaunchSetWindow(IReadOnlyList<ProfileOption> profiles, IReadOnlyList<EuroScopeLaunchSetDefinition> sets,
        Guid? activeSetId, ProfileOption? defaultProfile)
    {
        foreach (var profile in profiles) Profiles.Add(profile);
        InitializeComponent();

        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        MinHeight = Math.Min(MinHeight, Height); MinWidth = Math.Min(MinWidth, Width);

        _defaultProfile = defaultProfile?.FilePath != null ? defaultProfile : Profiles.FirstOrDefault(profile => profile.FilePath != null);
        foreach (var set in sets)
        {
            var item = NewItem(set.Id, set.Name, set.Id == activeSetId);
            foreach (var entry in EuroScopeLaunchPlanner.Normalize(set.Entries))
                item.Rows.Add(new LaunchSetRow(entry.Id, entry.Enabled, ProfileFor(entry.ProfilePath), entry.DelaySeconds.ToString()));
            _sets.Add(item);
        }

        SetList.ItemsSource = _sets;
        _sets.CollectionChanged += (_, _) => RefreshSetButtons();
        SetList.SelectedItem = _sets.FirstOrDefault(set => set.IsActive) ?? _sets.FirstOrDefault();
        ShowSelected();
    }

    private LaunchSetItem? Selected => SetList.SelectedItem as LaunchSetItem;

    private LaunchSetItem NewItem(Guid id, string name, bool active)
    {
        var item = new LaunchSetItem(id, name, active);
        item.ActiveChanged += ActiveChanged;
        item.Rows.CollectionChanged += (_, _) => { item.Renumber(); if (ReferenceEquals(item, Selected)) RefreshRowState(); };
        return item;
    }

    // Only one set can be the Launch target.
    private void ActiveChanged(LaunchSetItem changed)
    {
        if (!changed.IsActive) return;
        foreach (var other in _sets.Where(set => !ReferenceEquals(set, changed))) other.IsActive = false;
    }

    private ProfileOption ProfileFor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Profiles.FirstOrDefault(profile => profile.FilePath == null) ?? AddProfile(new("— No profile —", null));

        var match = Profiles.FirstOrDefault(profile => string.Equals(profile.FilePath, path, StringComparison.OrdinalIgnoreCase));
        // Keep a saved entry visible even when its profile has moved or the data folder changed.
        return match ?? AddProfile(new(Path.GetFileNameWithoutExtension(path) + " (not in the data folder)", path));
    }

    private ProfileOption AddProfile(ProfileOption profile) { Profiles.Add(profile); return profile; }

    private void SetList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowSelected();

    private void ShowSelected()
    {
        var selected = Selected;
        SetEditor.DataContext = selected;
        SetEditor.Visibility = selected is null ? Visibility.Collapsed : Visibility.Visible;
        NoSetText.Visibility = selected is null ? Visibility.Visible : Visibility.Collapsed;

        RefreshRowState();
        RefreshSetButtons();
    }

    private void RefreshRowState()
    {
        var selected = Selected;
        EmptyText.Visibility = selected is { Rows.Count: 0 } ? Visibility.Visible : Visibility.Collapsed;
        AddButton.IsEnabled = selected is not null && selected.Rows.Count < EuroScopeLaunchEntry.MaxEntries;
    }

    private void RefreshSetButtons()
    {
        NewSetButton.IsEnabled = _sets.Count < EuroScopeLaunchSetDefinition.MaxSets;
        DuplicateSetButton.IsEnabled = Selected is not null && _sets.Count < EuroScopeLaunchSetDefinition.MaxSets;
        DeleteSetButton.IsEnabled = Selected is not null;
    }

    private string UniqueName(string baseName)
    {
        var names = _sets.Select(set => set.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName)) return baseName;
        for (int i = 2; ; i++) if (!names.Contains($"{baseName} {i}")) return $"{baseName} {i}";
    }

    private void NewSet_Click(object sender, RoutedEventArgs e)
    {
        if (_sets.Count >= EuroScopeLaunchSetDefinition.MaxSets) return;

        var item = NewItem(Guid.NewGuid(), UniqueName("Launch set " + (_sets.Count + 1)), _sets.Count == 0);
        _sets.Add(item);
        SetList.SelectedItem = item;

        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void DuplicateSet_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } source || _sets.Count >= EuroScopeLaunchSetDefinition.MaxSets) return;

        var item = NewItem(Guid.NewGuid(), UniqueName(source.Name.Trim() + " copy"), false);
        // New entry IDs: running instances stay tied to the set that started them.
        foreach (var row in source.Rows) item.Rows.Add(new LaunchSetRow(Guid.NewGuid(), row.Enabled, row.Profile, row.DelayText));

        _sets.Insert(_sets.IndexOf(source) + 1, item);
        SetList.SelectedItem = item;

        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void DeleteSet_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } set) return;
        if (set.Rows.Count > 0 && MessageBox.Show(this, $"Delete {set.Name}? Running EuroScope instances are not closed.",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        int index = _sets.IndexOf(set);
        _sets.Remove(set);
        SetList.SelectedItem = _sets.Count == 0 ? null : _sets[Math.Min(index, _sets.Count - 1)];
        ShowSelected();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } set || set.Rows.Count >= EuroScopeLaunchEntry.MaxEntries) return;

        var used = set.Rows.Select(row => row.Profile?.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var next = Profiles.FirstOrDefault(profile => profile.FilePath != null && !used.Contains(profile.FilePath)) ?? _defaultProfile;

        // 5 s default. The first instance ignores its delay, but keeps it if it is moved down.
        set.Rows.Add(new LaunchSetRow(Guid.NewGuid(), true, next, "5"));
    }

    private static LaunchSetRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as LaunchSetRow;

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } set && RowOf(sender) is { } row && set.Rows.IndexOf(row) is var index and > 0) set.Rows.Move(index, index - 1);
    }

    private void Down_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } set && RowOf(sender) is { } row && set.Rows.IndexOf(row) is var index && index >= 0 && index < set.Rows.Count - 1)
            set.Rows.Move(index, index + 1);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } set && RowOf(sender) is { } row) set.Rows.Remove(row);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var problems = new List<string>();
        var duplicates = new List<string>();
        var sets = new List<EuroScopeLaunchSetDefinition>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Guid? active = null;

        foreach (var set in _sets)
        {
            var name = set.Name.Trim();
            var label = name.Length == 0 ? "A set" : name;
            if (name.Length == 0) problems.Add("Give every launch set a name.");
            else if (!names.Add(name)) problems.Add($"{name}: another set has the same name.");

            var entries = new List<EuroScopeLaunchEntry>();
            foreach (var row in set.Rows)
            {
                if (!int.TryParse(row.DelayText?.Trim(), out var delay) || delay < 0 || delay > EuroScopeLaunchEntry.MaxDelaySeconds)
                {
                    problems.Add($"{label}, instance {row.OrderText}: enter a delay from 0 to {EuroScopeLaunchEntry.MaxDelaySeconds} seconds.");
                    continue;
                }
                entries.Add(new(row.Id, row.Profile?.FilePath ?? string.Empty, delay, row.Enabled));
            }

            if (set.IsActive)
            {
                if (!entries.Any(entry => entry.Enabled)) problems.Add($"{label}: enable at least one instance, or stop using it for Launch.");
                active = set.Id;
            }

            duplicates.AddRange(entries.Where(entry => entry.Enabled && entry.ProfilePath.Length > 0)
                .GroupBy(entry => entry.ProfilePath, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1)
                .Select(group => $"{EuroScopeLaunchPlanner.ProfileName(group.Key)} in {label}"));
            sets.Add(new(set.Id, name, entries));
        }

        if (problems.Count > 0)
        {
            ValidationText.Text = string.Join("\n", problems.Distinct());
            ValidationText.Visibility = Visibility.Visible;
            return;
        }

        if (duplicates.Count > 0 && MessageBox.Show(this,
                $"{string.Join(", ", duplicates)} is used by more than one instance. Those instances write the same session and settings files.\n\nSave anyway?",
                Title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        Sets = sets;
        ActiveSetId = active;
        DialogResult = true;
    }

    public sealed class LaunchSetItem : INotifyPropertyChanged
    {
        public Guid Id { get; }
        public ObservableCollection<LaunchSetRow> Rows { get; } = [];
        private string _name;
        private bool _isActive;

        public event Action<LaunchSetItem>? ActiveChanged;

        public LaunchSetItem(Guid id, string name, bool active)
        {
            Id = id; _name = name; _isActive = active;
            Rows.CollectionChanged += (_, _) => OnChanged(nameof(ListDetail));
        }

        public string Name
        {
            get => _name;
            set { if (_name == value) return; _name = value ?? string.Empty; OnChanged(nameof(Name)); OnChanged(nameof(ListLabel)); }
        }

        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive == value) return;
                _isActive = value;
                OnChanged(nameof(IsActive)); OnChanged(nameof(ListDetail));
                ActiveChanged?.Invoke(this);
            }
        }

        public string ListLabel => string.IsNullOrWhiteSpace(Name) ? "(unnamed)" : Name;
        public string ListDetail => $"{Rows.Count} instance{(Rows.Count == 1 ? "" : "s")}" + (IsActive ? " · used by Launch" : "");

        public void Renumber()
        {
            for (int i = 0; i < Rows.Count; i++) Rows[i].Position = i;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new(name));
    }

    public sealed class LaunchSetRow : INotifyPropertyChanged
    {
        public Guid Id { get; }
        private bool _enabled;
        private ProfileOption? _profile;
        private string _delayText;
        private int _position = -1;

        public LaunchSetRow(Guid id, bool enabled, ProfileOption? profile, string delayText)
        {
            Id = id; _enabled = enabled; _profile = profile; _delayText = delayText;
        }

        public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
        public ProfileOption? Profile { get => _profile; set => Set(ref _profile, value); }
        public string DelayText { get => _delayText; set => Set(ref _delayText, value); }

        public int Position
        {
            get => _position;
            set { Set(ref _position, value); OnChanged(nameof(OrderText)); OnChanged(nameof(DelayApplies)); }
        }

        public string OrderText => (Position + 1).ToString();
        // The first row starts immediately; later rows wait their delay.
        public bool DelayApplies => Position > 0;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new(name));

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            OnChanged(name!);
        }
    }
}
