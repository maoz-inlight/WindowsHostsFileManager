using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using HostsManager.Core;
using HostsManager.Services;

namespace HostsManager.ViewModels;

public sealed record AddEntryRequest(string Ip, IReadOnlyList<string> Hostnames, string? Comment);

public enum EntryFilter { All, Loopback, Problems }

public sealed class MainViewModel : Observable, IDisposable
{
    private readonly HostsFileWriter _writer;
    private FileSystemWatcher? _watcher;

    private string _search = "";
    private EntryFilter _filter = EntryFilter.All;
    private string _statusMessage = "";
    private bool _hasExternalChange;

    public MainViewModel(HostsFileWriter writer)
    {
        _writer = writer;

        Entries = new ObservableCollection<EntryViewModel>();
        EntriesView = CollectionViewSource.GetDefaultView(Entries);
        EntriesView.Filter = o => Matches((EntryViewModel)o);

        AddCommand = new RelayCommand(Add);
        DeleteCommand = new RelayCommand(Delete, () => SelectedEntry is { IsReadOnly: false });
        ReloadCommand = new RelayCommand(ReloadWithConfirmation);
        SaveCommand = new RelayCommand(Save, () => IsDirty);
        RevertCommand = new RelayCommand(RevertWithConfirmation, () => IsDirty);
        FlushDnsCommand = new RelayCommand(FlushDns);
        BackupsCommand = new RelayCommand(() => ShowBackups?.Invoke());
        ToggleCommand = new RelayCommand(ToggleSelected, () => SelectedEntry is { CanToggle: true });
        ShowProblemsCommand = new RelayCommand(() => Filter = EntryFilter.Problems);
        DeleteRowCommand = new RelayCommand(o => DeleteEntry(o as EntryViewModel));
    }

    // ---- host-supplied dialogs -------------------------------------------

    public Func<AddEntryRequest?>? RequestAddEntry { get; set; }
    public Action? ShowBackups { get; set; }
    public Func<string, string, bool>? Confirm { get; set; }
    public Action<string, string>? ShowError { get; set; }

    // ---- state ------------------------------------------------------------

    public ObservableCollection<EntryViewModel> Entries { get; }
    public ICollectionView EntriesView { get; }

    public HostsFileWriter Writer => _writer;

    public RelayCommand AddCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand ReloadCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand RevertCommand { get; }
    public RelayCommand FlushDnsCommand { get; }
    public RelayCommand BackupsCommand { get; }
    public RelayCommand ToggleCommand { get; }
    public RelayCommand ShowProblemsCommand { get; }

    /// <summary>
    /// Deletes a specific row directly, without requiring it to be selected first. The
    /// warning icon on an unparseable row uses this, so a flagged line is one click from
    /// being removed rather than select-then-find-the-toolbar-button.
    /// </summary>
    public RelayCommand DeleteRowCommand { get; }

