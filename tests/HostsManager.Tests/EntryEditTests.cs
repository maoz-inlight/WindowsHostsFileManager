using HostsManager.Core;

namespace HostsManager.Tests;

public class EntryEditTests
{
    [Theory]
    [InlineData("")]
    [InlineData("#\t")]
    public void EditPreservesPositionGroupStateAndUnrelatedBytes(string prefix)
    {
        var before = "# HostsManager: group Development\r\n" + prefix +
            " 127.0.0.1\told.test  alias.test  # keep\r\n# HostsManager: end-group\r\n" +
            "# Added by Docker Desktop\n192.168.1.2 host.docker.internal\n# End of section\n";
        var doc = HostsFileParser.Parse(before, FileFormat.Default);
        var line = doc.Entries.First();
        doc.EditEntry(line, "::1", new[] { "new.test", "alias.test" }, "keep");
        Assert.Equal(before.Replace("127.0.0.1\told.test", "::1\tnew.test"), doc.Render());
        Assert.Same(line, doc.Lines[1]);
        Assert.Equal("Development", line.GroupName);
        Assert.Equal(prefix.Length == 0, line.IsEnabled);
        Assert.True(doc.IsDirty);
        doc.EditEntry(line, "127.0.0.1", new[] { "old.test", "alias.test" }, "keep");
        Assert.Equal(before, doc.Render());
        Assert.False(doc.IsDirty);
        Assert.False(line.IsModified);
    }

    [Fact]
    public void NoOpPreservesWhitespaceAndEmptyComment()
    {
        var text = "127.0.0.1\tone.test   #   ";
        var doc = HostsFileParser.Parse(text, FileFormat.Default);
        var line = doc.Entries.Single();
        doc.EditEntry(line, line.Ip!, line.Hostnames, line.InlineComment);
        Assert.Equal(text, doc.Render());
        Assert.False(doc.IsDirty);
    }

    [Theory]
    [InlineData("bad", "valid.test", "comment")]
    [InlineData("127.0.0.1", "https://bad.test", "comment")]
    [InlineData("127.0.0.1", "valid.test", "injected\n127.0.0.1 other.test")]
    public void InvalidEditIsAtomic(string ip, string hostname, string comment)
    {
        const string text = "127.0.0.1 original.test # original\n";
        var doc = HostsFileParser.Parse(text, FileFormat.Default);
        Assert.Throws<ArgumentException>(() => doc.EditEntry(doc.Entries.Single(), ip, new[] { hostname }, comment));
        Assert.Equal(text, doc.Render());
        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void ManagedAndForeignEntriesCannotBeEdited()
    {
        var doc = HostsFileParser.Parse("# Added by Docker Desktop\n127.0.0.1 docker.test\n# End of section\n", FileFormat.Default);
        Assert.Throws<InvalidOperationException>(() => doc.EditEntry(doc.Entries.Single(), "::1", new[] { "changed.test" }));
        var other = HostsFileParser.Parse("127.0.0.1 other.test", FileFormat.Default);
        Assert.Throws<InvalidOperationException>(() => other.EditEntry(doc.Entries.Single(), "::1", new[] { "changed.test" }));
        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void EditedDisabledAliasesAndCommentSurviveSaveAndReload()
    {
        var root = Path.Combine(Path.GetTempPath(), "HostsManagerEdit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "hosts");
            File.WriteAllText(path, "# HostsManager: group Test\n#\t127.0.0.1 old.test # old\n# HostsManager: end-group\n");
            var writer = new HostsFileWriter(path, new BackupManager(Path.Combine(root, "backups")));
            var doc = writer.Load();
            doc.EditEntry(doc.Entries.Single(), "::1", new[] { "new.test", "alias.test" }, "new comment");
            writer.Save();
            var entry = writer.Load().Entries.Single();
            Assert.False(entry.IsEnabled);
            Assert.Equal("Test", entry.GroupName);
            Assert.Equal("::1", entry.Ip);
            Assert.Equal(new[] { "new.test", "alias.test" }, entry.Hostnames);
            Assert.Equal("new comment", entry.InlineComment);
        }
        finally { Directory.Delete(root, true); }
    }
}
