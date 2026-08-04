using System.Text;
using HostsManager.Core;

namespace HostsManager.Tests;

/// <summary>Covers defects found in review, so they cannot come back unnoticed.</summary>
public class RegressionTests
{
    // ---- files that do not end with a newline ------------------------------

    [Fact]
    public void AddEntry_ToFileWithoutTrailingNewline_DoesNotMergeLines()
    {
        var doc = HostsFileParser.Parse("127.0.0.1 a.local", FileFormat.Default);
        doc.AddEntry("127.0.0.1", new[] { "b.local" });

        Assert.Equal("127.0.0.1 a.local\r\n127.0.0.1 b.local\r\n", doc.Render());
    }

    [Fact]
    public void AddEntry_ToFileWithoutTrailingNewline_PassesVerification()
    {
        var doc = HostsFileParser.Parse("127.0.0.1 a.local", FileFormat.Default);
        doc.AddEntry("127.0.0.1", new[] { "b.local" });

        HostsFileVerifier.Verify(doc, doc.Render());
        Assert.Equal(2, doc.Entries.Count());
    }

    [Fact]
    public void AddEntry_AfterACommentWithoutTrailingNewline_DoesNotMergeLines()
    {
        var doc = HostsFileParser.Parse("# just a comment", FileFormat.Default);
        doc.AddEntry("127.0.0.1", new[] { "b.local" });

        Assert.Equal("# just a comment\r\n127.0.0.1 b.local\r\n", doc.Render());
        HostsFileVerifier.Verify(doc, doc.Render());
    }

    // ---- text that is not valid UTF-8 -------------------------------------

    private static byte[] Latin1Sample()
    {
        // 0xE9 is 'é' in Windows-1252 and is not valid standalone UTF-8.
        var bytes = new List<byte>();
        bytes.AddRange(Encoding.ASCII.GetBytes("# caf"));
        bytes.Add(0xE9);
        bytes.AddRange(Encoding.ASCII.GetBytes("\r\n127.0.0.1 a.local\r\n"));
        return bytes.ToArray();
    }

    [Fact]
    public void NonUtf8File_RoundTripsByteForByte()
    {
        var bytes = Latin1Sample();
        var (text, format) = FileFormat.Decode(bytes);
        var doc = HostsFileParser.Parse(text, format);

        Assert.False(format.IsUtf8);
        Assert.Equal(bytes, format.Encode(doc.Render()));
    }

    [Fact]
    public void NonUtf8File_SurvivesAToggle()
    {
        var bytes = Latin1Sample();
        var (text, format) = FileFormat.Decode(bytes);
        var doc = HostsFileParser.Parse(text, format);

        var entry = doc.Entries.Single();
        doc.SetEnabled(entry, false);
        doc.SetEnabled(entry, true);

        // The untouched comment must come back with its original byte, not U+FFFD.
        Assert.Equal(bytes, format.Encode(doc.Render()));
    }

    [Fact]
    public void Utf8File_IsStillDetectedAsUtf8()
    {
        var (_, format) = Fixture.Decoded;

        Assert.True(format.IsUtf8);
        Assert.True(format.HasBom);
        Assert.Equal("UTF-8 BOM · CRLF", format.Describe());
    }

    [Fact]
    public void NonUtf8Format_IsDescribedAsLatin1()
    {
        var (_, format) = FileFormat.Decode(Latin1Sample());
        Assert.Equal("Latin-1 · CRLF", format.Describe());
    }

    [Fact]
    public void CanRoundTrip_RejectsCharactersTheCodecCannotStore()
    {
        var (_, latin1) = FileFormat.Decode(Latin1Sample());

        Assert.True(latin1.CanRoundTrip("# plain ascii\r\n"));
        Assert.False(latin1.CanRoundTrip("# emoji \U0001F600\r\n"));   // outside Latin-1
        Assert.True(FileFormat.Default.CanRoundTrip("# emoji \U0001F600\r\n"));
    }

    [Fact]
    public void Save_RefusesTextTheFilesEncodingCannotStore()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HostsManagerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var hostsPath = Path.Combine(directory, "hosts");
            var before = Latin1Sample();
            File.WriteAllBytes(hostsPath, before);

            var writer = new HostsFileWriter(hostsPath, new BackupManager(Path.Combine(directory, "backups")));
            var doc = writer.Load();
            doc.AddEntry("127.0.0.1", new[] { "b.local" }, "smiley \U0001F600");

            var ex = Assert.Throws<HostsWriteException>(() => writer.Save());

            Assert.Contains("Latin-1", ex.Message);
            Assert.Equal(before, File.ReadAllBytes(hostsPath));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    // ---- pending markers --------------------------------------------------

