using HostsManager.Core;

namespace HostsManager.Tests;

/// <summary>
/// Exercises the save pipeline end to end against a throwaway copy of the real hosts
/// file. Nothing here touches the machine's actual hosts file.
/// </summary>
public class WriterTests : IDisposable
{
    private readonly string _hostsPath;
    private readonly string _workDir;
    private readonly BackupManager _backups;
    private readonly HostsFileWriter _writer;

    public WriterTests()
    {
        _hostsPath = Fixture.CopyToTemp(out _workDir);
        _backups = new BackupManager(Path.Combine(_workDir, "backups"));
        _writer = new HostsFileWriter(_hostsPath, _backups);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch (IOException) { }
    }

    private byte[] OnDisk => File.ReadAllBytes(_hostsPath);

    // ---- happy path -------------------------------------------------------

    [Fact]
    public void Save_WithNoChanges_LeavesTheFileByteIdentical()
    {
        var before = OnDisk;
        _writer.Load();

        var result = _writer.Save();

        Assert.True(result.Success);
        Assert.Equal(before, OnDisk);
    }

    [Fact]
    public void ToggleAndSave_ChangesOnlyThatLine()
    {
        var before = OnDisk;
        var doc = _writer.Load();

        doc.SetEnabled(doc.Entries.First(e => e.PrimaryHostname == "ics.local"), false);
        _writer.Save();

        var after = OnDisk;
        Assert.Equal(before.Length + 1, after.Length);

        var (text, _) = FileFormat.Decode(after);
        Assert.Contains("#127.0.0.1 ics.local", text);
        Assert.Contains("127.0.0.1 broker.local", text);
    }

    [Fact]
    public void ImportAndSave_UsesTheVerifiedWriterPipeline()
    {
        var doc = _writer.Load();
        var import = HostsImportReader.Parse(System.Text.Encoding.UTF8.GetBytes(
            "10.0.0.1 imported.local # from another file\r\n" +
            "#10.0.0.2 disabled.imported.local\r\n"));

        doc.ReplaceUserEntries(import.Entries);
        var result = _writer.Save("Imported entries");

        Assert.True(result.Success);
        var reloaded = _writer.Load();
        Assert.Contains(reloaded.Entries, line => line.PrimaryHostname == "imported.local" && line.IsEnabled);
        Assert.Contains(reloaded.Entries, line => line.PrimaryHostname == "disabled.imported.local" && !line.IsEnabled);
        Assert.Contains(reloaded.Entries, line => line.ManagedBy == "Docker");
        Assert.Contains(reloaded.Entries, line => line.ManagedBy == "Tailscale");
        Assert.NotEmpty(_backups.List());
    }

    [Fact]
    public void ToggleOffAndBackOn_RestoresTheOriginalBytes()
    {
        var before = OnDisk;

        var doc = _writer.Load();
        doc.SetEnabled(doc.Entries.First(e => e.PrimaryHostname == "ics.local"), false);
        _writer.Save();
        Assert.NotEqual(before, OnDisk);

        var reloaded = _writer.Load();
        reloaded.SetEnabled(reloaded.Entries.First(e => e.PrimaryHostname == "ics.local"), true);
        _writer.Save();

        Assert.Equal(before, OnDisk);
    }

    [Fact]
    public void ToggleSaveToggleSave_AcrossTwoSaves_RestoresTheOriginalBytes()
    {
        // The file writes this entry as "#\t127.0.0.1       localhost". Re-parsing after a
        // save would forget the tab in the disable prefix and write back a plain "#",
        // reformatting a line the user only toggled.
        var before = OnDisk;
        var doc = _writer.Load();

        var entry = doc.Entries.First(e => e.PrimaryHostname == "localhost" && !e.IsEnabled);

        doc.SetEnabled(entry, true);
        _writer.Save();
        Assert.Equal(before.Length - 2, OnDisk.Length);

        doc.SetEnabled(entry, false);
        _writer.Save();

        Assert.Equal(before, OnDisk);
    }

    [Fact]
    public void Save_ClearsPendingStateWithoutReparsing()
    {
        var doc = _writer.Load();
        var entry = doc.Entries.First(e => e.PrimaryHostname == "ics.local");

        doc.SetEnabled(entry, false);
        Assert.True(doc.IsDirty);
        Assert.Equal(1, doc.ModifiedCount);

        _writer.Save();

        Assert.False(doc.IsDirty);
        Assert.Equal(0, doc.ModifiedCount);
        Assert.Same(doc, _writer.Document);
    }

