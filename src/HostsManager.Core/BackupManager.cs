using System.Text.Json;
using System.Text.Json.Serialization;

namespace HostsManager.Core;

public sealed record BackupEntry
{
    public required string FilePath { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required long Bytes { get; init; }
    public required string Sha256 { get; init; }
    public required string Reason { get; init; }
    public int EntryCount { get; init; }
    public string? Encoding { get; init; }
    public bool IsOriginal { get; init; }
    public string? TargetPath { get; init; }
    public bool HasRecordedHash { get; init; } = true;

    [JsonIgnore]
    public string FileName => Path.GetFileName(FilePath);
}

/// <summary>Runs backup I/O as its owner, including when the writer is elevated.</summary>
public interface IBackupFileAccess
{
    T Run<T>(Func<T> operation);
}

/// <summary>
/// Each canonical target has a separate directory under the configured backup root.
/// Older unscoped backups remain in the root; they are never silently adopted.
/// </summary>
public sealed class BackupManager
{
    public const int DefaultRetention = 50;
    public const string OriginalFileName = "hosts.original.bak";
    private readonly int _retention;
    private readonly IBackupFileAccess? _access;

    public BackupManager(string? directory = null, int retention = DefaultRetention,
        string? hostsPath = null, IBackupFileAccess? access = null)
    {
        if (retention < 1) throw new ArgumentOutOfRangeException(nameof(retention));
        RootDirectory = Path.GetFullPath(directory ?? DefaultDirectory);
        TargetPath = TargetIdentity.CanonicalPath(hostsPath ?? HostsFileWriter.DefaultHostsPath);
        Directory = Path.Combine(RootDirectory, "targets", TargetIdentity.Key(TargetPath));
        _retention = retention;
        _access = access;
    }

    public string RootDirectory { get; }
    public string TargetPath { get; }
    public string Directory { get; }
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HostsManager", "backups");
    public string OriginalPath => Path.Combine(Directory, OriginalFileName);
    public bool HasOriginal => Run(() => File.Exists(OriginalPath));

    public BackupManager ForTarget(string path) => TargetIdentity.PathsEqual(TargetPath, TargetIdentity.CanonicalPath(path))
        ? this : new BackupManager(RootDirectory, _retention, path, _access);

    private T Run<T>(Func<T> operation) => _access is null ? operation() : _access.Run(operation);

    public BackupEntry? EnsureOriginal(byte[] bytes, out string? error)
    {
        error = null;
        try
        {
            return Run<BackupEntry?>(() =>
            {
                System.IO.Directory.CreateDirectory(Directory);
                if (File.Exists(OriginalPath)) return null;
                try { return Write(OriginalPath, bytes, "Original file, captured on first run", true); }
                catch (BackupNameCollisionException) { return null; }
            });
        }
        catch (Exception ex)
        {
            error = $"Could not create the original backup in {Directory}: {ex.Message}";
            return null;
        }
    }

