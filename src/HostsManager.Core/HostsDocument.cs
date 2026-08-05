using System.Security.Cryptography;
using System.Text;

namespace HostsManager.Core;

/// <summary>
/// An in-memory hosts file: the ordered line model plus the mutations the UI performs.
/// <para>
/// The core guarantee: for an unmodified document, <see cref="Render"/> returns exactly
/// the text it was parsed from. Every mutation is scoped to the lines it touches, so
/// unrelated content — comments, blank lines, junk, other tools' blocks — is carried
/// through verbatim.
/// </para>
/// </summary>
public sealed class HostsDocument
{
    private readonly List<HostsLine> _lines;

    internal HostsDocument(List<HostsLine> lines, FileFormat format, string originalText, int leadingDocLines)
    {
        _lines = lines;
        Format = format;
        OriginalText = originalText;
        LeadingDocLines = leadingDocLines;
    }

    public IReadOnlyList<HostsLine> Lines => _lines;

    public FileFormat Format { get; }

    /// <summary>The baseline text this document is compared against to decide if it is dirty.</summary>
    public string OriginalText { get; private set; }

    /// <summary>Length of the documentation header, carried into verification re-parses.</summary>
    public int LeadingDocLines { get; }

    public IEnumerable<HostsLine> Entries => _lines.Where(l => l.IsEntry);

    public IEnumerable<HostsLine> UnparseableLines => _lines.Where(l => l.Kind == LineKind.Unparseable);