    private EntryViewModel? _selectedEntry;
    public EntryViewModel? SelectedEntry
    {
        get => _selectedEntry;
        set => Set(ref _selectedEntry, value);
    }

    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) EntriesView.Refresh(); }
    }

    public EntryFilter Filter
    {
        get => _filter;
        set { if (Set(ref _filter, value)) EntriesView.Refresh(); }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    public bool HasExternalChange
    {
        get => _hasExternalChange;
        private set => Set(ref _hasExternalChange, value);
    }

    public bool IsDirty => _writer.Document?.IsDirty ?? false;

    public int PendingCount => _writer.Document?.ModifiedCount ?? 0;

    public string PendingText => PendingCount switch
    {
        0 => "No unsaved changes",
        1 => "1 unsaved change",
        _ => $"{PendingCount} unsaved changes",
    };

    public int UnparseableCount => _writer.Document?.UnparseableLines.Count() ?? 0;

    public int ShadowedCount => _writer.Document?.FindShadowedEntries().Count ?? 0;

    public bool HasProblems => UnparseableCount > 0 || ShadowedCount > 0;

    public string ProblemText
    {
        get
        {
            var parts = new List<string>();
            if (UnparseableCount > 0)
                parts.Add(UnparseableCount == 1 ? "1 unparseable line" : $"{UnparseableCount} unparseable lines");
            if (ShadowedCount > 0)
                parts.Add(ShadowedCount == 1 ? "1 duplicate domain" : $"{ShadowedCount} duplicate domains");
            return string.Join(" · ", parts);
        }
    }

    public string HostsPath => _writer.HostsPath;

    public string FormatText => _writer.Document?.Format.Describe() ?? "";

    public string CountsText
    {
        get
        {
            var doc = _writer.Document;
            if (doc is null) return "";

            var active = doc.Entries.Count(e => e.IsEnabled);
            var disabled = doc.Entries.Count(e => !e.IsEnabled);
            return $"{active} active · {disabled} disabled";
        }
    }

    public string BackupText
    {
        get
        {
            var latest = _writer.Backups.List().FirstOrDefault();
            return latest is null ? "No backups yet" : $"Last backup {latest.Timestamp:HH:mm}";
        }
    }

    // ---- operations -------------------------------------------------------

    public void Initialize()
    {
        Reload(discardChanges: true);

        if (_writer.BackupWarning is not null)
            ShowError?.Invoke("Backups unavailable", _writer.BackupWarning + "\n\nSaving is disabled until this is fixed.");

        StartWatching();
    }

    /// <summary>
    /// Reload re-reads the file and throws away anything pending. Every other
    /// destructive action here takes a backup or asks first, so this one asks too.
    /// </summary>
    private void ReloadWithConfirmation()
    {
        if (IsDirty && Confirm?.Invoke("Discard changes?",
                $"{PendingText} will be lost. Reload from the file on disk anyway?") != true) return;

        Reload(discardChanges: true);
    }

    private void RevertWithConfirmation()
    {
        if (IsDirty && Confirm?.Invoke("Revert changes?",
                $"{PendingText} will be discarded. Revert to the file on disk?") != true) return;

        Reload(discardChanges: true);
    }

    public void Reload(bool discardChanges)
    {
        if (!discardChanges && IsDirty) return;

        try
        {
            var doc = _writer.Load();

            Entries.Clear();
            var shadowed = doc.FindShadowedEntries().ToHashSet();

            foreach (var line in doc.Lines.Where(l => l.IsEntry || l.Kind == LineKind.Unparseable))
            {
                Entries.Add(new EntryViewModel(line, doc, OnDocumentChanged)
                {
                    IsShadowed = shadowed.Contains(line),
                });
            }

            HasExternalChange = false;
            StatusMessage = "";
            RaiseAll();
        }
        catch (Exception ex)
        {
            ShowError?.Invoke("Could not read the hosts file", ex.Message);
        }
    }

    private void Add()
    {
        var request = RequestAddEntry?.Invoke();
        if (request is null) return;

        var doc = _writer.Document;
        if (doc is null) return;

        try
        {
            var line = doc.AddEntry(request.Ip, request.Hostnames, request.Comment);
            var vm = new EntryViewModel(line, doc, OnDocumentChanged);

            var index = doc.Lines.Take(doc.Lines.ToList().IndexOf(line)).Count(l => l.IsEntry || l.Kind == LineKind.Unparseable);
            Entries.Insert(Math.Clamp(index, 0, Entries.Count), vm);

            SelectedEntry = vm;
            OnDocumentChanged();
        }
        catch (ArgumentException ex)
        {
            ShowError?.Invoke("Could not add that entry", ex.Message);
        }
    }

    private void Delete() => DeleteEntry(SelectedEntry);

    private void DeleteEntry(EntryViewModel? entry)
    {
        var doc = _writer.Document;
        if (entry is null || doc is null || entry.IsReadOnly) return;

        var what = entry.IsUnparseable
            ? $"Delete the unparseable text on line {entry.Line.LineNumber}?"
            : $"Delete {entry.Domain}?";

        if (Confirm?.Invoke("Delete entry", what + "\n\nA backup is taken before the file is saved.") != true) return;

        doc.Remove(entry.Line);
        Entries.Remove(entry);
        OnDocumentChanged();
    }

    private void ToggleSelected()
    {
        if (SelectedEntry is { CanToggle: true } entry) entry.IsEnabled = !entry.IsEnabled;
    }

    /// <summary>
    /// Saves and rethrows on failure, for callers that need to react — the tray menu
    /// reverts its toggle rather than leaving the menu disagreeing with the file.
    /// </summary>
    public void SaveFromTray(string reason)
    {
        _writer.Save(reason);
        System.Windows.Application.Current?.Dispatcher.Invoke(RefreshRows);
    }

    private void Save()
    {
        try
        {
            var reason = PendingCount == 1 ? "1 change" : $"{PendingCount} changes";
            var result = _writer.Save(reason);

            // Keep the in-memory document rather than reloading: re-parsing would drop
            // each line's captured disable prefix and silently reformat untouched lines.
            // It also preserves selection and scroll position.
            RefreshRows();
            StatusMessage = result.Message;
        }
        catch (HostsDriftException ex)
        {
            HasExternalChange = true;
            ShowError?.Invoke("The file changed on disk", ex.Message);
        }
        catch (HostsVerificationException ex)
        {
            ShowError?.Invoke("Save cancelled by the safety check",
                "The file was not modified.\n\n" + ex.Message);
        }
        catch (Exception ex)
        {
            ShowError?.Invoke("Could not save the hosts file", ex.Message);
        }
    }

    private void FlushDns()
    {
        var (success, message) = DnsFlusher.Flush();
        StatusMessage = message;
        if (!success) ShowError?.Invoke("Could not flush DNS", message);
    }

    private void OnDocumentChanged()
    {
        RaiseAll();
        EntriesView.Refresh();
    }

    /// <summary>Re-reads row state from the model without rebuilding the list.</summary>
    public void RefreshRows()
    {
        var shadowed = _writer.Document?.FindShadowedEntries().ToHashSet() ?? new HashSet<HostsLine>();

        foreach (var entry in Entries)
        {
            entry.IsShadowed = shadowed.Contains(entry.Line);
            entry.Refresh();
        }

        HasExternalChange = false;
        RaiseAll();
        EntriesView.Refresh();
    }

    public void RaiseAll()
    {
        Raise(nameof(IsDirty));
        Raise(nameof(PendingCount));
        Raise(nameof(PendingText));
        Raise(nameof(UnparseableCount));
        Raise(nameof(ShadowedCount));
        Raise(nameof(HasProblems));
        Raise(nameof(ProblemText));
        Raise(nameof(FormatText));
        Raise(nameof(CountsText));
        Raise(nameof(BackupText));
    }

    private bool Matches(EntryViewModel entry)
    {
        if (_filter == EntryFilter.Loopback && !IsLoopback(entry.Line.Ip)) return false;
        if (_filter == EntryFilter.Problems && !entry.IsUnparseable && !entry.IsShadowed) return false;

        if (string.IsNullOrWhiteSpace(_search)) return true;

        return entry.Domain.Contains(_search, StringComparison.OrdinalIgnoreCase)
            || entry.MapsTo.Contains(_search, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Covers ::1 and the whole 127.0.0.0/8 range, not just the literal 127.0.0.1.</summary>
    private static bool IsLoopback(string? ip) =>
        System.Net.IPAddress.TryParse(ip, out var parsed) && System.Net.IPAddress.IsLoopback(parsed);

    // ---- external change detection ---------------------------------------

    private void StartWatching()
    {
        var directory = Path.GetDirectoryName(_writer.HostsPath);
        if (directory is null || !Directory.Exists(directory)) return;

        try
        {
            _watcher = new FileSystemWatcher(directory, Path.GetFileName(_writer.HostsPath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };

            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Renamed += OnFileChanged;
        }
        catch (Exception)
        {
            // Watching is a convenience; the drift check at save time is the real guard.
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Our own atomic replace fires this too, so compare hashes rather than trust the event.
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (_writer.HasExternalChange()) HasExternalChange = true;
            }
            catch (IOException)
            {
            }
        });
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}
