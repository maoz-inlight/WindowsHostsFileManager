namespace HostsManager.Core;

public enum LineKind
{
    /// <summary>Empty or whitespace-only.</summary>
    Blank,

    /// <summary>A genuine comment, including the file's leading documentation header.</summary>
    Comment,

    /// <summary>An active mapping: <c>IP host [host...] [# comment]</c>.</summary>
    Entry,

    /// <summary>A commented-out mapping, e.g. <c>#127.0.0.1 app.local</c>.</summary>
    DisabledEntry,

    /// <summary>Neither blank, comment, nor a valid mapping. Junk that Windows silently ignores.</summary>
    Unparseable,
}

/// <summary>
/// One physical line of the hosts file.
/// <para>
/// The invariant that makes saves safe: rendering a line that has not been modified
/// reproduces its original bytes exactly. Toggling only adds or removes the disable
/// prefix, leaving the entry body — including its original spacing, tabs and inline
/// comment — completely untouched.
/// </para>
/// </summary>
public sealed class HostsLine
{
    /// <summary>1-based position at load time. Used to point the user at junk lines.</summary>
    public int LineNumber { get; init; }

    public LineKind Kind { get; internal set; }

    /// <summary>The line's newline sequence, or "" for the final line when the file has no trailing newline.</summary>
    public string Terminator { get; internal set; } = "";

    /// <summary>
    /// For Entry/DisabledEntry: the mapping text without any leading comment marker.
    /// For every other kind: the full original line.
    /// </summary>
    public string Body { get; internal set; } = "";

    /// <summary>
    /// The exact text that precedes <see cref="Body"/> when this entry is disabled —
    /// captured from the file when it was already disabled, so re-enabling and
    /// re-disabling round-trips byte-for-byte. Defaults to "#" for entries that
    /// have only ever been active.
    /// </summary>
    public string DisablePrefix { get; internal set; } = "#";

    public string? Ip { get; internal set; }

    public IReadOnlyList<string> Hostnames { get; internal set; } = Array.Empty<string>();

    /// <summary>Trailing <c>#</c> comment on an entry line, without the marker.</summary>
    public string? InlineComment { get; internal set; }

    /// <summary>Owner of the managed block this line sits in ("Docker", "Tailscale"), else null.</summary>
    public string? ManagedBy { get; internal set; }

    /// <summary>User-defined group carried by HostsManager comment markers, else null.</summary>
    public string? GroupName { get; internal set; }

    /// <summary>Identifies a portable HostsManager group delimiter comment.</summary>
    public GroupMarkerKind GroupMarker { get; internal set; }

    public bool IsGroupMarker => GroupMarker != GroupMarkerKind.None;

    public bool IsReadOnly => ManagedBy is not null;

    public bool IsEntry => Kind is LineKind.Entry or LineKind.DisabledEntry;

    public bool IsEnabled => Kind == LineKind.Entry;

    /// <summary>
    /// The line's rendered text as of the last load or save, or null for a line added in
    /// this session. Comparing against it — rather than latching a flag when a mutation
    /// runs — is what makes undoing an edit actually undo it: toggling an entry off and
    /// back on leaves the line byte-identical to the file, so it stops being pending.
    /// A latched flag would leave the row marked "Pending" and the header claiming an
    /// unsaved change that no save could ever clear, because there is nothing to write.
    /// </summary>
    private string? _committedRender;

    /// <summary>
    /// Group membership is rendered by neighboring marker lines rather than by the entry
    /// itself, so its saved value is tracked separately for the row's pending state.
    /// </summary>
    private string? _committedGroupName;

    /// <summary>True while this line differs from the file: toggled, edited or newly added.</summary>
    public bool IsModified => _committedRender is null ||
        _committedRender != Render() ||
        !string.Equals(_committedGroupName, GroupName, StringComparison.Ordinal);

    /// <summary>Accepts the current text as this line's on-disk state, after a load or a save.</summary>
    internal void MarkCommitted()
    {
        _committedRender = Render();
        _committedGroupName = GroupName;
    }

    /// <summary>The primary hostname, used for display and duplicate detection.</summary>
    public string? PrimaryHostname => Hostnames.Count > 0 ? Hostnames[0] : null;

    /// <summary>Renders the line back to text, without its terminator.</summary>
    public string Render() => Kind switch
    {
        LineKind.Entry => Body,
        LineKind.DisabledEntry => DisablePrefix + Body,
        _ => Body,
    };

    public override string ToString() => Render();
}