    [Fact]
    public void Save_PreservesBomAndLineEndings()
    {
        var doc = _writer.Load();
        doc.AddEntry("127.0.0.1", new[] { "new.local" });
        _writer.Save();

        var bytes = OnDisk;
        Assert.True(FileFormat.StartsWithBom(bytes));

        var (text, format) = FileFormat.Decode(bytes);
        Assert.Equal("\r\n", format.NewLine);
        Assert.DoesNotContain(text.Replace("\r\n", ""), c => c == '\n' || c == '\r');
    }

    [Fact]
    public void Save_LeavesManagedBlocksByteIdentical()
    {
        var (beforeText, _) = FileFormat.Decode(OnDisk);
        var dockerBefore = ExtractBlock(beforeText, "# Added by Docker Desktop", "# End of section");
        var tailscaleBefore = ExtractBlock(beforeText, "# TailscaleHostsSectionStart", "# TailscaleHostsSectionEnd");

        var doc = _writer.Load();
        doc.AddEntry("127.0.0.1", new[] { "new.local" });
        foreach (var junk in doc.UnparseableLines.ToList()) doc.Remove(junk);
        foreach (var entry in doc.Entries.Where(e => !e.IsReadOnly).ToList()) doc.Toggle(entry);
        _writer.Save();

        var (afterText, _) = FileFormat.Decode(OnDisk);

        Assert.Equal(dockerBefore, ExtractBlock(afterText, "# Added by Docker Desktop", "# End of section"));
        Assert.Equal(tailscaleBefore, ExtractBlock(afterText, "# TailscaleHostsSectionStart", "# TailscaleHostsSectionEnd"));
    }

    [Fact]
    public void RemovingJunkAndSaving_KeepsEveryOtherLine()
    {
        var doc = _writer.Load();
        var entriesBefore = doc.Entries.Select(e => e.PrimaryHostname).ToList();

        foreach (var junk in doc.UnparseableLines.ToList()) doc.Remove(junk);
        _writer.Save();

        var reloaded = _writer.Load();
        Assert.Empty(reloaded.UnparseableLines);
        Assert.Equal(entriesBefore, reloaded.Entries.Select(e => e.PrimaryHostname).ToList());
    }

    // ---- the gates --------------------------------------------------------

    [Fact]
    public void Save_AbortsWhenTheFileChangedOnDisk()
    {
        var doc = _writer.Load();
        doc.AddEntry("127.0.0.1", new[] { "mine.local" });

        // Simulate Docker or Tailscale rewriting the file underneath us.
        File.AppendAllText(_hostsPath, "127.0.0.1 someone-else.local\r\n");
        var afterExternalWrite = OnDisk;

        Assert.Throws<HostsDriftException>(() => _writer.Save());
        Assert.Equal(afterExternalWrite, OnDisk);
    }

    [Fact]
    public void HasExternalChange_DetectsAnOutsideWrite()
    {
        _writer.Load();
        Assert.False(_writer.HasExternalChange());

        File.AppendAllText(_hostsPath, "127.0.0.1 outside.local\r\n");
        Assert.True(_writer.HasExternalChange());
    }

    [Fact]
    public void Save_AbortsWhenTheBackupCannotBeWritten()
    {
        var before = OnDisk;

        // Point backups at a path that cannot be a directory, so creating one fails.
        var blocked = Path.Combine(_workDir, "blocked");
        File.WriteAllText(blocked, "not a directory");

        var writer = new HostsFileWriter(_hostsPath, new BackupManager(Path.Combine(blocked, "backups")));

        // Load stays read-only and survives, but warns that saving will not be possible.
        var doc = writer.Load();
        Assert.NotNull(writer.BackupWarning);

        doc.AddEntry("127.0.0.1", new[] { "never.local" });

        var ex = Assert.Throws<HostsWriteException>(() => writer.Save());

        Assert.Contains("backup", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, OnDisk);
    }

    [Fact]
    public void Save_RefusesToModifyAManagedLine()
    {
        var doc = _writer.Load();
        var managed = doc.Entries.First(e => e.IsReadOnly);

        Assert.Throws<InvalidOperationException>(() => doc.Toggle(managed));
    }

