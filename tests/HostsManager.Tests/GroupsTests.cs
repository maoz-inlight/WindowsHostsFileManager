using System.Text;
using HostsManager.Core;

namespace HostsManager.Tests;

public class GroupsTests
{
    [Fact]
    public void Parser_RoundTripsGroupsAndKeepsThemEditable()
    {
        var text =
            "# HostsManager: group Local development\r\n" +
            "127.0.0.1 api.local\r\n" +
            "#127.0.0.1 old-api.local\r\n" +
            "# HostsManager: end-group\r\n";

        var document = HostsFileParser.Parse(text, FileFormat.Default);

        Assert.Equal(text, document.Render());
        Assert.Equal(new[] { "Local development" }, document.Groups);
        Assert.All(document.Entries, line =>
        {
            Assert.Equal("Local development", line.GroupName);
            Assert.False(line.IsReadOnly);
        });
        Assert.Equal(2, document.Lines.Count(line => line.IsGroupMarker));
    }

    [Fact]
    public void GroupMarkersInsideManagedSections_AreIgnored()
    {
        var text =
            "# Added by Docker Desktop\r\n" +
            "# HostsManager: group Not ours\r\n" +
            "192.168.1.2 host.docker.internal\r\n" +
            "# HostsManager: end-group\r\n" +
            "# End of section\r\n";

        var document = HostsFileParser.Parse(text, FileFormat.Default);

        Assert.Empty(document.Groups);
        Assert.Null(document.Entries.Single().GroupName);
        Assert.DoesNotContain(document.Lines, line => line.IsGroupMarker);
    }

    [Fact]
    public void DisabledOnlyGroupAtTop_IsNotMistakenForWindowsDocumentation()
    {
        var text =
            "# HostsManager: group Disabled set\r\n" +
            "#127.0.0.1 one.local\r\n" +
            "#127.0.0.1 two.local\r\n" +
            "# HostsManager: end-group\r\n\r\n";

        var document = HostsFileParser.Parse(text, FileFormat.Default);

        Assert.Equal(2, document.Entries.Count());
        Assert.All(document.Entries, line =>
        {
            Assert.False(line.IsEnabled);
            Assert.Equal("Disabled set", line.GroupName);
        });
        Assert.Equal(text, document.Render());
    }

    [Fact]
    public void AssignGroup_PreservesEntryOrderAndWritesPortableMarkers()
    {
        var text =
            "127.0.0.1 first.local\r\n" +
            "# keep this comment\r\n" +
            "127.0.0.1 second.local\r\n";
        var document = HostsFileParser.Parse(text, FileFormat.Default);

        document.AssignGroup(document.Entries, "Local development");

        var rendered = document.Render();
        Assert.True(rendered.IndexOf("first.local", StringComparison.Ordinal) <
                    rendered.IndexOf("second.local", StringComparison.Ordinal));
        Assert.Contains("# HostsManager: group Local development\r\n127.0.0.1 first.local", rendered);
        Assert.Contains("# keep this comment\r\n# HostsManager: group Local development", rendered);
        Assert.All(document.Entries, line => Assert.Equal("Local development", line.GroupName));
        HostsFileVerifier.Verify(document, rendered);
    }

    [Fact]
    public void AssigningScatteredEntries_DoesNotMoveUnselectedEntries()
    {
        var document = HostsFileParser.Parse(
            "127.0.0.1 one.local\r\n" +
            "127.0.0.1 two.local\r\n" +
            "127.0.0.1 three.local\r\n", FileFormat.Default);
        var entries = document.Entries.ToArray();

        document.AssignGroup(new[] { entries[0], entries[2] }, "Odd");

        Assert.Equal(new[] { "one.local", "two.local", "three.local" },
            document.Entries.Select(line => line.PrimaryHostname));
        Assert.Equal("Odd", entries[0].GroupName);
        Assert.Null(entries[1].GroupName);
        Assert.Equal("Odd", entries[2].GroupName);
        Assert.Equal(2, document.Lines.Count(line => line.GroupMarker == GroupMarkerKind.Start));
    }

