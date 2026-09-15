namespace HostsManager.Core;

/// <summary>Each nonempty line is one Chromium switch, passed as a single argument.</summary>
public static class BrowserPreviewFlags
{
    public static IReadOnlyList<string> Combine(string? customFlags, IEnumerable<string> selectedFlags) =>
        Parse(string.Join(Environment.NewLine,
            Parse(customFlags).Concat(selectedFlags).Distinct(StringComparer.Ordinal)));

    private static readonly HashSet<string> ManagedFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "user-data-dir", "host-resolver-rules", "no-first-run",
        "no-default-browser-check", "disable-background-mode", "new-window",
    };

    public static IReadOnlyList<string> Parse(string? text)
    {
        var flags = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(text ?? "");
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            var flag = line.Trim();
            if (flag.Length == 0) continue;

            var separator = flag.IndexOf('=');
            var name = separator < 0 ? flag : flag[..separator];
            if (!name.StartsWith("--", StringComparison.Ordinal) || name.Length <= 2
                || !char.IsAsciiLetterOrDigit(name[2])
                || name[2..].Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')
                || flag.Any(char.IsControl))
                throw new ArgumentException(
                    $"Flag on line {lineNumber}: use --name or --name=value, one per line.");

            name = name[2..];
            if (ManagedFlags.Contains(name))
                throw new ArgumentException($"--{name} is managed by Hosts Manager and cannot be overridden.");
            if (!names.Add(name))
                throw new ArgumentException($"--{name} is listed more than once.");

            flags.Add(flag);
        }

        return flags;
    }
}