    public BackupEntry Create(byte[] bytes, string reason) => Run(() =>
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory, $"hosts.{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}.{Guid.NewGuid():N}.bak");
        var entry = Write(path, bytes, reason, false);
        PruneCore();
        return entry;
    });

    public IReadOnlyList<BackupEntry> List() => Run<IReadOnlyList<BackupEntry>>(() => ListCore(Directory));

    /// <summary>Unscoped history is exposed for inspection, never for automatic restore.</summary>
    public IReadOnlyList<BackupEntry> ListLegacy() => Run<IReadOnlyList<BackupEntry>>(() =>
        ListCore(RootDirectory).Select(entry => entry with { TargetPath = null }).ToArray());

    public bool CanRestore(BackupEntry entry) =>
        entry.HasRecordedHash && entry.TargetPath is not null
        && TargetIdentity.PathsEqual(entry.TargetPath, TargetPath)
        && TargetIdentity.PathsEqual(Path.GetDirectoryName(Path.GetFullPath(entry.FilePath))!, Directory);

    public byte[] Read(BackupEntry entry) => Run(() => File.ReadAllBytes(entry.FilePath));

    public bool Verify(BackupEntry entry) => Run(() =>
        entry.HasRecordedHash && File.Exists(entry.FilePath)
        && HostsDocument.Sha256(File.ReadAllBytes(entry.FilePath)) == entry.Sha256);

    /// <summary>Use current disk metadata and verify the exact buffer returned to restore.</summary>
    public byte[] ReadForRestore(BackupEntry entry) => Run(() =>
    {
        if (!CanRestore(entry))
            throw new HostsWriteException("This backup does not have verified provenance for this target. " +
                "Inspect it and use Import entries for an intentional import; automatic restore was refused.");
        var current = ReadManifest(entry.FilePath);
        if (current is null || !CanRestore(current) || current.Sha256 != entry.Sha256)
            throw new HostsWriteException("The backup metadata changed or is unavailable. Reload the backup list.");
        var bytes = File.ReadAllBytes(entry.FilePath);
        if (HostsDocument.Sha256(bytes) != current.Sha256)
            throw new HostsWriteException($"Backup {entry.FileName} no longer matches its recorded hash.");
        return bytes;
    });

    public void Prune() => Run(() => { PruneCore(); return true; });

    private void PruneCore()
    {
        // Unknown or mismatched provenance is retained for manual inspection.
        foreach (var stale in ListCore(Directory).Where(e => !e.IsOriginal && CanRestore(e)).Skip(_retention))
        {
            TryDelete(stale.FilePath);
            TryDelete(ManifestPath(stale.FilePath));
        }
    }

    private static List<BackupEntry> ListCore(string directory)
    {
        if (!System.IO.Directory.Exists(directory)) return new();
        return System.IO.Directory.GetFiles(directory, "*.bak")
            .Select(file => ReadManifest(file) ?? Reconstruct(file))
            .OrderByDescending(e => e.IsOriginal ? DateTimeOffset.MinValue : e.Timestamp).ToList();
    }

    private static string ManifestPath(string backupPath) => backupPath + ".json";

    private BackupEntry Write(string path, byte[] bytes, string reason, bool original)
    {
        var (text, format) = FileFormat.Decode(bytes);
        var entry = new BackupEntry
        {
            FilePath = path, Timestamp = DateTimeOffset.UtcNow, Bytes = bytes.LongLength,
            Sha256 = HostsDocument.Sha256(bytes), Reason = reason,
            EntryCount = HostsFileParser.Parse(text, format).Entries.Count(),
            Encoding = format.Describe(), IsOriginal = original, TargetPath = TargetPath,
        };
        // CreateNew is the allocation, not an existence check followed by an overwrite.
        FileStream file;
        try { file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
        catch (IOException ex) when (File.Exists(path)) { throw new BackupNameCollisionException(ex); }
        using (var stream = file)
        {
            stream.Write(bytes);
            stream.Flush(true);
        }

        var manifest = ManifestPath(path);
        var staged = manifest + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, entry, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(true);
            }
            File.Move(staged, manifest); // Never replace pre-existing metadata.
        }
        finally { TryDelete(staged); }
        return entry;
    }

    private static BackupEntry? ReadManifest(string path)
    {
        try
        {
            var entry = JsonSerializer.Deserialize<BackupEntry>(File.ReadAllText(ManifestPath(path)));
            return entry is null ? null : entry with
            {
                FilePath = path,
                IsOriginal = string.Equals(Path.GetFileName(path), OriginalFileName, StringComparison.OrdinalIgnoreCase),
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static BackupEntry Reconstruct(string path)
    {
        var info = new FileInfo(path);
        return new BackupEntry
        {
            FilePath = path, Timestamp = info.LastWriteTimeUtc, Bytes = info.Length,
            Sha256 = "", Reason = "Unknown — metadata unavailable", HasRecordedHash = false,
            IsOriginal = string.Equals(info.Name, OriginalFileName, StringComparison.OrdinalIgnoreCase),
        };
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class BackupNameCollisionException(IOException inner) : IOException("Backup name already exists.", inner);
}
