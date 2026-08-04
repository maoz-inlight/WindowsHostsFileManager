namespace HostsManager.Core;

/// <summary>
/// A block of the hosts file owned by another tool, delimited by sentinel comments.
/// Entries inside are surfaced read-only: the owning tool rewrites them on its own
/// schedule, so editing them here would either be clobbered or clobber it.
/// </summary>
public sealed record ManagedSectionMarker(string Owner, string StartMarker, string EndMarker);

public static class ManagedSections
{
    /// <summary>
    /// Known third-party blocks. The end markers are matched only while inside the
    /// corresponding section, so a generic sentinel like "# End of section" cannot
    /// close a block it did not open.
    /// </summary>
    public static readonly IReadOnlyList<ManagedSectionMarker> Known = new[]
    {
        new ManagedSectionMarker("Docker", "# Added by Docker Desktop", "# End of section"),
        new ManagedSectionMarker("Tailscale", "# TailscaleHostsSectionStart", "# TailscaleHostsSectionEnd"),
    };

    public static ManagedSectionMarker? MatchStart(string rawLine, IReadOnlyList<ManagedSectionMarker> markers)
    {
        var trimmed = rawLine.Trim();
        return markers.FirstOrDefault(m => string.Equals(trimmed, m.StartMarker, StringComparison.OrdinalIgnoreCase));
    }

    public static bool MatchesEnd(string rawLine, ManagedSectionMarker marker) =>
        string.Equals(rawLine.Trim(), marker.EndMarker, StringComparison.OrdinalIgnoreCase);
}