    [Fact]
    public void Verifier_RejectsAModelThatModifiesAManagedLine()
    {
        var doc = _writer.Load();

        // Bypass the guard the way only a bug could — disable a managed line without going
        // through the mutation that refuses it — and confirm the gate still catches it.
        var managed = doc.Lines.First(l => l.IsReadOnly && l.Kind == LineKind.Entry);
        typeof(HostsLine).GetProperty(nameof(HostsLine.Kind))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(managed, new object[] { LineKind.DisabledEntry });

        Assert.True(managed.IsModified);

        Assert.Throws<HostsVerificationException>(() => HostsFileVerifier.Verify(doc, doc.Render()));
    }

    [Fact]
    public void Verifier_RejectsAnEmptyRender()
    {
        var doc = _writer.Load();
        Assert.Throws<HostsVerificationException>(() => HostsFileVerifier.Verify(doc, ""));
    }

    [Fact]
    public void Verifier_RejectsARenderThatLosesLines()
    {
        var doc = _writer.Load();
        Assert.Throws<HostsVerificationException>(() => HostsFileVerifier.Verify(doc, "127.0.0.1 only.local\r\n"));
    }

    [Fact]
    public void Verifier_RejectsANullByte()
    {
        var doc = HostsFileParser.Parse("127.0.0.1 a.local\r\n", FileFormat.Default);
        Assert.Throws<HostsVerificationException>(() => HostsFileVerifier.Verify(doc, "127.0.0.1 a.\0local\r\n"));
    }

    [Fact]
    public void Verifier_RejectsALostTrailingNewline()
    {
        var doc = HostsFileParser.Parse("127.0.0.1 a.local\r\n", FileFormat.Default);
        Assert.Throws<HostsVerificationException>(() => HostsFileVerifier.Verify(doc, "127.0.0.1 a.local"));
    }

    // ---- backups and restore ---------------------------------------------

    [Fact]
    public void Load_CapturesAPristineOriginalExactlyOnce()
    {
        var before = OnDisk;

        _writer.Load();
        Assert.True(_backups.HasOriginal);
        Assert.Equal(before, File.ReadAllBytes(_backups.OriginalPath));

        var doc = _writer.Load();
        doc.AddEntry("127.0.0.1", new[] { "later.local" });
        _writer.Save();
        _writer.Load();

        // The original must still be the pre-app state, not the latest save.
        Assert.Equal(before, File.ReadAllBytes(_backups.OriginalPath));
    }

    [Fact]
    public void EverySave_TakesABackupOfWhatWasThereBefore()
    {
        var before = OnDisk;
        var doc = _writer.Load();
        doc.AddEntry("127.0.0.1", new[] { "backed-up.local" });

        var result = _writer.Save();

        Assert.NotNull(result.BackupPath);
        Assert.Equal(before, File.ReadAllBytes(result.BackupPath!));
    }

    [Fact]
    public void Restore_ReturnsTheFileToTheBackedUpBytes()
    {
        var before = OnDisk;

        var doc = _writer.Load();
        doc.AddEntry("127.0.0.1", new[] { "temporary.local" });
        foreach (var junk in doc.UnparseableLines.ToList()) doc.Remove(junk);
        _writer.Save();
        Assert.NotEqual(before, OnDisk);

        var original = _backups.List().Single(b => b.IsOriginal);
        _writer.Restore(original);

        Assert.Equal(before, OnDisk);
    }

    [Fact]
    public void Restore_BacksUpTheCurrentStateFirstSoItIsUndoable()
    {
        var doc = _writer.Load();
        doc.AddEntry("127.0.0.1", new[] { "undo-me.local" });
        _writer.Save();
        var afterEdit = OnDisk;

        _writer.Restore(_backups.List().Single(b => b.IsOriginal));

        var undoPoint = _backups.List().First(b => b.Reason.StartsWith("Before restoring"));
        Assert.Equal(afterEdit, File.ReadAllBytes(undoPoint.FilePath));
    }

    [Fact]
    public void Restore_RejectsATamperedBackup()
    {
        _writer.Load();
        var original = _backups.List().Single(b => b.IsOriginal);

        File.AppendAllText(original.FilePath, "127.0.0.1 tampered.local\r\n");

        Assert.Throws<HostsWriteException>(() => _writer.Restore(original));
    }

    [Fact]
    public void BackupManifests_RecordWhatHappened()
    {
        var doc = _writer.Load();
        doc.AddEntry("127.0.0.1", new[] { "manifest.local" });
        _writer.Save("Toggled 1 entry");

        var latest = _backups.List().First(b => !b.IsOriginal);

        Assert.Equal("Toggled 1 entry", latest.Reason);
        Assert.Equal("UTF-8 BOM · CRLF", latest.Encoding);
        Assert.True(latest.EntryCount > 0);
        Assert.True(_backups.Verify(latest));
    }

