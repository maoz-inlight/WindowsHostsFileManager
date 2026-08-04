using System.Net;
using System.Net.Sockets;

namespace HostsManager.Core;

/// <summary>A hostname mapping applied only inside an isolated Chromium process.</summary>
public sealed record BrowserOverride(string Hostname, IPAddress Target);

/// <summary>Builds Chromium's host-resolver rule string from validated hosts entries.</summary>
public static class BrowserOverrideRules
{
    public static IReadOnlyList<BrowserOverride> FromLine(HostsLine line)
    {
        if (!line.IsEntry || line.Ip is null || line.Hostnames.Count == 0)
            throw new ArgumentException("Select a valid hosts entry to preview.", nameof(line));

        if (!IPAddress.TryParse(line.Ip, out var target))
            throw new ArgumentException($"'{line.Ip}' is not a valid IP address.", nameof(line));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<BrowserOverride>();

        foreach (var hostname in line.Hostnames)
        {
            Add(hostname);

            // Browsers normally canonicalize an FQDN before resolution. Including the
            // unqualified spelling makes a hosts entry ending in '.' useful in a URL too.
            if (hostname.EndsWith('.')) Add(hostname.TrimEnd('.'));
        }

        return result;

        void Add(string hostname)
        {
            if (!HostsValidator.ValidateHostname(hostname).IsValid || !seen.Add(hostname)) return;
            result.Add(new BrowserOverride(hostname, target));
        }
    }

    public static string Build(IEnumerable<BrowserOverride> overrides)
    {
        var rules = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in overrides)
        {
            var hostname = item.Hostname.Trim();
            var validation = HostsValidator.ValidateHostname(hostname);
            if (!validation.IsValid)
                throw new ArgumentException(validation.Error, nameof(overrides));

            if (!seen.Add(hostname)) continue;

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
