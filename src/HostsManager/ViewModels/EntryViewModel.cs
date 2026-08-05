using HostsManager.Core;

namespace HostsManager.ViewModels;

public enum RowState { Normal, Disabled, Pending, Invalid, Managed }

/// <summary>One row in the grid. Wraps a <see cref="HostsLine"/> for display and toggling.</summary>
public sealed class EntryViewModel : Observable
{
    private readonly HostsDocument _document;
    private readonly Action _onChanged;

    public EntryViewModel(HostsLine line, HostsDocument document, Action onChanged)
    {
        Line = line;
        _document = document;
        _onChanged = onChanged;
    }

    public HostsLine Line { get; }

    public bool IsUnparseable => Line.Kind == LineKind.Unparseable;

    public bool IsReadOnly => Line.IsReadOnly;

    public bool CanToggle => Line.IsEntry && !Line.IsReadOnly;

    public string Domain => IsUnparseable
        ? Truncate(Line.Body.Trim(), 60)
        : string.Join(", ", Line.Hostnames);

    /// <summary>The mapped IP, or the line number for junk so it can be found in an editor.</summary>
    public string MapsTo => IsUnparseable ? $"line {Line.LineNumber}" : Line.Ip ?? "";

    /// <summary>Lexicographically sortable key that still orders IP addresses numerically.</summary>
    public string MapsToSortKey
    {
        get
        {
            if (!System.Net.IPAddress.TryParse(Line.Ip, out var address)) return "2" + MapsTo;
            var family = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "0" : "1";
            return family + Convert.ToHexString(address.GetAddressBytes());
        }
    }

    public string Source => Line.ManagedBy ?? (IsUnparseable ? "Unknown" : "You");

    public string? Comment => Line.InlineComment;

    public bool IsEnabled
    {
        get => Line.IsEnabled;
        set
        {
            if (!CanToggle || Line.IsEnabled == value) return;

            _document.SetEnabled(Line, value);
            Raise();
            Raise(nameof(StatusText));
            Raise(nameof(State));
            _onChanged();
        }
    }

    public bool IsShadowed { get; set; }

    public string StatusText => Line.Kind switch
    {
        LineKind.Unparseable => "Invalid",
        _ when Line.IsReadOnly => "Read-only",
        _ when Line.IsModified => "Pending",
        LineKind.Entry => "Enabled",
        _ => "Disabled",
    };

    public RowState State => Line.Kind switch
    {
        LineKind.Unparseable => RowState.Invalid,
        _ when Line.IsReadOnly => RowState.Managed,
        _ when Line.IsModified => RowState.Pending,
        LineKind.Entry => RowState.Normal,
        _ => RowState.Disabled,
    };

    public string Tooltip => IsUnparseable
        ? $"Line {Line.LineNumber} is not a valid hosts entry and is ignored by Windows:\n{Line.Body.Trim()}"
        : IsReadOnly
            ? $"Managed by {Line.ManagedBy}. Change it in {Line.ManagedBy} instead."
            : $"{Line.Ip}  {string.Join(' ', Line.Hostnames)}" +
              (Comment is null ? "" : $"\n{Comment}") +
              (IsShadowed ? "\n\nThis domain is defined earlier in the file, so this line has no effect." : "");

    public void Refresh()
    {
        Raise(nameof(IsEnabled));
        Raise(nameof(StatusText));
        Raise(nameof(State));
        Raise(nameof(Tooltip));
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
