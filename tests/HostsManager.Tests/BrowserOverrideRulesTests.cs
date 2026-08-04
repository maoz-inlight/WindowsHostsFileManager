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
            new BrowserOverride("api.LOCAL", IPAddress.Parse("192.168.1.20")),
        });

        Assert.Equal("MAP API.local 127.0.0.1", rules);
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
