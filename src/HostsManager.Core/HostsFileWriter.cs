using System.Security.AccessControl;

namespace HostsManager.Core;

public sealed class HostsWriteException : Exception
{
    public HostsWriteException(string message, Exception? inner = null) : base(message, inner) { }
}
public sealed class HostsDriftException : Exception
{
    public HostsDriftException(string message) : base(message) { }
}
public sealed record SaveResult(bool Success, string Message, string? BackupPath = null, bool RolledBack = false);

public enum HostsWriteOperation { Save, Restore }

/// <summary>A restore must identify an existing, verified backup for this target.</summary>
public sealed record PreparedHostsWrite(
    string HostsPath,
    string BackupsRoot,
    byte[] Bytes,
    string ExpectedSha256,
    string BackupReason,
    HostsWriteOperation Operation = HostsWriteOperation.Save,
    string? RestoreBackupFileName = null,
    string? FailureAdvice = null)
{
    public bool RefuseOnDrift => Operation == HostsWriteOperation.Save;
}

public interface IHostsWriteCommitter
{
    SaveResult Commit(PreparedHostsWrite request);
}

public sealed class HostsFileWriter
{
    private readonly IReadOnlyList<ManagedSectionMarker> _markers;
    private readonly IHostsWriteCommitter? _committer;
    internal HostsFileOperations Files { get; set; } = new();

    public HostsFileWriter(string? hostsPath = null, BackupManager? backups = null,
        IReadOnlyList<ManagedSectionMarker>? markers = null, IHostsWriteCommitter? committer = null)
    {
        HostsPath = TargetIdentity.CanonicalPath(hostsPath ?? DefaultHostsPath);
        Backups = (backups ?? new BackupManager(hostsPath: HostsPath)).ForTarget(HostsPath);
        _markers = markers ?? ManagedSections.Known;
        _committer = committer;
    }

    public static string DefaultHostsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
    public string HostsPath { get; }
    public BackupManager Backups { get; }
    public HostsDocument? Document { get; private set; }
    public string LoadedSha256 { get; private set; } = "";
    public string? BackupWarning { get; private set; }

    public HostsDocument Load()
    {
        using var transaction = TargetWriteLock.Acquire(HostsPath);
        var bytes = Files.Read(HostsPath);
        var (text, format) = FileFormat.Decode(bytes);
        Document = HostsFileParser.Parse(text, format, _markers);
        LoadedSha256 = HostsDocument.Sha256(bytes);
        Backups.EnsureOriginal(bytes, out var warning);
        BackupWarning = warning;
        return Document;
    }

    public bool HasExternalChange() => !File.Exists(HostsPath)
        || HostsDocument.Sha256(Files.Read(HostsPath)) != LoadedSha256;

    public SaveResult Save(string reason = "Before save")
    {
        var doc = Document ?? throw new HostsWriteException("Load the hosts file before saving.");
        var rendered = doc.Render();
        HostsFileVerifier.Verify(doc, rendered);
        if (!doc.Format.CanRoundTrip(rendered))
            throw new HostsWriteException(
                $"This file is {doc.Format.Describe()} and cannot store one of the characters you entered. " +
                "The hosts file is unchanged. Remove any accented or non-Latin characters from your comments and try again.");

        var result = Commit(new PreparedHostsWrite(HostsPath, Backups.RootDirectory,
            doc.Format.Encode(rendered), LoadedSha256, reason,
            FailureAdvice: "If this repeats, check whether antivirus or Controlled Folder Access is blocking the hosts file."));
        doc.Commit();
        return result with { Message = "Saved." };
    }

    public SaveResult Restore(BackupEntry backup)
    {
        var bytes = Backups.ReadForRestore(backup);
        var result = Commit(new PreparedHostsWrite(HostsPath, Backups.RootDirectory, bytes, LoadedSha256,
            $"Before restoring {backup.FileName}", HostsWriteOperation.Restore, backup.FileName));
        Load();
        return result with { Message = $"Restored from {backup.FileName}." };
    }

