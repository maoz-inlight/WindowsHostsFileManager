using HostsManager.Core;

namespace HostsManager.Tests;

public class BrowserPreviewFlagsTests
{
    [Fact]
    public void Combine_DeduplicatesPresetAlreadyEnteredAsCustomFlag()
    {
        Assert.Equal(new[] { "--ignore-certificate-errors", "--disable-web-security" },
            BrowserPreviewFlags.Combine("--ignore-certificate-errors",
                new[] { "--ignore-certificate-errors", "--disable-web-security" }));
    }

    [Fact]
    public void Combine_StillRejectsConflictingValuesAndDuplicateCustomFlags()
    {
        Assert.Throws<ArgumentException>(() => BrowserPreviewFlags.Combine(
            "--disable-web-security=false", new[] { "--disable-web-security" }));
        Assert.Throws<ArgumentException>(() => BrowserPreviewFlags.Combine(
            "--incognito\n--incognito", Array.Empty<string>()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n\t\n")]
    public void Parse_EmptyInputLeavesDefaultLaunchUnchanged(string? text) =>
        Assert.Empty(BrowserPreviewFlags.Parse(text));

    [Fact]
    public void Parse_PreservesEachValueAsOneArgument()
    {
        var flags = BrowserPreviewFlags.Parse(
            "  --disable-extensions  \r\n\r\n--user-agent=Test Browser/1.0\n" +
            "--log-file=C:\\logs with spaces\\browser.txt\n--example=a=b; & value\r--empty=");

        Assert.Equal(new[]
        {
            "--disable-extensions", "--user-agent=Test Browser/1.0",
            "--log-file=C:\\logs with spaces\\browser.txt", "--example=a=b; & value", "--empty=",
        }, flags);
    }

    [Theory]
    [InlineData("--user-data-dir=C:\\other-profile")]
    [InlineData("--USER-DATA-DIR=C:\\other-profile")]
    [InlineData("--host-resolver-rules=MAP * 127.0.0.1")]
    [InlineData("--no-first-run")]
    [InlineData("--no-default-browser-check")]
    [InlineData("--disable-background-mode=false")]
    [InlineData("--new-window")]
    public void Parse_RejectsOverridesOfManagedSwitches(string text)
    {
        var error = Assert.Throws<ArgumentException>(() => BrowserPreviewFlags.Parse(text));
        Assert.Contains("managed by Hosts Manager", error.Message);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("--")]
    [InlineData("---user-data-dir=test")]
    [InlineData("-user-data-dir=test")]
    [InlineData("/user-data-dir=test")]
    [InlineData("--=value")]
    [InlineData("--disable-extensions --incognito")]
    [InlineData("--user-agent Test Browser")]
    [InlineData("\"--incognito\"")]
    [InlineData("--example=value\0other")]
    public void Parse_RejectsMalformedArgumentsWithLineNumber(string text)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            BrowserPreviewFlags.Parse("\n--disable-extensions\n" + text));
        Assert.Contains("line 3", error.Message);
    }

    [Fact]
    public void Parse_RejectsDuplicateSwitchesWithDifferentValues()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            BrowserPreviewFlags.Parse("--user-agent=First\n--USER-AGENT=Second"));
        Assert.Contains("listed more than once", error.Message);
    }
}
