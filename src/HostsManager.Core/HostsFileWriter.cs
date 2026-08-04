namespace HostsManager.Core;

public sealed class HostsWriteException : Exception
{
    public HostsWriteException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Raised when the file on disk changed since it was loaded.</summary>
public sealed class HostsDriftException : Exception
{
    public HostsDriftException(string message) : base(message) { }
}

public sealed record SaveResult(bool Success, string Message, string? BackupPath = null, bool RolledBack = false);

/// <summary>
/// Loads and saves the hosts file. Every save runs the same gated pipeline:
/// drift check, render, verify, backup, atomic replace, read back. Any failure aborts
/// before the file changes, or rolls back if it changed already.
/// </summary>
public sealed class HostsFileWriter
{
    private readonly IReadOnlyList<ManagedSectionMarker> _markers;

    public HostsFileWriter(string? hostsPath = null, BackupManager? backups = null,
        IReadOnlyList<ManagedSectionMarker>? markers = null)
    {
        HostsPath = hostsPath ?? DefaultHostsPath;
        Backups = backups ?? new BackupManager();
        _markers = markers ?? ManagedSections.Known;
    }

    public static string DefaultHostsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    public string HostsPath { get; }

    public BackupManager Backups { get; }

    public HostsDocument? Document { get; private set; }

    /// <summary>Hash of the file as it was when loaded, used to detect external edits.</summary>
    public string LoadedSha256 { get; private set; } = "";

    public HostsDocument Load()
    {
        if (!File.Exists(HostsPath))
            throw new HostsWriteException($"Hosts file not found at {HostsPath}.");

        var bytes = File.ReadAllBytes(HostsPath);
        var (text, format) = FileFormat.Decode(bytes);

        Document = HostsFileParser.Parse(text, format, _markers);
        LoadedSha256 = HostsDocument.Sha256(bytes);

        Backups.EnsureOriginal(bytes, Document.Entries.Count(), format, out var backupError);
        BackupWarning = backupError;

        return Document;
    }

    /// <summary>
    /// Set when the backup directory could not be written during load. Saving will fail
    /// for the same reason, so the UI should surface this before the user edits anything.
    /// </summary>
    public string? BackupWarning { get; private set; }

    /// <summary>True if another process (Docker, Tailscale, an editor) rewrote the file since load.</summary>
    public bool HasExternalChange()
    {
        if (!File.Exists(HostsPath)) return true;
        return HostsDocument.Sha256(File.ReadAllBytes(HostsPath)) != LoadedSha256;
    }

    public SaveResult Save(string reason = "Before save")
    {
        var doc = Document ?? throw new HostsWriteException("Load the hosts file before saving.");

        // 1. Drift check — never clobber changes another tool made since we loaded.
        var currentBytes = File.Exists(HostsPath) ? File.ReadAllBytes(HostsPath) : null;
        if (currentBytes is null)
            throw new HostsWriteException($"Hosts file not found at {HostsPath}.");

        if (HostsDocument.Sha256(currentBytes) != LoadedSha256)
            throw new HostsDriftException(
                "The hosts file changed on disk since it was loaded. Reload before saving so those changes aren't lost.");

        // 2 & 3 & 4. Render, then prove the render survives a parse unchanged.
        var rendered = doc.Render();
        HostsFileVerifier.Verify(doc, rendered);

        // Encoding is the one step the round-trip check above cannot see, because it
        // works on text while this turns text back into bytes. A character the file's
        // codec cannot represent would be written as '?' and silently lose data.
        if (!doc.Format.CanRoundTrip(rendered))
            throw new HostsWriteException(
                $"This file is {doc.Format.Describe()} and cannot store one of the characters you entered. " +
                "The hosts file is unchanged. Remove any accented or non-Latin characters from your comments and try again.");

        var newBytes = doc.Format.Encode(rendered);

        // 5. Back up. A save that cannot be undone does not happen.
        BackupEntry backup;
        try
        {
            backup = Backups.Create(currentBytes, reason, doc.Entries.Count(), doc.Format);
        }
        catch (Exception ex)
        {
            throw new HostsWriteException(
                $"Could not write a backup to {Backups.Directory}, so the save was cancelled. The hosts file is unchanged.", ex);
        }

        // 6. Atomic replace.
        try
        {
            ReplaceFile(newBytes);
        }
        catch (Exception ex)
        {
            var rolledBack = TryRestore(currentBytes);
            throw new HostsWriteException(
                $"Writing the hosts file failed. {(rolledBack ? "The original was restored." : $"Restore from {backup.FilePath}.")} " +
                "If this repeats, check whether antivirus or Controlled Folder Access is blocking writes to the hosts file.", ex);
        }

        // 7. Read back and confirm the bytes on disk are the bytes we meant to write.
        var writtenSha = HostsDocument.Sha256(File.ReadAllBytes(HostsPath));
        var expectedSha = HostsDocument.Sha256(newBytes);
        if (writtenSha != expectedSha)
        {
            var rolledBack = TryRestore(currentBytes);
            throw new HostsWriteException(
                $"The hosts file on disk does not match what was written. {(rolledBack ? "The original was restored." : $"Restore from {backup.FilePath}.")}");
        }

        LoadedSha256 = writtenSha;
        doc.Commit();

        return new SaveResult(true, "Saved.", backup.FilePath);
    }