    private SaveResult Commit(PreparedHostsWrite request)
    {
        var result = _committer is null ? CommitPrepared(request) : _committer.Commit(request);
        var written = HostsDocument.Sha256(Files.Read(HostsPath));
        if (!result.Success || written != HostsDocument.Sha256(request.Bytes))
            throw new HostsWriteException(
                $"The final hosts file could not be confirmed. Reload before editing. Recovery backup: {result.BackupPath}");
        LoadedSha256 = written;
        return result;
    }

    public SaveResult CommitPrepared(PreparedHostsWrite request)
    {
        if (!TargetIdentity.PathsEqual(request.HostsPath, HostsPath)
            || !TargetIdentity.PathsEqual(request.BackupsRoot, Backups.RootDirectory))
            throw new HostsWriteException("The prepared write does not match this writer's paths.");
        if (!Enum.IsDefined(request.Operation))
            throw new HostsWriteException("Unknown hosts write operation.");
        // Do not let a caller mutate the proposal while we validate or write it.
        request = request with { Bytes = request.Bytes.ToArray() };
        using var transaction = TargetWriteLock.Acquire(HostsPath);

        var (text, format) = FileFormat.Decode(request.Bytes);
        var candidate = HostsFileParser.Parse(text, format, _markers);
        var rendered = candidate.Render();
        HostsFileVerifier.Verify(candidate, rendered);
        if (!format.Encode(rendered).SequenceEqual(request.Bytes))
            throw new HostsWriteException("The prepared hosts content does not round-trip byte for byte.");

        var currentBytes = Files.Read(HostsPath);
        var currentHash = HostsDocument.Sha256(currentBytes);
        if (request.Operation == HostsWriteOperation.Save)
        {
            if (request.RestoreBackupFileName is not null)
                throw new HostsWriteException("A save cannot request restore semantics.");
            if (currentHash != request.ExpectedSha256)
                throw new HostsDriftException("The hosts file changed on disk since it was loaded. Reload before saving.");
            VerifyManagedSections(currentBytes, request.Bytes);
        }
        else
        {
            // A restore's exception to drift/managed preservation is tied to an actual
            // verified backup, not a caller-controlled 'skip validation' switch.
            var selected = Backups.List().SingleOrDefault(b =>
                string.Equals(b.FileName, request.RestoreBackupFileName, StringComparison.Ordinal));
            if (selected is null || !Backups.ReadForRestore(selected).SequenceEqual(request.Bytes))
                throw new HostsWriteException("Restore content must match a verified backup of this target.");
        }

        // Capture before any write. Reading ACLs is mandatory on Windows.
        var permissions = Files.CapturePermissions(HostsPath);
        if (OperatingSystem.IsWindows() && permissions is null)
            throw new HostsWriteException("Could not capture original permissions. The hosts file is unchanged.");

        BackupEntry backup;
        try
        {
            Backups.EnsureOriginal(currentBytes, out var originalError);
            if (originalError is not null) throw new IOException(originalError);
            backup = Backups.Create(currentBytes, request.BackupReason);
        }
        catch (Exception ex)
        {
            throw new HostsWriteException($"Could not write a backup to {Backups.Directory}. The hosts file is unchanged.", ex);
        }

        try
        {
            ReplaceFile(request.Bytes, currentHash, permissions);
            if (HostsDocument.Sha256(Files.Read(HostsPath)) != HostsDocument.Sha256(request.Bytes))
                throw new HostsWriteException("The hosts file does not match the intended write.");
            if (!Files.PermissionsMatch(HostsPath, permissions))
                throw new HostsWriteException("The final file permissions do not match the original.");
        }
        catch (HostsDriftException) { throw; } // A newer external file must never be rolled back.
        catch (Exception ex)
        {
            throw Recover(request, currentBytes, permissions, backup, ex);
        }

        LoadedSha256 = HostsDocument.Sha256(request.Bytes);
        return new SaveResult(true, "Done.", backup.FilePath);
    }

