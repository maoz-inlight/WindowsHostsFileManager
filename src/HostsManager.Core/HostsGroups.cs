using System.Text.RegularExpressions;

namespace HostsManager.Core;

public enum GroupMarkerKind
{
    None,
    Start,
    End,
}

/// <summary>
/// Portable group metadata stored as ordinary hosts-file comments. Windows ignores
/// the markers, while HostsManager can use them to filter and toggle related entries.
/// </summary>
public static partial class HostsGroups
{
    public const int MaximumNameLength = 60;

    [GeneratedRegex(@"^\s*#\s*HostsManager:\s*group\s+(.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex StartPattern();

    [GeneratedRegex(@"^\s*#\s*HostsManager:\s*end-group\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex EndPattern();

    public static string NormalizeName(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) throw new ArgumentException("Enter a group name.", nameof(name));
        if (trimmed.Length > MaximumNameLength)
            throw new ArgumentException($"Group names can be at most {MaximumNameLength} characters.", nameof(name));
        if (trimmed.Contains('\r') || trimmed.Contains('\n'))
            throw new ArgumentException("Group names cannot contain line breaks.", nameof(name));
        if (string.Equals(trimmed, "All groups", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Ungrouped", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"'{trimmed}' is reserved by the group filter.", nameof(name));
        return trimmed;
    }

    public static bool TryMatchStart(string rawLine, out string? name)
    {
        var match = StartPattern().Match(rawLine);
        if (!match.Success)
        {
            name = null;
            return false;
        }

        try
        {
            name = NormalizeName(match.Groups[1].Value);
            return true;
        }
        catch (ArgumentException)
        {
            name = null;
            return false;
        }
    }

    public static bool IsEnd(string rawLine) => EndPattern().IsMatch(rawLine);

    public static string RenderStart(string name) => $"# HostsManager: group {NormalizeName(name)}";

    public const string EndMarker = "# HostsManager: end-group";
}