    public IReadOnlyList<string> Groups => Entries
        .Where(line => !string.IsNullOrWhiteSpace(line.GroupName))
        .Select(line => line.GroupName!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public bool IsDirty => Render() != OriginalText;

    public int ModifiedCount => _lines.Count(l => l.IsModified);

    public string Render()
    {
        var sb = new StringBuilder(OriginalText.Length + 64);
        foreach (var line in _lines)
        {
            sb.Append(line.Render());
            sb.Append(line.Terminator);
        }
        return sb.ToString();
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>
    /// Accepts the current state as the new baseline after a successful save.
    /// <para>
    /// Deliberately not a re-parse. Re-parsing would discard each line's captured
    /// <see cref="HostsLine.DisablePrefix"/> — an entry written as "#\t127.0.0.1 host"
    /// and then enabled would come back disabled as plain "#127.0.0.1 host", silently
    /// reformatting a line the user never edited.
    /// </para>
    /// </summary>
    public void Commit()
    {
        OriginalText = Render();
        foreach (var line in _lines) line.MarkCommitted();
    }

    // ---- mutations -------------------------------------------------------

    public void SetEnabled(HostsLine line, bool enabled)
    {
        Guard(line);
        if (!line.IsEntry) throw new InvalidOperationException("Only entries can be enabled or disabled.");
        if (line.IsEnabled == enabled) return;

        // No pending flag to set: the line's own render now differs from its committed
        // text, and toggling straight back makes it match again.
        line.Kind = enabled ? LineKind.Entry : LineKind.DisabledEntry;
    }

    public void Toggle(HostsLine line) => SetEnabled(line, !line.IsEnabled);

    /// <summary>Assigns editable entries to one group without reordering their mappings.</summary>
    public void AssignGroup(IEnumerable<HostsLine> lines, string? groupName)
    {
        var selected = lines.Distinct().ToArray();
        var normalized = groupName is null ? null : HostsGroups.NormalizeName(groupName);

        foreach (var line in selected)
        {
            Guard(line);
            if (!line.IsEntry) throw new InvalidOperationException("Only hosts entries can be grouped.");
        }

        if (selected.Length == 0 || selected.All(line =>
                string.Equals(line.GroupName, normalized, StringComparison.OrdinalIgnoreCase))) return;

        foreach (var line in selected)
        {
            line.GroupName = normalized;
        }
        RebuildGroupMarkers();
        SettleIfUnchanged();
    }

    public int SetGroupEnabled(string groupName, bool enabled)
    {
        var normalized = HostsGroups.NormalizeName(groupName);
        var changed = 0;

        foreach (var line in Entries.Where(line => !line.IsReadOnly &&
                     string.Equals(line.GroupName, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            if (line.IsEnabled == enabled) continue;
            SetEnabled(line, enabled);
            changed++;
        }

        return changed;
    }

    public void RenameGroup(string currentName, string newName)
    {
        var current = HostsGroups.NormalizeName(currentName);
        var replacement = HostsGroups.NormalizeName(newName);
        var matches = Entries.Where(line => !line.IsReadOnly &&
            string.Equals(line.GroupName, current, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0) throw new InvalidOperationException($"Group '{current}' no longer exists.");

        var collision = Groups.Any(name =>
            !string.Equals(name, current, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(name, replacement, StringComparison.OrdinalIgnoreCase));
        if (collision) throw new ArgumentException($"A group named '{replacement}' already exists.", nameof(newName));

        if (string.Equals(current, replacement, StringComparison.Ordinal)) return;

        foreach (var line in matches)
        {
            line.GroupName = replacement;
        }
        RebuildGroupMarkers();
        SettleIfUnchanged();
    }

    /// <summary>Removes a group but deliberately keeps all of its entries.</summary>
    public void DeleteGroup(string groupName)
    {
        var normalized = HostsGroups.NormalizeName(groupName);
        var matches = Entries.Where(line => !line.IsReadOnly &&
            string.Equals(line.GroupName, normalized, StringComparison.OrdinalIgnoreCase)).ToArray();
        AssignGroup(matches, null);
    }

    /// <summary>
    /// Appends a new active entry after the last user-owned entry, keeping the user's
    /// entries grouped and clear of any tool-managed block.
    /// </summary>
    public HostsLine AddEntry(string ip, IReadOnlyList<string> hostnames, string? comment = null)
    {
        var ipCheck = HostsValidator.ValidateIp(ip);
        if (!ipCheck.IsValid) throw new ArgumentException(ipCheck.Error, nameof(ip));

        if (hostnames.Count == 0) throw new ArgumentException("Enter at least one domain name.", nameof(hostnames));
        foreach (var host in hostnames)
        {
            var check = HostsValidator.ValidateHostname(host);
            if (!check.IsValid) throw new ArgumentException(check.Error, nameof(hostnames));
        }

        var commentCheck = HostsValidator.ValidateComment(comment);
        if (!commentCheck.IsValid) throw new ArgumentException(commentCheck.Error, nameof(comment));

        var body = $"{ip} {string.Join(' ', hostnames)}";
        if (!string.IsNullOrWhiteSpace(comment)) body += $" # {comment.Trim()}";

        var line = new HostsLine
        {
            Kind = LineKind.Entry,
            Body = body,
            Terminator = Format.NewLine,
            Ip = ip,
            Hostnames = hostnames.ToList(),
            InlineComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
        };

        var insertAt = UserEntryInsertIndex();
        _lines.Insert(insertAt, line);

        EnsureLineSeparators();
        SettleIfUnchanged();
        return line;
    }

    /// <summary>
    /// Replaces only mappings owned by the user. Comments, unparseable text and blocks
    /// owned by another tool stay exactly where and how they were in the current file.
    /// Imported mappings keep their enabled state, spacing and inline comments.
    /// </summary>
    public int ReplaceUserEntries(IEnumerable<HostsImportEntry> entries)
    {
        var replacements = entries.Select(CreateImportedLine).ToList();
        var editableEntryCount = _lines.Count(line => line.IsEntry && !line.IsReadOnly);
        var editableIndices = _lines
            .Select((line, index) => (line, index))
            .Where(item => (item.line.IsEntry && !item.line.IsReadOnly) || item.line.IsGroupMarker)
            .Select(item => item.index)
            .ToArray();

        var insertAt = editableIndices.Length > 0 ? editableIndices[0] : UserEntryInsertIndex();
        for (var i = editableIndices.Length - 1; i >= 0; i--)
            _lines.RemoveAt(editableIndices[i]);

        insertAt = Math.Clamp(insertAt, 0, _lines.Count);
        _lines.InsertRange(insertAt, replacements);
        RebuildGroupMarkers();
        EnsureLineSeparators();
        SettleIfUnchanged();
        return editableEntryCount;
    }

    /// <summary>
    /// Adds mappings from another file without introducing duplicate hostnames. Existing
    /// active and disabled names both count as duplicates. If only some aliases on an
    /// imported line are new, the new aliases are retained as one normalized mapping.
    /// </summary>
    public HostsMergeResult MergeEntries(IEnumerable<HostsImportEntry> entries)
    {
        var known = _lines
            .Where(line => line.IsEntry)
            .SelectMany(line => line.Hostnames)
            .Select(NormalizeHostname)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var additions = new List<HostsLine>();
        var addedHostnames = 0;
        var skippedHostnames = 0;

        foreach (var entry in entries)
        {
            ValidateImportEntry(entry);

            var novel = new List<string>();
            foreach (var hostname in entry.Hostnames)
            {
                if (known.Add(NormalizeHostname(hostname))) novel.Add(hostname);
                else skippedHostnames++;
            }

            if (novel.Count == 0) continue;

            addedHostnames += novel.Count;
            additions.Add(novel.Count == entry.Hostnames.Count
                ? CreateImportedLine(entry)
                : CreateNormalizedImportedLine(entry, novel));
        }

        var insertAt = UserEntryInsertIndex();
        _lines.InsertRange(insertAt, additions);
        if (additions.Count > 0) RebuildGroupMarkers();
        EnsureLineSeparators();
        SettleIfUnchanged();

        return new HostsMergeResult(additions.Count, addedHostnames, skippedHostnames);
    }

    public void Remove(HostsLine line)
    {
        Guard(line);

        var index = _lines.IndexOf(line);
        if (index < 0) return;

        // The final line owns whether the file ends with a newline; hand that off
        // before dropping it, so removing the last entry can't strip the trailing newline.
        if (index == _lines.Count - 1 && index > 0)
            _lines[index - 1].Terminator = line.Terminator;

        _lines.RemoveAt(index);
        if (line.IsEntry && line.GroupName is not null) RebuildGroupMarkers();
        SettleIfUnchanged();
    }

    // ---- analysis --------------------------------------------------------

    /// <summary>
    /// Active hostnames defined more than once. Windows resolves the first match, so
    /// every later duplicate is dead weight the user probably didn't intend.
    /// </summary>
    public IReadOnlyList<HostsLine> FindShadowedEntries()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shadowed = new List<HostsLine>();

        foreach (var line in _lines.Where(l => l.Kind == LineKind.Entry))
        {
            foreach (var host in line.Hostnames)
            {
                if (!seen.Add(host.TrimEnd('.')))
                {
                    shadowed.Add(line);
                    break;
                }
            }
        }

        return shadowed;
    }

    // ---- internals -------------------------------------------------------

    /// <summary>
    /// Keeps the promise that a document matching the file on disk has nothing pending.
    /// <para>
    /// A line compares itself against its own committed text, which settles a toggle that
    /// was undone (see <see cref="HostsLine.IsModified"/>) but cannot settle a line that
    /// was inserted, because a new line has no committed text to compare against. When
    /// inserts and removals happen to add back up to the file already on disk — importing
    /// a file identical to the current one — only the document can see it. Without this,
    /// the window would report unsaved changes while Save sat correctly disabled, since
    /// <see cref="IsDirty"/> says there is nothing to write.
    /// </para>
    /// </summary>
    private void SettleIfUnchanged()
    {
        if (IsDirty) return;

        foreach (var line in _lines) line.MarkCommitted();
    }

    private void Guard(HostsLine line)
    {
        if (!_lines.Contains(line))
            throw new InvalidOperationException("This line does not belong to the current hosts file.");
        if (line.IsReadOnly)
            throw new InvalidOperationException($"This entry is managed by {line.ManagedBy} and cannot be changed here.");
    }

    private int UserEntryInsertIndex()
    {
        for (var i = _lines.Count - 1; i >= 0; i--)
        {
            if (!_lines[i].IsEntry || _lines[i].IsReadOnly) continue;

            if (_lines[i].GroupName is not null)
            {
                for (var j = i + 1; j < _lines.Count; j++)
                    if (_lines[j].GroupMarker == GroupMarkerKind.End &&
                        string.Equals(_lines[j].GroupName, _lines[i].GroupName,
                            StringComparison.OrdinalIgnoreCase)) return j + 1;
            }

            return i + 1;
        }

        var firstManaged = _lines.FindIndex(line => line.IsReadOnly);
        return firstManaged >= 0 ? firstManaged : _lines.Count;
    }

    private HostsLine CreateImportedLine(HostsImportEntry entry)
    {
        ValidateImportEntry(entry);

        return new HostsLine
        {
            Kind = entry.IsEnabled ? LineKind.Entry : LineKind.DisabledEntry,
            Body = entry.Body,
            DisablePrefix = entry.DisablePrefix,
            Terminator = Format.NewLine,
            Ip = entry.Ip,
            Hostnames = entry.Hostnames.ToArray(),
            InlineComment = entry.Comment,
            GroupName = entry.GroupName is null ? null : HostsGroups.NormalizeName(entry.GroupName),
        };
    }

    private HostsLine CreateNormalizedImportedLine(HostsImportEntry entry, IReadOnlyList<string> hostnames)
    {
        var body = $"{entry.Ip} {string.Join(' ', hostnames)}";
        if (!string.IsNullOrWhiteSpace(entry.Comment)) body += $" # {entry.Comment.Trim()}";

        return CreateImportedLine(entry with { Hostnames = hostnames, Body = body });
    }

    private static void ValidateImportEntry(HostsImportEntry entry)
    {
        var ipCheck = HostsValidator.ValidateIp(entry.Ip);
        if (!ipCheck.IsValid) throw new ArgumentException(ipCheck.Error, nameof(entry));

        if (entry.Hostnames.Count == 0)
            throw new ArgumentException("An imported entry must contain at least one domain name.", nameof(entry));

        foreach (var hostname in entry.Hostnames)
        {
            var check = HostsValidator.ValidateHostname(hostname);
            if (!check.IsValid) throw new ArgumentException(check.Error, nameof(entry));
        }

        var commentCheck = HostsValidator.ValidateComment(entry.Comment);
        if (!commentCheck.IsValid) throw new ArgumentException(commentCheck.Error, nameof(entry));
    }

    private static string NormalizeHostname(string hostname) => hostname.TrimEnd('.');

    private void RebuildGroupMarkers()
    {
        var content = _lines.Where(line => !line.IsGroupMarker).ToArray();
        var rebuilt = new List<HostsLine>(content.Length + 8);
        string? openGroup = null;

        foreach (var line in content)
        {
            var lineGroup = line.IsEntry && !line.IsReadOnly ? line.GroupName : null;

            if (!string.Equals(openGroup, lineGroup, StringComparison.OrdinalIgnoreCase))
            {
                if (openGroup is not null) rebuilt.Add(CreateGroupEnd(openGroup));
                openGroup = null;

                if (lineGroup is not null)
                {
                    rebuilt.Add(CreateGroupStart(lineGroup));
                    openGroup = lineGroup;
                }
            }

            rebuilt.Add(line);
        }

        if (openGroup is not null) rebuilt.Add(CreateGroupEnd(openGroup));

        _lines.Clear();
        _lines.AddRange(rebuilt);
        EnsureLineSeparators();
    }

    private HostsLine CreateGroupStart(string name) => new()
    {
        Kind = LineKind.Comment,
        Body = HostsGroups.RenderStart(name),
        Terminator = Format.NewLine,
        GroupName = name,
        GroupMarker = GroupMarkerKind.Start,
    };

    private HostsLine CreateGroupEnd(string name) => new()
    {
        Kind = LineKind.Comment,
        Body = HostsGroups.EndMarker,
        Terminator = Format.NewLine,
        GroupName = name,
        GroupMarker = GroupMarkerKind.End,
    };

    /// <summary>
    /// Gives every line a terminator after an insert.
    /// <para>
    /// A file that does not end with a newline has a final line whose terminator is "".
    /// Inserting after it would run the two entries together on one physical line, so
    /// every line except the last needs one — not just the last, which is the line the
    /// insert has already displaced.
    /// </para>
    /// </summary>
    private void EnsureLineSeparators()
    {
        for (var i = 0; i < _lines.Count; i++)
        {
            if (_lines[i].Terminator.Length == 0) _lines[i].Terminator = Format.NewLine;
        }
    }
}