    private void VerifyManagedSections(byte[] before, byte[] after)
    {
        byte[] ManagedBytes(byte[] bytes)
        {
            var (text, format) = FileFormat.Decode(bytes);
            var document = HostsFileParser.Parse(text, format, _markers);
            var lines = document.Lines.Where(line => line.IsReadOnly).ToArray();
            return lines.Length == 0 ? Array.Empty<byte>()
                : format.Encode(string.Concat(lines.Select(line => line.Render() + line.Terminator)));
        }
        if (!ManagedBytes(before).SequenceEqual(ManagedBytes(after)))
            throw new HostsVerificationException(
                "An ordinary save cannot change, remove or reorder Docker/Tailscale sections. Use a deliberate backup restore instead.");
    }

    private void AssertUnchanged(string expected)
    {
        if (!File.Exists(HostsPath) || HostsDocument.Sha256(Files.Read(HostsPath)) != expected)
            throw new HostsDriftException("The hosts file changed during the write preparation. Nothing further was written. Reload before trying again.");
    }

    private void ReplaceFile(byte[] bytes, string expected, FileSecurity? permissions)
    {
        var directory = Path.GetDirectoryName(HostsPath)!;
        var id = Guid.NewGuid().ToString("N");
        var temp = Path.Combine(directory, $"hosts.hm.{id}.tmp");
        var previous = Path.Combine(directory, $"hosts.hm.{id}.prev");
        var completed = false;
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            // Apply before the swap: a fallback never exposes the temp's inherited ACL.
            Files.ApplyPermissions(temp, permissions);
            if (!Files.PermissionsMatch(temp, permissions))
                throw new HostsWriteException("Could not prepare the replacement with the original permissions.");

            AssertUnchanged(expected);
            try { Files.Replace(temp, HostsPath, previous); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // A partially completed Replace is not permission to overwrite whatever
                // is now there. Recheck before fallback, including for intentional restore.
                if (!File.Exists(temp)) throw;
                AssertUnchanged(expected);
                Files.Move(temp, HostsPath);
            }
            // ReplaceFile can merge inherited ACEs from the two files. Reapply the
            // captured DACL to our installed bytes before the final verification.
            // Leave a detected external replacement alone for recovery to report.
            if (HostsDocument.Sha256(Files.Read(HostsPath)) == HostsDocument.Sha256(bytes))
                Files.ApplyPermissions(HostsPath, permissions);
            completed = true;
        }
        finally
        {
            TryDelete(temp);
            // Preserve an OS recovery copy if Replace partially failed.
            if (completed) TryDelete(previous);
        }
    }

    private HostsWriteException Recover(PreparedHostsWrite request, byte[] original, FileSecurity? permissions,
        BackupEntry backup, Exception failure)
    {
        string outcome;
        try
        {
            var actual = HostsDocument.Sha256(Files.Read(HostsPath));
            var originalHash = HostsDocument.Sha256(original);
            if (actual == originalHash)
            {
                if (!Files.PermissionsMatch(HostsPath, permissions)) Files.ApplyPermissions(HostsPath, permissions);
                outcome = Files.PermissionsMatch(HostsPath, permissions)
                    ? "The previous contents and permissions are intact."
                    : "The previous contents remain, but their permissions could not be confirmed.";
            }
            else if (actual == HostsDocument.Sha256(request.Bytes))
            {
                ReplaceFile(original, actual, permissions);
                outcome = HostsDocument.Sha256(Files.Read(HostsPath)) == originalHash
                          && Files.PermissionsMatch(HostsPath, permissions)
                    ? "The previous contents and permissions were put back."
                    : "Rollback could not be verified; the final state is unknown.";
            }
            else
            {
                outcome = "The file changed again. It was not overwritten during recovery.";
            }
        }
        catch
        {
            outcome = "Recovery could not be verified; the final state is unknown.";
        }
        return new HostsWriteException(
            $"The write could not be verified. {outcome} Recovery backup: {backup.FilePath}. {request.FailureAdvice}", failure);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
