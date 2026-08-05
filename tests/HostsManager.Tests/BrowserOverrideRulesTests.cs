using System.Net;
using HostsManager.Core;

namespace HostsManager.Tests;

public class BrowserOverrideRulesTests
{
    [Fact]
    public void FromLine_UsesEveryHostnameOnTheEntry()
    {
        var doc = HostsFileParser.Parse("127.0.0.1 api.local cdn.local\r\n", FileFormat.Default);

        var result = BrowserOverrideRules.FromLine(doc.Entries.Single());

        Assert.Equal(new[] { "api.local", "cdn.local" }, result.Select(r => r.Hostname));
        Assert.All(result, r => Assert.Equal(IPAddress.Loopback, r.Target));
    }

    [Fact]
    public void FromLine_AddsTheBrowserCanonicalFormOfATrailingDotHostname()
    {
        var doc = HostsFileParser.Parse("100.64.0.1 node.example.\r\n", FileFormat.Default);

        var result = BrowserOverrideRules.FromLine(doc.Entries.Single());

        Assert.Equal(new[] { "node.example.", "node.example" }, result.Select(r => r.Hostname));
    }

    [Fact]
    public void Build_FormatsIpv4RulesForChromium()
    {
        var rules = BrowserOverrideRules.Build(new[]
        {
            new BrowserOverride("api.local", IPAddress.Parse("127.0.0.1")),
            new BrowserOverride("cdn.local", IPAddress.Parse("192.168.1.20")),
        });

        Assert.Equal("MAP api.local 127.0.0.1, MAP cdn.local 192.168.1.20", rules);
    }

    [Fact]
    public void Build_BracketsIpv6Targets()
    {
        var rules = BrowserOverrideRules.Build(new[]
        {
            new BrowserOverride("api.local", IPAddress.IPv6Loopback),
        });

        Assert.Equal("MAP api.local [::1]", rules);
    }

    [Fact]
    public void Build_RemovesDuplicateHostnamesCaseInsensitively()
    {
        var rules = BrowserOverrideRules.Build(new[]
        {
            new BrowserOverride("API.local", IPAddress.Loopback),
            new BrowserOverride("api.LOCAL", IPAddress.Loopback),
        });

        Assert.Equal("MAP API.local 127.0.0.1", rules);
    }

    [Fact]
    public void FromLines_CombinesMappingsFromEverySelectedEntry()
    {
        var doc = HostsFileParser.Parse(
            "127.0.0.1 app.local\r\n192.168.1.20 api.local cdn.local\r\n",
            FileFormat.Default);

        var result = BrowserOverrideRules.FromLines(doc.Entries);

        Assert.Equal(new[] { "app.local", "api.local", "cdn.local" },
            result.Select(mapping => mapping.Hostname));
        Assert.Equal(IPAddress.Loopback, result[0].Target);
        Assert.Equal(IPAddress.Parse("192.168.1.20"), result[1].Target);
        Assert.Equal(IPAddress.Parse("192.168.1.20"), result[2].Target);
    }

    [Fact]
    public void FromLines_RejectsConflictingMappingsForTheSameHostname()
    {
        var doc = HostsFileParser.Parse(
            "127.0.0.1 api.local\r\n192.168.1.20 API.local\r\n",
            FileFormat.Default);

        var error = Assert.Throws<ArgumentException>(() =>
            BrowserOverrideRules.FromLines(doc.Entries));

        Assert.Contains("maps to both", error.Message);
    }

    [Fact]
    public void Build_RejectsConflictingMappingsForTheSameHostname()
    {
        var error = Assert.Throws<ArgumentException>(() => BrowserOverrideRules.Build(new[]
        {
            new BrowserOverride("api.local", IPAddress.Loopback),
            new BrowserOverride("API.local", IPAddress.Parse("192.168.1.20")),
        }));

        Assert.Contains("cannot map to both", error.Message);
    }

    [Fact]
    public void Build_RejectsHostnamesThatCouldInjectAnotherRule()
    {
        Assert.Throws<ArgumentException>(() => BrowserOverrideRules.Build(new[]
        {
            new BrowserOverride("safe.local, MAP * attacker", IPAddress.Loopback),
        }));
    }

    [Fact]
    public void Build_RequiresAtLeastOneOverride() =>
        Assert.Throws<ArgumentException>(() => BrowserOverrideRules.Build(Array.Empty<BrowserOverride>()));

    [Fact]
    public void FromLine_AllowsDisabledEntries()
    {
        var doc = HostsFileParser.Parse("#127.0.0.1 disabled.local\r\n", FileFormat.Default);

        var result = BrowserOverrideRules.FromLine(doc.Entries.Single());

        Assert.Single(result);
        Assert.Equal("disabled.local", result[0].Hostname);
    }
}
