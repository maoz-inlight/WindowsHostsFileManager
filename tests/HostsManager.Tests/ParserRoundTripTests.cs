using HostsManager.Core;

namespace HostsManager.Tests;

public class ParserRoundTripTests
{
    // ---- the central guarantee -------------------------------------------

    [Fact]
    public void RealHostsFile_ParseThenRender_IsByteIdentical()
    {
        var (text, format) = Fixture.Decoded;
        var doc = HostsFileParser.Parse(text, format);

        Assert.Equal(text, doc.Render());
        Assert.Equal(Fixture.Bytes, format.Encode(doc.Render()));
    }

    [Fact]
    public void RealHostsFile_FormatIsPreserved()
    {
        var (_, format) = Fixture.Decoded;

        Assert.True(format.HasBom);
        Assert.Equal("\r\n", format.NewLine);
        Assert.Equal("UTF-8 BOM · CRLF", format.Describe());
    }

    [Theory]
    [InlineData("127.0.0.1 a.local\r\n127.0.0.1 b.local\r\n")]
    [InlineData("127.0.0.1 a.local\n127.0.0.1 b.local\n")]
    [InlineData("127.0.0.1 a.local\r\n127.0.0.1 b.local")]      // no trailing newline
    [InlineData("127.0.0.1 a.local\r\n\n127.0.0.1 b.local\r\n")] // mixed endings
    [InlineData("")]
    [InlineData("\r\n\r\n")]
    public void ArbitraryContent_RoundTripsUnchanged(string text)
    {
        var doc = HostsFileParser.Parse(text, FileFormat.Default);
        Assert.Equal(text, doc.Render());
    }

    [Fact]
    public void UnmodifiedDocument_IsNotDirty()
    {
        Assert.False(Fixture.Parse().IsDirty);
    }

    // ---- toggling ---------------------------------------------------------

    [Fact]
    public void Toggle_ThenToggleBack_IsByteIdentical()
    {
        var (text, _) = Fixture.Decoded;
        var doc = Fixture.Parse();

        foreach (var entry in doc.Entries.Where(e => !e.IsReadOnly).ToList())
        {
            doc.Toggle(entry);
            doc.Toggle(entry);
        }

        Assert.Equal(text, doc.Render());
    }

    [Fact]
    public void DisablingAnEntry_OnlyAddsAHash()
    {
        var doc = Fixture.Parse();
        var entry = doc.Entries.First(e => e.PrimaryHostname == "ics.local");

        var before = doc.Render();
        doc.SetEnabled(entry, false);
        var after = doc.Render();

        Assert.Equal(before.Length + 1, after.Length);
        Assert.Contains("#127.0.0.1 ics.local", after);
        Assert.DoesNotContain("\r\n127.0.0.1 ics.local", after);
    }

    [Fact]
    public void EnablingADisabledEntry_PreservesOriginalSpacing()
    {
        var doc = Fixture.Parse();
        var entry = doc.Entries.First(e => e.PrimaryHostname == "localhost" && !e.IsEnabled);

        // The file writes this one as "#\t127.0.0.1       localhost".
        doc.SetEnabled(entry, true);
        Assert.Contains("127.0.0.1       localhost", doc.Render());

        doc.SetEnabled(entry, false);
        Assert.Contains("#\t127.0.0.1       localhost", doc.Render());
    }

    [Fact]
    public void ReadOnlyEntries_CannotBeToggled()
    {
        var doc = Fixture.Parse();
        var docker = doc.Entries.First(e => e.ManagedBy == "Docker");

        Assert.Throws<InvalidOperationException>(() => doc.Toggle(docker));
    }

    // ---- classification ---------------------------------------------------

    [Fact]
    public void PastedChatText_IsClassifiedUnparseable()
    {
        var junk = Fixture.Parse().UnparseableLines.ToList();

        Assert.NotEmpty(junk);
        Assert.Contains(junk, l => l.Body.Contains("i see only white now"));
        Assert.All(junk, l => Assert.True(l.LineNumber > 0));
    }

    [Fact]
    public void MicrosoftHeader_IsCommentsNotDisabledEntries()
    {
        var doc = Fixture.Parse();

        // The header's example mappings parse as valid entries, but showing them as
        // toggleable would bury the user's real entries.
        Assert.DoesNotContain(doc.Entries, e => e.Hostnames.Contains("rhino.acme.com"));
        Assert.DoesNotContain(doc.Entries, e => e.Hostnames.Contains("x.acme.com"));
        Assert.True(doc.LeadingDocLines > 3);
    }

    [Fact]
    public void CommentedEntriesBelowTheHeader_AreDisabledEntries()
    {
        var doc = Fixture.Parse();

        var disabled = doc.Entries.Where(e => !e.IsEnabled).Select(e => e.PrimaryHostname).ToList();

        Assert.Contains("broker.eventscalendar.co", disabled);
        Assert.Contains("dashboard.getpraise.com", disabled);
        Assert.Contains("dist.eventscalendar.co", disabled);
    }

    [Fact]
    public void ActiveEntries_AreDetected()
    {
        var doc = Fixture.Parse();
        var active = doc.Entries.Where(e => e.IsEnabled && !e.IsReadOnly).Select(e => e.PrimaryHostname).ToList();

        Assert.Contains("ics.local", active);
        Assert.Contains("broker.local", active);
    }