    /// <summary>Restores a backup through the same verified pipeline rather than a raw copy.</summary>
    public SaveResult Restore(BackupEntry backup)
    {
        if (!Backups.Verify(backup))
            throw new HostsWriteException($"Backup {backup.FileName} is missing or its contents no longer match its recorded hash.");

        var restoreBytes = Backups.Read(backup);
        var (text, format) = FileFormat.Decode(restoreBytes);

        // Parse it so a corrupted backup can't be pushed onto the live file unchecked.
        var candidate = HostsFileParser.Parse(text, format, _markers);
        HostsFileVerifier.Verify(candidate, candidate.Render());

        var currentBytes = File.ReadAllBytes(HostsPath);
        Backups.Create(currentBytes, $"Before restoring {backup.FileName}", Document?.Entries.Count() ?? 0, format);

        try
        {
            ReplaceFile(restoreBytes);
        }
        catch (Exception ex)
        {
            TryRestore(currentBytes);
            throw new HostsWriteException("Restore failed. The hosts file is unchanged.", ex);
        }

        Load();
        return new SaveResult(true, $"Restored from {backup.FileName}.", backup.FilePath);
    }

    // ---- internals -------------------------------------------------------

    /// <summary>
    /// Writes to a sibling temp file, flushes it to the physical disk, then swaps it in
    /// with <see cref="File.Replace(string,string,string)"/> — atomic on NTFS, and it
    /// carries over the destination's ACLs, which matters for a file owned by
    /// BUILTIN\Administrators.
    /// </summary>
    private void ReplaceFile(byte[] bytes)
    {
        var directory = Path.GetDirectoryName(HostsPath)!;

        // Process-scoped names: the restore flags deliberately bypass the single-instance
        // guard, so a recovery run can overlap a save from the open window. Sharing one
        // temp name would make whichever got there second fail on a locked file.
        var id = Environment.ProcessId;
        var temp = Path.Combine(directory, $"hosts.hm.{id}.tmp");
        var osBackup = Path.Combine(directory, $"hosts.hm.{id}.prev");

        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Replace(temp, HostsPath, osBackup, ignoreMetadataErrors: true);
            }
            catch (Exception) when (File.Exists(temp))
            {
                // Some filesystems and security products refuse Replace; a move is still
                // better than truncating the destination and writing into it.
                File.Move(temp, HostsPath, overwrite: true);
            }
        }
        finally
        {
            TryDelete(temp);
            TryDelete(osBackup);
        }
    }

    private bool TryRestore(byte[] originalBytes)
    {
        try
        {
            ReplaceFile(originalBytes);
            return HostsDocument.Sha256(File.ReadAllBytes(HostsPath)) == HostsDocument.Sha256(originalBytes);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
