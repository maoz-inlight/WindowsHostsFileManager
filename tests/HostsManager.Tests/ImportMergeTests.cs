using System.Text;
using HostsManager.Core;

namespace HostsManager.Tests;

public class ImportMergeTests
{
    [Fact]
    public void ImportReader_ExtractsEditableEntriesAndReportsIgnoredContent()
    {
        var text =
            "127.0.0.1 active.local # local app\r\n" +
            "#\t127.0.0.2 disabled.local\r\n" +
            "this is not a mapping\r\n" +
            "# Added by Docker Desktop\r\n" +
            "192.168.1.2 host.docker.internal\r\n" +
            "# End of section\r\n";

        var import = HostsImportReader.Parse(Encoding.UTF8.GetBytes(text));

        Assert.Equal(2, import.Entries.Count);
        Assert.Equal(1, import.UnparseableLineCount);
        Assert.Equal(1, import.ManagedEntryCount);

        var active = import.Entries[0];
        Assert.True(active.IsEnabled);
        Assert.Equal("local app", active.Comment);

        var disabled = import.Entries[1];
        Assert.False(disabled.IsEnabled);
        Assert.Equal("#\t", disabled.DisablePrefix);
    }

    [Fact]
    public void ReplaceUserEntries_PreservesCurrentCommentsJunkAndManagedBlocks()
    {
        var current =
            "# keep this comment\r\n" +
            "127.0.0.1 old.local\r\n" +
            "pasted junk\r\n" +
            "# Added by Docker Desktop\r\n" +
            "192.168.1.2 host.docker.internal\r\n" +
            "# End of section\r\n";
        var source =
            "10.0.0.1 imported.local # imported comment\n" +
            "# 10.0.0.2 disabled.imported.local\n";

        var document = HostsFileParser.Parse(current, FileFormat.Default);
        var import = HostsImportReader.Parse(Encoding.UTF8.GetBytes(source));

        var removed = document.ReplaceUserEntries(import.Entries);

        Assert.Equal(1, removed);
        Assert.DoesNotContain("old.local", document.Render());
        Assert.Contains("# keep this comment\r\n", document.Render());
        Assert.Contains("pasted junk\r\n", document.Render());
        Assert.Contains("10.0.0.1 imported.local # imported comment\r\n", document.Render());
        Assert.Contains("# 10.0.0.2 disabled.imported.local\r\n", document.Render());
        Assert.Contains("192.168.1.2 host.docker.internal\r\n", document.Render());
        Assert.False(document.Entries.Single(line => line.PrimaryHostname == "disabled.imported.local").IsEnabled);
        HostsFileVerifier.Verify(document, document.Render());
    }

    [Fact]
    public void MergeEntries_AddsOnlyNewHostnamesAndKeepsImportedState()
    {
        var current =
            "127.0.0.1 existing.local\r\n" +
            "#127.0.0.2 disabled-existing.local\r\n";
        var source =
            "10.0.0.1 existing.local new.local # mixed aliases\r\n" +
            "10.0.0.2 disabled-existing.local\r\n" +
            "#10.0.0.3 disabled-new.local\r\n";

        var document = HostsFileParser.Parse(current, FileFormat.Default);
        var import = HostsImportReader.Parse(Encoding.UTF8.GetBytes(source));

        var result = document.MergeEntries(import.Entries);

        Assert.Equal(2, result.AddedEntries);
        Assert.Equal(2, result.AddedHostnames);
        Assert.Equal(2, result.SkippedDuplicateHostnames);
        Assert.Contains("10.0.0.1 new.local # mixed aliases", document.Render());
        Assert.DoesNotContain("10.0.0.1 existing.local new.local", document.Render());
        Assert.False(document.Entries.Single(line => line.PrimaryHostname == "disabled-new.local").IsEnabled);
        HostsFileVerifier.Verify(document, document.Render());
    }

    [Fact]
    public void MergeTreatsTrailingDotAndCaseAsTheSameHostname()
    {
        var document = HostsFileParser.Parse("127.0.0.1 Api.Local.\r\n", FileFormat.Default);
        var import = HostsImportReader.Parse(Encoding.UTF8.GetBytes("10.0.0.1 api.local\r\n"));

        var result = document.MergeEntries(import.Entries);

        Assert.Equal(0, result.AddedEntries);
        Assert.Equal(1, result.SkippedDuplicateHostnames);
        Assert.Single(document.Entries);
    }

    [Fact]
    public void AddEntry_WhenOnlyManagedEntriesExist_LandsBeforeTheirSection()
    {
        var text =
            "# Added by Docker Desktop\r\n" +
            "192.168.1.2 host.docker.internal\r\n" +
            "# End of section\r\n";
        var document = HostsFileParser.Parse(text, FileFormat.Default);

        document.AddEntry("127.0.0.1", new[] { "mine.local" });

        var lines = document.Lines.ToList();
        Assert.True(
            lines.FindIndex(line => line.PrimaryHostname == "mine.local") <
            lines.FindIndex(line => line.ManagedBy == "Docker"));
        HostsFileVerifier.Verify(document, document.Render());
    }

    [Fact]
    public void InvalidImport_IsRejectedBeforeTheDocumentChanges()
    {
        var original = "127.0.0.1 existing.local\r\n";
        var document = HostsFileParser.Parse(original, FileFormat.Default);
        var entries = new[]
        {
            new HostsImportEntry("10.0.0.1", new[] { "valid.local" }, null, true,
                "10.0.0.1 valid.local", "#"),
            new HostsImportEntry("999.0.0.1", new[] { "invalid.local" }, null, true,
                "999.0.0.1 invalid.local", "#"),
        };

        Assert.Throws<ArgumentException>(() => document.ReplaceUserEntries(entries));
        Assert.Equal(original, document.Render());
    }
}