    [Fact]
    public void ClearPendingMarkers_ResetsAnUndoneToggle()
    {
        var doc = Fixture.Parse();
        var entry = doc.Entries.First(e => e.PrimaryHostname == "ics.local");

        doc.Toggle(entry);
        doc.Toggle(entry);

        // Toggling back leaves the content clean but the line still flagged, which is
        // what made the window report an unsaved change that could not be saved.
        Assert.False(doc.IsDirty);
        Assert.Equal(1, doc.ModifiedCount);

        doc.ClearPendingMarkers();
        Assert.Equal(0, doc.ModifiedCount);
    }

    [Fact]
    public void ClearPendingMarkers_RefusesWhenContentActuallyDiffers()
    {
        var doc = Fixture.Parse();
        doc.Toggle(doc.Entries.First(e => e.PrimaryHostname == "ics.local"));

        Assert.Throws<InvalidOperationException>(() => doc.ClearPendingMarkers());
    }

    // ---- backup manifests describe the bytes they contain ------------------

    /// <summary>
    /// The manifest used to be built from whatever the caller passed alongside the bytes,
    /// so a backup holding the pre-save file was stamped with the post-edit entry count.
    /// </summary>
    [Fact]
    public void BackupTakenDuringSave_RecordsTheEntryCountOfTheFileItActuallyContains()
    {
        var hostsPath = Fixture.CopyToTemp(out var workDir);
        try
        {
            var backups = new BackupManager(Path.Combine(workDir, "backups"));
            var writer = new HostsFileWriter(hostsPath, backups);

            var doc = writer.Load();
            var countBeforeEdit = doc.Entries.Count();

            doc.AddEntry("127.0.0.1", new[] { "counted.local" });
            Assert.Equal(countBeforeEdit + 1, doc.Entries.Count());

            writer.Save();

            // The backup holds the file as it was *before* the save.
            var backup = backups.List().First(b => !b.IsOriginal);
            Assert.Equal(countBeforeEdit, backup.EntryCount);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// The pre-restore backup holds the current file, but used to be labelled with the
    /// encoding of the backup being restored — a different file entirely.
    /// </summary>
    [Fact]
    public void BackupTakenBeforeRestore_RecordsTheEncodingOfTheFileItActuallyContains()
    {
        var hostsPath = Fixture.CopyToTemp(out var workDir);
        try
        {
            var backups = new BackupManager(Path.Combine(workDir, "backups"));
            var writer = new HostsFileWriter(hostsPath, backups);
            writer.Load();

            // Deliberately a different format from the fixture's UTF-8 BOM + CRLF.
            var lfBytes = Encoding.UTF8.GetBytes("127.0.0.1 lf-no-bom.local\n");
            var lfBackup = backups.Create(lfBytes, "Handmade LF backup");
            Assert.Equal("UTF-8 · LF", lfBackup.Encoding);

            writer.Restore(lfBackup);

            var undoPoint = backups.List().First(b => b.Reason.StartsWith("Before restoring"));
            Assert.Equal("UTF-8 BOM · CRLF", undoPoint.Encoding);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch (IOException) { }
        }
    }

    // ---- restore is not blocked by the save path's drift check -------------

    /// <summary>
    /// Save refuses when the file changed underneath it, because it would overwrite those
    /// changes unknowingly. Restore overwrites deliberately and backs the current bytes up
    /// first, so refusing would only ever fire in the situation restore exists for.
    /// </summary>
    [Fact]
    public void Restore_StillWorksAfterAnotherToolRewroteTheFile()
    {
        var hostsPath = Fixture.CopyToTemp(out var workDir);
        try
        {
            var backups = new BackupManager(Path.Combine(workDir, "backups"));
            var writer = new HostsFileWriter(hostsPath, backups);
            writer.Load();

            var original = backups.List().Single(b => b.IsOriginal);
            var originalBytes = File.ReadAllBytes(original.FilePath);

            // Something else rewrites the file behind our back.
            File.WriteAllText(hostsPath, "127.0.0.1 written-by-docker.local\r\n");
            Assert.True(writer.HasExternalChange());

            var result = writer.Restore(original);

            Assert.True(result.Success);
            Assert.Equal(originalBytes, File.ReadAllBytes(hostsPath));
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>The drifted content is still captured, so the restore itself is undoable.</summary>
    [Fact]
    public void Restore_AfterExternalChange_StillCapturesWhatItOverwrote()
    {
        var hostsPath = Fixture.CopyToTemp(out var workDir);
        try
        {
            var backups = new BackupManager(Path.Combine(workDir, "backups"));
            var writer = new HostsFileWriter(hostsPath, backups);
            writer.Load();

            const string external = "127.0.0.1 written-by-docker.local\r\n";
            File.WriteAllText(hostsPath, external);

            writer.Restore(backups.List().Single(b => b.IsOriginal));

            var undoPoint = backups.List().First(b => b.Reason.StartsWith("Before restoring"));
            Assert.Equal(external, File.ReadAllText(undoPoint.FilePath));
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch (IOException) { }
        }
    }
}