    [Fact]
    public void SetGroupEnabled_ChangesEveryEditableMemberAndReportsChanges()
    {
        var document = HostsFileParser.Parse(
            "# HostsManager: group Work\r\n" +
            "127.0.0.1 one.local\r\n" +
            "#127.0.0.1 two.local\r\n" +
            "# HostsManager: end-group\r\n", FileFormat.Default);

        Assert.Equal(1, document.SetGroupEnabled("work", false));
        Assert.All(document.Entries, line => Assert.False(line.IsEnabled));
        Assert.Equal(2, document.SetGroupEnabled("WORK", true));
        Assert.All(document.Entries, line => Assert.True(line.IsEnabled));
    }

    [Fact]
    public void RenameAndDeleteGroup_KeepAllEntries()
    {
        var document = HostsFileParser.Parse(
            "# HostsManager: group Old\r\n" +
            "127.0.0.1 one.local\r\n" +
            "# HostsManager: end-group\r\n", FileFormat.Default);

        document.RenameGroup("Old", "New name");
        Assert.Equal("New name", document.Entries.Single().GroupName);
        Assert.Contains("# HostsManager: group New name", document.Render());

        document.DeleteGroup("New name");
        Assert.Single(document.Entries);
        Assert.Null(document.Entries.Single().GroupName);
        Assert.DoesNotContain("# HostsManager:", document.Render());
        Assert.True(document.IsDirty);
        Assert.True(document.ModifiedCount > 0);
    }

    [Fact]
    public void AddingAfterAGroup_LandsOutsideItsMarkers()
    {
        var document = HostsFileParser.Parse(
            "# HostsManager: group Existing\r\n" +
            "127.0.0.1 grouped.local\r\n" +
            "# HostsManager: end-group\r\n", FileFormat.Default);

        document.AddEntry("127.0.0.1", new[] { "ungrouped.local" });

        Assert.Null(document.Entries.Single(line => line.PrimaryHostname == "ungrouped.local").GroupName);
        Assert.True(document.Render().IndexOf("# HostsManager: end-group", StringComparison.Ordinal) <
                    document.Render().IndexOf("ungrouped.local", StringComparison.Ordinal));
    }

    [Fact]
    public void ImportAndMerge_PreservePortableGroups()
    {
        var source =
            "# HostsManager: group Imported\n" +
            "10.0.0.1 imported.local\n" +
            "# HostsManager: end-group\n";
        var import = HostsImportReader.Parse(Encoding.UTF8.GetBytes(source));
        Assert.Equal("Imported", import.Entries.Single().GroupName);

        var replaced = HostsFileParser.Parse("127.0.0.1 old.local\r\n", FileFormat.Default);
        replaced.ReplaceUserEntries(import.Entries);
        Assert.Equal("Imported", replaced.Entries.Single().GroupName);
        Assert.Contains("# HostsManager: group Imported", replaced.Render());

        var merged = HostsFileParser.Parse("127.0.0.1 current.local\r\n", FileFormat.Default);
        merged.MergeEntries(import.Entries);
        Assert.Equal("Imported", merged.Entries.Single(line =>
            line.PrimaryHostname == "imported.local").GroupName);
        Assert.Contains("# HostsManager: group Imported", merged.Render());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\nname")]
    [InlineData("All groups")]
    [InlineData("Ungrouped")]
    public void InvalidGroupNames_AreRejected(string name)
    {
        var document = HostsFileParser.Parse("127.0.0.1 app.local\r\n", FileFormat.Default);
        Assert.Throws<ArgumentException>(() => document.AssignGroup(document.Entries, name));
        Assert.Equal("127.0.0.1 app.local\r\n", document.Render());
    }
}