    [Fact]
    public void ManagedSections_AreMarkedAndReadOnly()
    {
        var doc = Fixture.Parse();

        var docker = doc.Entries.Where(e => e.ManagedBy == "Docker").Select(e => e.PrimaryHostname).ToList();
        var tailscale = doc.Entries.Where(e => e.ManagedBy == "Tailscale").Select(e => e.PrimaryHostname).ToList();

        Assert.Contains("host.docker.internal", docker);
        Assert.Contains("kubernetes.docker.internal", docker);
        Assert.Contains("maozs-mac-mini.tail772ce4.ts.net.", tailscale);

        Assert.All(doc.Entries.Where(e => e.ManagedBy is not null), e => Assert.True(e.IsReadOnly));
    }

    [Fact]
    public void GenericEndMarker_DoesNotCloseASectionItDidNotOpen()
    {
        var text = "# End of section\r\n127.0.0.1 free.local\r\n";
        var doc = HostsFileParser.Parse(text, FileFormat.Default);

        Assert.Null(doc.Entries.Single().ManagedBy);
    }

    [Fact]
    public void InlineComments_AreCaptured()
    {
        var doc = HostsFileParser.Parse("127.0.0.1 app.local # staging box\r\n", FileFormat.Default);
        var entry = doc.Entries.Single();

        Assert.Equal("staging box", entry.InlineComment);
        Assert.Equal("app.local", entry.PrimaryHostname);
    }

    [Fact]
    public void MultipleHostnamesOnOneLine_AreAllCaptured()
    {
        var doc = HostsFileParser.Parse("127.0.0.1 a.local b.local c.local\r\n", FileFormat.Default);

        Assert.Equal(new[] { "a.local", "b.local", "c.local" }, doc.Entries.Single().Hostnames);
    }

    // ---- add and remove ---------------------------------------------------

    [Fact]
    public void AddThenRemove_IsByteIdentical()
    {
        var (text, _) = Fixture.Decoded;
        var doc = Fixture.Parse();

        var added = doc.AddEntry("127.0.0.1", new[] { "test.local" });
        Assert.True(doc.IsDirty);

        doc.Remove(added);
        Assert.Equal(text, doc.Render());
    }

    [Fact]
    public void AddedEntry_LandsBeforeTheManagedBlocks()
    {
        var doc = Fixture.Parse();
        doc.AddEntry("127.0.0.1", new[] { "test.local" });

        var lines = doc.Lines.ToList();
        var addedIndex = lines.FindIndex(l => l.PrimaryHostname == "test.local");
        var firstManagedIndex = lines.FindIndex(l => l.IsReadOnly);

        Assert.True(addedIndex < firstManagedIndex,
            "A new entry must not land inside or after a block another tool owns.");
    }

    [Fact]
    public void AddEntry_WithComment_RendersInlineComment()
    {
        var doc = Fixture.Parse();
        doc.AddEntry("127.0.0.1", new[] { "api.local" }, "local api");

        Assert.Contains("127.0.0.1 api.local # local api", doc.Render());
    }

    [Fact]
    public void AddEntry_RejectsInvalidInput()
    {
        var doc = Fixture.Parse();

        Assert.Throws<ArgumentException>(() => doc.AddEntry("999.1.1.1", new[] { "a.local" }));
        Assert.Throws<ArgumentException>(() => doc.AddEntry("127.0.0.1", new[] { "not a host" }));
        Assert.Throws<ArgumentException>(() => doc.AddEntry("127.0.0.1", Array.Empty<string>()));
        Assert.Throws<ArgumentException>(() => doc.AddEntry("127.0.0.1", new[] { "a.local" }, "two\r\nlines"));
    }

    [Fact]
    public void RemovingTheJunkLines_LeavesEverythingElseUntouched()
    {
        var doc = Fixture.Parse();
        var before = doc.Render();

        var junk = doc.UnparseableLines.ToList();
        foreach (var line in junk) doc.Remove(line);

        var after = doc.Render();

        Assert.DoesNotContain("i see only white now", after);
        Assert.Contains("127.0.0.1 ics.local", after);
        Assert.Contains("# TailscaleHostsSectionStart", after);
        Assert.Contains("192.168.68.108 host.docker.internal", after);
        Assert.True(after.Length < before.Length);
    }

    [Fact]
    public void RemovingTheLastLine_KeepsTheTrailingNewlineBehaviour()
    {
        var doc = HostsFileParser.Parse("127.0.0.1 a.local\r\n127.0.0.1 b.local", FileFormat.Default);
        doc.Remove(doc.Lines[^1]);

        Assert.Equal("127.0.0.1 a.local", doc.Render());
    }

    // ---- duplicates -------------------------------------------------------

    [Fact]
    public void ShadowedEntries_AreReported()
    {
        var text = "127.0.0.1 dup.local\r\n127.0.0.2 dup.local\r\n127.0.0.1 unique.local\r\n";
        var doc = HostsFileParser.Parse(text, FileFormat.Default);

        var shadowed = doc.FindShadowedEntries();

        Assert.Single(shadowed);
        Assert.Equal("127.0.0.2", shadowed[0].Ip);
    }

    [Fact]
    public void DisabledDuplicates_AreNotReported()
    {
        var text = "127.0.0.1 dup.local\r\n#127.0.0.2 dup.local\r\n";
        var doc = HostsFileParser.Parse(text, FileFormat.Default);

        Assert.Empty(doc.FindShadowedEntries());
    }
}
