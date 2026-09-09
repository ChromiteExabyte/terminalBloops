using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TerminalBloops.Core;

namespace TerminalBloops.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private string _search = "";
    private bool _briefOnly = true;
    private string _evidenceFilter = "All";
    private string _statusText = "Recorder is starting…";
    private bool _notificationsEnabled = true;
    private bool _startAtLogin = true;
    private bool _selectedLauncherMuted;
    private bool _loadingSettings;
    private ProcessRecord? _selected;
    private HashSet<string> _mutedLaunchers = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<ProcessRecord> Items { get; } = [];
    public ObservableCollection<CaptureNotice> Notices { get; } = [];
    public ObservableCollection<ProcessRecord> Ancestors { get; } = [];
    public IReadOnlyList<string> EvidenceFilters { get; } = ["All", "Observed", "Unconfirmed", "Unresolved"];

    public MainViewModel() => Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(EventCount));

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? FilterChanged;
    public event Action<ProcessRecord?>? SelectionChanged;
    public event Action<bool>? NotificationsChanged;
    public event Action<bool>? StartAtLoginChanged;
    public event Action<ProcessRecord, bool>? LauncherMuteChanged;
    public event Action? SetupRequested;

    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) FilterChanged?.Invoke(); }
    }
    public bool BriefOnly
    {
        get => _briefOnly;
        set { if (Set(ref _briefOnly, value)) FilterChanged?.Invoke(); }
    }
    public string EvidenceFilter
    {
        get => _evidenceFilter;
        set { if (Set(ref _evidenceFilter, value)) FilterChanged?.Invoke(); }
    }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public bool NotificationsEnabled
    {
        get => _notificationsEnabled;
        set { if (Set(ref _notificationsEnabled, value) && !_loadingSettings) NotificationsChanged?.Invoke(value); }
    }
    public bool StartAtLogin
    {
        get => _startAtLogin;
        set { if (Set(ref _startAtLogin, value) && !_loadingSettings) StartAtLoginChanged?.Invoke(value); }
    }
    public bool SelectedLauncherMuted
    {
        get => _selectedLauncherMuted;
        set
        {
            if (!Set(ref _selectedLauncherMuted, value) || _loadingSettings || Selected is not { } selected || !CanMuteLauncher) return;
            var key = NotificationPolicy.LauncherKey(selected);
            if (value) _mutedLaunchers.Add(key); else _mutedLaunchers.Remove(key);
            LauncherMuteChanged?.Invoke(selected, value);
        }
    }
    public ProcessRecord? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            UpdateSelectedSettings();
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(CanMuteLauncher));
            SelectionChanged?.Invoke(value);
        }
    }
    public bool HasSelection => Selected is not null;
    public bool CanMuteLauncher => !string.IsNullOrWhiteSpace(Selected?.ParentImage);
    public string EventCount => Items.Count == 1 ? "1 event in this view" : $"{Items.Count:N0} events in this view";

    public void SetSettings(AppSettings settings)
    {
        _loadingSettings = true;
        try
        {
            NotificationsEnabled = settings.NotificationsEnabled;
            StartAtLogin = settings.StartAtLogin;
            _mutedLaunchers = new(settings.MutedLaunchers, StringComparer.OrdinalIgnoreCase);
            UpdateSelectedSettings();
        }
        finally { _loadingSettings = false; }
    }
    public void SetStatus(string text) => StatusText = text;
    public void RequestSetup() => SetupRequested?.Invoke();
    public void ReplaceItems(IEnumerable<ProcessRecord> records)
    {
        var selectedId = Selected?.Id;
        var incoming = records.ToArray();
        // Preserve containers and scroll position when capture adds or completes an event.
        for (var i = 0; i < incoming.Length; i++)
        {
            var record = incoming[i];
            if (i >= Items.Count) { Items.Add(record); continue; }
            if (Items[i].Id != record.Id)
            {
                var previousIndex = -1;
                for (var j = i + 1; j < Items.Count; j++)
                    if (Items[j].Id == record.Id) { previousIndex = j; break; }
                if (previousIndex >= 0) Items.Move(previousIndex, i); else Items.Insert(i, record);
            }
            if (Items[i] != record) Items[i] = record;
        }
        while (Items.Count > incoming.Length) Items.RemoveAt(Items.Count - 1);
        Selected = selectedId is null ? null : Items.FirstOrDefault(item => item.Id == selectedId);
    }
    public void ReplaceAncestors(IEnumerable<ProcessRecord> records)
    {
        Ancestors.Clear();
        foreach (var record in records) Ancestors.Add(record);
    }
    private void UpdateSelectedSettings()
    {
        _selectedLauncherMuted = Selected is { } selected && _mutedLaunchers.Contains(NotificationPolicy.LauncherKey(selected));
        OnPropertyChanged(nameof(SelectedLauncherMuted));
    }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(property);
        return true;
    }
    private void OnPropertyChanged([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}
