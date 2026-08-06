using System.Text.RegularExpressions;

namespace HostsManager.Core;

/// <summary>
/// Turns hosts-file text into a line model and back again without losing a byte.
/// Every classification path keeps the original text intact; nothing is normalized,
/// re-spaced or re-ordered.
/// </summary>
public static partial class HostsFileParser
{
    [GeneratedRegex(@"^(\s*#+[ \t]*)(.*)$")]
    private static partial Regex CommentPrefixPattern();

    /// <param name="leadingDocLines">
    /// Overrides the computed documentation-header length. Verification re-parses a
    /// rendered document with the original value so that classification is deterministic
    /// and the comparison tests the render, not the heuristic.
    /// </param>
    public static HostsDocument Parse(string text, FileFormat format,
        IReadOnlyList<ManagedSectionMarker>? markers = null, int? leadingDocLines = null)
    {
        markers ??= ManagedSections.Known;

        var raws = SplitLines(text);
        var docLines = leadingDocLines ?? ComputeLeadingDocLines(raws);
        var lines = new List<HostsLine>(raws.Count);

        for (var i = 0; i < raws.Count; i++)
        {
            var (raw, terminator) = raws[i];
            lines.Add(Classify(raw, terminator, i + 1, inLeadingDocs: i < docLines));
        }

        ApplyManagedSections(lines, raws, markers);
        ApplyGroups(lines, raws);

        return new HostsDocument(lines, format, text, docLines);
    }

    /// <summary>
    /// Length of the file's documentation header — the contiguous comment block at the
    /// top, which on Windows is Microsoft's boilerplate containing example mappings
    /// (rhino.acme.com, x.acme.com). Those parse as valid entries, so without this they
    /// would show up as disabled entries and bury the user's real ones.
    /// <para>
    /// A header only counts if it is several lines long and closed by a blank line.
    /// That keeps the result stable when a user disables an entry near the top of a file
    /// that has no such header.
    /// </para>
    /// </summary>
    private static int ComputeLeadingDocLines(List<(string Raw, string Terminator)> raws)
    {
        var n = 0;
        while (n < raws.Count && raws[n].Raw.TrimStart().StartsWith('#'))
        {
            // A group made only of disabled entries is also a run of comments. Its
            // marker must stop the Windows-boilerplate heuristic or the entries would
            // be mistaken for documentation and disappear from the grid.
            if (HostsGroups.TryMatchStart(raws[n].Raw, out _) || HostsGroups.IsEnd(raws[n].Raw)) break;
            n++;
        }

        if (n < 3) return 0;
        if (n >= raws.Count || !string.IsNullOrWhiteSpace(raws[n].Raw)) return 0;
        return n;
    }

    private static HostsLine Classify(string raw, string terminator, int lineNumber, bool inLeadingDocs)
    {
        var line = new HostsLine { LineNumber = lineNumber, Terminator = terminator, Body = raw };

        if (string.IsNullOrWhiteSpace(raw))
        {
            line.Kind = LineKind.Blank;
            return line;
        }

        if (raw.TrimStart().StartsWith('#'))
        {
            var match = CommentPrefixPattern().Match(raw);
            if (!inLeadingDocs && match.Success &&
                TryParseEntry(match.Groups[2].Value, out var dIp, out var dHosts, out var dComment))
            {
                line.Kind = LineKind.DisabledEntry;
                line.DisablePrefix = match.Groups[1].Value;
                line.Body = match.Groups[2].Value;
                line.Ip = dIp;
                line.Hostnames = dHosts;
                line.InlineComment = dComment;
                return line;
            }

            line.Kind = LineKind.Comment;
            return line;
        }

        if (TryParseEntry(raw, out var ip, out var hosts, out var comment))
        {
            line.Kind = LineKind.Entry;
            line.Ip = ip;
            line.Hostnames = hosts;
            line.InlineComment = comment;
            return line;
        }

        line.Kind = LineKind.Unparseable;
        return line;
    }

    /// <summary>
    /// Parses <c>IP host [host...] [# comment]</c>. Returns false for anything that
    /// isn't a well-formed mapping, which is what routes junk to <see cref="LineKind.Unparseable"/>.
    /// </summary>
    private static bool TryParseEntry(string body, out string? ip, out IReadOnlyList<string> hostnames,
        out string? inlineComment)
    {
        ip = null;
        hostnames = Array.Empty<string>();
        inlineComment = null;

        var hash = body.IndexOf('#');
        var mapping = hash >= 0 ? body[..hash] : body;
        var comment = hash >= 0 ? body[(hash + 1)..] : null;

        var tokens = mapping.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2) return false;

        if (!HostsValidator.ValidateIp(tokens[0]).IsValid) return false;

        var names = new List<string>(tokens.Length - 1);
        for (var i = 1; i < tokens.Length; i++)
        {
            if (!HostsValidator.ValidateHostname(tokens[i]).IsValid) return false;
            names.Add(tokens[i]);
        }

        ip = tokens[0];
        hostnames = names;
        inlineComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        return true;
    }

    private static void ApplyManagedSections(List<HostsLine> lines, List<(string Raw, string Terminator)> raws,
        IReadOnlyList<ManagedSectionMarker> markers)
    {
        ManagedSectionMarker? current = null;

        for (var i = 0; i < lines.Count; i++)
        {
            var raw = raws[i].Raw;

            if (current is null)
            {
                var start = ManagedSections.MatchStart(raw, markers);
                if (start is not null)
                {
                    current = start;
                    lines[i].ManagedBy = start.Owner;
                }
                continue;
            }

            lines[i].ManagedBy = current.Owner;
            if (ManagedSections.MatchesEnd(raw, current)) current = null;
        }
    }

    private static void ApplyGroups(List<HostsLine> lines, List<(string Raw, string Terminator)> raws)
    {
        string? current = null;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var raw = raws[i].Raw;

            // Comments inside blocks owned by another tool are its data, not ours.
            if (line.IsReadOnly) continue;

            if (current is null && HostsGroups.TryMatchStart(raw, out var name))
            {
                current = name;
                line.GroupMarker = GroupMarkerKind.Start;
                line.GroupName = name;
                continue;
            }

            if (current is not null && HostsGroups.IsEnd(raw))
            {
                line.GroupMarker = GroupMarkerKind.End;
                line.GroupName = current;
                current = null;
                continue;
            }

            if (current is not null && line.IsEntry) line.GroupName = current;
        }
    }

    /// <summary>
    /// Splits on CRLF, LF or CR, keeping each line's own terminator. Storing the
    /// terminator per line — rather than assuming one style for the file — is what
    /// lets a mixed-ending file round-trip unchanged.
    /// </summary>
    public static List<(string Raw, string Terminator)> SplitLines(string text)
    {
        var result = new List<(string, string)>();
        if (text.Length == 0) return result;

        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                var crlf = i + 1 < text.Length && text[i + 1] == '\n';
                result.Add((text[start..i], crlf ? "\r\n" : "\r"));
                if (crlf) i++;
                start = i + 1;
            }
            else if (text[i] == '\n')
            {
                result.Add((text[start..i], "\n"));
                start = i + 1;
            }
        }

        // Trailing content with no terminator becomes a final line with terminator "".
        if (start < text.Length) result.Add((text[start..], ""));

        return result;
    }
}
