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

        Backups.EnsureOriginal(bytes, out var backupError);
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

        // Render, then prove the render survives a parse unchanged.
        var rendered = doc.Render();
        HostsFileVerifier.Verify(doc, rendered);

        // Encoding is the one step the round-trip check above cannot see, because it
        // works on text while this turns text back into bytes. A character the file's
        // codec cannot represent would be written as '?' and silently lose data.
        if (!doc.Format.CanRoundTrip(rendered))
            throw new HostsWriteException(
                $"This file is {doc.Format.Describe()} and cannot store one of the characters you entered. " +
                "The hosts file is unchanged. Remove any accented or non-Latin characters from your comments and try again.");

        // A save writes the whole file from a model built at load time, so an edit another
        // tool made since then would be silently overwritten. Refusing is the safe answer.
        var result = Commit(doc.Format.Encode(rendered), reason, refuseOnDrift: true,
            failureAdvice: "If this repeats, check whether antivirus or Controlled Folder Access " +
                           "is blocking writes to the hosts file.");

        doc.Commit();
        return result with { Message = "Saved." };
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

        // Deliberately does NOT refuse on drift, unlike a save. Overwriting whatever is
        // currently there is the entire point of a restore, the user asked for it
        // explicitly, and Commit backs the current bytes up first either way — so nothing
        // is lost. Refusing here would only ever fire in the situation restore exists for.
        var result = Commit(restoreBytes, $"Before restoring {backup.FileName}", refuseOnDrift: false);

        Load();
        return result with { Message = $"Restored from {backup.FileName}.", BackupPath = backup.FilePath };
    }

    // ---- the one write path ----------------------------------------------

    /// <summary>
    /// The only code that ever writes the hosts file. Save and restore both go through it
    /// so they cannot drift apart on which safety gates they run: back up, replace
    /// atomically, then read back and prove the bytes on disk are the bytes intended. Any
    /// failure after the file was touched rolls back, and says so honestly if the rollback
    /// itself failed.
    /// </summary>
    private SaveResult Commit(byte[] newBytes, string backupReason, bool refuseOnDrift,
        string? failureAdvice = null)
    {
        if (!File.Exists(HostsPath))
            throw new HostsWriteException($"Hosts file not found at {HostsPath}.");

        var currentBytes = File.ReadAllBytes(HostsPath);

        if (refuseOnDrift && HostsDocument.Sha256(currentBytes) != LoadedSha256)
            throw new HostsDriftException(
                "The hosts file changed on disk since it was loaded. Reload before saving so those changes aren't lost.");

        // A change that cannot be undone does not happen.
        BackupEntry backup;
        try
        {
            backup = Backups.Create(currentBytes, backupReason);
        }
        catch (Exception ex)
        {
            throw new HostsWriteException(
                $"Could not write a backup to {Backups.Directory}, so nothing was changed. The hosts file is unchanged.", ex);
        }

        try
        {
            ReplaceFile(newBytes);
        }
        catch (Exception ex)
        {
            throw RolledBack("Writing the hosts file failed.", currentBytes, backup, failureAdvice, ex);
        }

        var writtenSha = HostsDocument.Sha256(File.ReadAllBytes(HostsPath));
        if (writtenSha != HostsDocument.Sha256(newBytes))
            throw RolledBack("The hosts file on disk does not match what was written.",
                currentBytes, backup, failureAdvice);

        LoadedSha256 = writtenSha;
        return new SaveResult(true, "Done.", backup.FilePath);
    }

    /// <summary>
    /// Attempts a rollback and builds the exception describing what actually happened.
    /// Reports the rollback's real outcome rather than asserting the file is untouched:
    /// if putting the old bytes back also failed, the file is in an unknown state and
    /// saying otherwise would send the user away from the one thing that can fix it.
    /// </summary>
    private HostsWriteException RolledBack(string problem, byte[] originalBytes,
        BackupEntry backup, string? advice, Exception? inner = null)
    {
        var outcome = TryRestore(originalBytes)
            ? "The previous contents were put back."
            : $"Rolling back ALSO failed, so the hosts file may be incomplete. Restore it from " +
              $"{backup.FilePath}, or run: HostsManager.exe --restore-latest";

        var message = advice is null ? $"{problem} {outcome}" : $"{problem} {outcome} {advice}";
        return new HostsWriteException(message, inner);
    }

    // ---- internals -------------------------------------------------------

    /// <summary>
    /// Writes to a sibling temp file, flushes it to the physical disk, then swaps it in
    /// with <see cref="File.Replace(string,string,string)"/> — atomic on NTFS, and it
    /// carries over the destination's ACLs, which matters for a file owned by
    /// BUILTIN\Administrators.
    /// <para>
    /// Some filesystems and security products refuse <c>Replace</c> outright. The fallback
    /// is a move, which is <em>not</em> equivalent: a move keeps the source file's
    /// permissions, so the hosts file would silently inherit whatever the temp file picked
    /// up from its directory. The destination's ACL is therefore captured up front and
    /// reapplied afterwards, and a failure to reapply it is raised rather than swallowed —
    /// quietly loosening the permissions on a file in System32 is not an acceptable
    /// outcome of a successful-looking save.
    /// </para>
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or PlatformNotSupportedException && File.Exists(temp))
            {
                MovePreservingPermissions(temp, HostsPath);
            }
        }
        finally
        {
            TryDelete(temp);
            TryDelete(osBackup);
        }
    }

    /// <summary>
    /// The <c>File.Replace</c> fallback. Carries the destination's existing access rules
    /// across the move so the swap keeps the guarantee <c>Replace</c> would have given.
    /// </summary>
    private static void MovePreservingPermissions(string temp, string destination)
    {
        var original = TryReadAccessRules(destination);

        File.Move(temp, destination, overwrite: true);

        if (original is null) return;

        try
        {
            if (OperatingSystem.IsWindows()) new FileInfo(destination).SetAccessControl(original);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                      or InvalidOperationException)
        {
            throw new HostsWriteException(
                $"The hosts file was written, but its original permissions could not be restored " +
                $"afterwards, so it may now be more permissive than Windows shipped it. Check the " +
                $"Security tab of {destination}.", ex);
        }
    }

    private static System.Security.AccessControl.FileSecurity? TryReadAccessRules(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return null;

        try
        {
            return new FileInfo(path).GetAccessControl(
                System.Security.AccessControl.AccessControlSections.Access);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException
                                      or InvalidOperationException)
        {
            // Can't read them, so there is nothing to reapply; the move still happens and
            // the file keeps whatever the temp file inherited from the etc directory.
            return null;
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
