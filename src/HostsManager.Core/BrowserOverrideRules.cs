using System.Net;
using System.Net.Sockets;

namespace HostsManager.Core;

/// <summary>A hostname mapping applied only inside an isolated Chromium process.</summary>
public sealed record BrowserOverride(string Hostname, IPAddress Target);

/// <summary>Builds Chromium's host-resolver rule string from validated hosts entries.</summary>
public static class BrowserOverrideRules
{
    public static IReadOnlyList<BrowserOverride> FromLine(HostsLine line) =>
        FromLines(new[] { line });

    public static IReadOnlyList<BrowserOverride> FromLines(IEnumerable<HostsLine> lines)
    {
        var selected = lines.ToList();
        if (selected.Count == 0)
            throw new ArgumentException("Select at least one valid hosts entry to preview.", nameof(lines));

        var targets = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
        var result = new List<BrowserOverride>();

        foreach (var line in selected)
        {
            if (!line.IsEntry || line.Ip is null || line.Hostnames.Count == 0)
                throw new ArgumentException("Select only valid hosts entries to preview.", nameof(lines));

            if (!IPAddress.TryParse(line.Ip, out var target))
                throw new ArgumentException($"'{line.Ip}' is not a valid IP address.", nameof(lines));

            foreach (var hostname in line.Hostnames)
            {
                Add(hostname, target);

                // Browsers normally canonicalize an FQDN before resolution. Including the
                // unqualified spelling makes a hosts entry ending in '.' useful in a URL too.
                if (hostname.EndsWith('.')) Add(hostname.TrimEnd('.'), target);
            }
        }

        return result;

        void Add(string hostname, IPAddress target)
        {
            if (!HostsValidator.ValidateHostname(hostname).IsValid) return;

            if (targets.TryGetValue(hostname, out var existing))
            {
                if (!existing.Equals(target))
                    throw new ArgumentException(
                        $"'{hostname}' maps to both {existing} and {target} in the selected entries.",
                        nameof(lines));
                return;
            }

            targets.Add(hostname, target);
            result.Add(new BrowserOverride(hostname, target));
        }
    }

    public static string Build(IEnumerable<BrowserOverride> overrides)
    {
        var rules = new List<string>();
        var targets = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in overrides)
        {
            var hostname = item.Hostname.Trim();
            var validation = HostsValidator.ValidateHostname(hostname);
            if (!validation.IsValid)
                throw new ArgumentException(validation.Error, nameof(overrides));

            if (targets.TryGetValue(hostname, out var existing))
            {
                if (!existing.Equals(item.Target))
                    throw new ArgumentException(
                        $"'{hostname}' cannot map to both {existing} and {item.Target}.",
                        nameof(overrides));
                continue;
            }

            targets.Add(hostname, item.Target);

            var target = item.Target.AddressFamily == AddressFamily.InterNetworkV6
                ? $"[{item.Target}]"
                : item.Target.ToString();

            rules.Add($"MAP {hostname} {target}");
        }

        if (rules.Count == 0)
            throw new ArgumentException("At least one browser override is required.", nameof(overrides));

        return string.Join(", ", rules);
    }
}