    [Fact]
    public void Retention_PrunesOldestFirstAndKeepsTheOriginal()
    {
        var backups = new BackupManager(Path.Combine(_workDir, "retained"), retention: 3);
        var writer = new HostsFileWriter(_hostsPath, backups);
        writer.Load();

        for (var i = 0; i < 6; i++)
        {
            var doc = writer.Load();
            doc.AddEntry("127.0.0.1", new[] { $"host{i}.local" });
            writer.Save($"Save {i}");
        }

        var all = backups.List();
        Assert.Equal(3, all.Count(b => !b.IsOriginal));
        Assert.Single(all, b => b.IsOriginal);
        Assert.True(File.Exists(backups.OriginalPath));
    }

    [Fact]
    public void BackupsSurviveAMissingManifest()
    {
        var doc = _writer.Load();
        doc.AddEntry("127.0.0.1", new[] { "orphan.local" });
        var result = _writer.Save();

        File.Delete(result.BackupPath + ".json");

        var rebuilt = _backups.List().First(b => b.FilePath == result.BackupPath);
        Assert.Equal("Unknown", rebuilt.Reason);
        Assert.True(_backups.Verify(rebuilt));
    }

    // ---- split-elevation handoff -----------------------------------------

    [Fact]
    public void Save_HandsVerifiedBytesAndLoadedHashToTheConfiguredCommitter()
    {
        var before = OnDisk;
        var committer = new RecordingCommitter();
        var writer = new HostsFileWriter(_hostsPath, _backups, committer: committer);
        var doc = writer.Load();
        doc.AddEntry("127.0.0.1", new[] { "elevated-handoff.local" });

        var result = writer.Save("Elevated test save");

        Assert.True(result.Success);
        Assert.NotNull(committer.Request);
        Assert.Equal(_hostsPath, committer.Request.HostsPath);
        Assert.Equal(_backups.Directory, committer.Request.BackupsDirectory);
        Assert.Equal(HostsDocument.Sha256(before), committer.Request.ExpectedSha256);
        Assert.Equal("Elevated test save", committer.Request.BackupReason);
        Assert.True(committer.Request.RefuseOnDrift);
        Assert.Contains("elevated-handoff.local", File.ReadAllText(_hostsPath));
        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void CommitPrepared_RechecksDriftAgainstTheHashFromTheUiProcess()
    {
        var doc = _writer.Load();
        doc.AddEntry("127.0.0.1", new[] { "intended.local" });
        var intended = doc.Format.Encode(doc.Render());
        var request = new PreparedHostsWrite(
            _hostsPath, _backups.Directory, intended, _writer.LoadedSha256,
            "Prepared save", RefuseOnDrift: true);

        const string external = "127.0.0.1 external-change.local\r\n";
        File.WriteAllText(_hostsPath, external);

        var helperWriter = new HostsFileWriter(_hostsPath, _backups);
        Assert.Throws<HostsDriftException>(() => helperWriter.CommitPrepared(request));
        Assert.Equal(external, File.ReadAllText(_hostsPath));
    }

    [Fact]
    public void CommitPrepared_RevalidatesHandoffBytesBeforeWriting()
    {
        _writer.Load();
        var before = OnDisk;
        var tampered = System.Text.Encoding.UTF8.GetBytes("127.0.0.1 bad\0host.local\r\n");
        var request = new PreparedHostsWrite(
            _hostsPath, _backups.Directory, tampered, _writer.LoadedSha256,
            "Tampered handoff", RefuseOnDrift: true);

        var helperWriter = new HostsFileWriter(_hostsPath, _backups);
        Assert.Throws<HostsVerificationException>(() => helperWriter.CommitPrepared(request));
        Assert.Equal(before, OnDisk);
    }

    private sealed class RecordingCommitter : IHostsWriteCommitter
    {
        public PreparedHostsWrite? Request { get; private set; }

        public SaveResult Commit(PreparedHostsWrite request)
        {
            Request = request;
            File.WriteAllBytes(request.HostsPath, request.Bytes);
            return new SaveResult(true, "Saved by test committer.");
        }
    }

    private static string ExtractBlock(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        var to = text.IndexOf(end, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from, $"Block {start} not found.");
        return text[from..(to + end.Length)];
    }
}
