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

    [JsonIgnore]
    public string FileName => Path.GetFileName(FilePath);
}

/// <summary>
/// Keeps timestamped copies of the hosts file so any change can be undone.
/// <para>
/// Backups live under <c>%LOCALAPPDATA%</c> rather than beside the hosts file in
/// System32. That is deliberate: they stay readable and restorable without elevation,
/// which is exactly the situation you're in when something has gone wrong.
/// </para>
/// </summary>
public sealed class BackupManager
{
    public const int DefaultRetention = 50;
    public const string OriginalFileName = "hosts.original.bak";

    private readonly int _retention;

    public BackupManager(string? directory = null, int retention = DefaultRetention)
    {
        Directory = directory ?? DefaultDirectory;
        _retention = retention;
    }

    public string Directory { get; }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HostsManager", "backups");

    public string OriginalPath => Path.Combine(Directory, OriginalFileName);

    public bool HasOriginal => File.Exists(OriginalPath);

    /// <summary>
    /// Captures the pristine pre-app state exactly once. This backup is never pruned,
    /// so "put it back the way it was before I installed this" is always one click away.
    /// <para>
    /// Best-effort by design: this runs during load, which must stay a read-only
    /// operation that cannot fail the app. A backup directory that cannot be written
    /// is reported through <paramref name="error"/> and blocks saving later, where
    /// refusing is the safe answer.
    /// </para>
    /// </summary>
    public BackupEntry? EnsureOriginal(byte[] bytes, int entryCount, FileFormat format, out string? error)
    {
        error = null;
        if (HasOriginal) return null;

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            return Write(OriginalPath, bytes, "Original file, captured on first run", entryCount, format, isOriginal: true);
        }
        catch (Exception ex)
        {
            error = $"Could not create a backup of the original hosts file in {Directory}: {ex.Message}";
            return null;
        }
    }

    /// <summary>Takes a timestamped backup and prunes older ones.</summary>
    public BackupEntry Create(byte[] bytes, string reason, int entryCount, FileFormat format)
    {
        System.IO.Directory.CreateDirectory(Directory);

        var path = Path.Combine(Directory, $"hosts.{DateTime.Now:yyyyMMdd-HHmmss}.bak");
        for (var suffix = 1; File.Exists(path); suffix++)
            path = Path.Combine(Directory, $"hosts.{DateTime.Now:yyyyMMdd-HHmmss}-{suffix}.bak");

        var entry = Write(path, bytes, reason, entryCount, format, isOriginal: false);
        Prune();
        return entry;
    }

    public IReadOnlyList<BackupEntry> List()
    {
        if (!System.IO.Directory.Exists(Directory)) return Array.Empty<BackupEntry>();

        var entries = new List<BackupEntry>();
        foreach (var file in System.IO.Directory.GetFiles(Directory, "*.bak"))
        {
            entries.Add(ReadManifest(file) ?? Reconstruct(file));
        }

        // Newest first, but the pristine original always sits at the bottom as the floor.
        return entries
            .OrderByDescending(e => e.IsOriginal ? DateTimeOffset.MinValue : e.Timestamp)
            .ToList();
    }

    public byte[] Read(BackupEntry entry) => File.ReadAllBytes(entry.FilePath);

    /// <summary>Verifies a backup still matches the hash recorded when it was taken.</summary>
    public bool Verify(BackupEntry entry)
    {
        if (!File.Exists(entry.FilePath)) return false;
        return HostsDocument.Sha256(File.ReadAllBytes(entry.FilePath)) == entry.Sha256;
    }

    /// <summary>Drops the oldest timestamped backups. Never touches the pristine original.</summary>
    public void Prune()
    {
        var timestamped = List().Where(e => !e.IsOriginal).ToList();
        foreach (var stale in timestamped.Skip(_retention))
        {
            TryDelete(stale.FilePath);
            TryDelete(ManifestPath(stale.FilePath));
        }
    }

    // ---- internals -------------------------------------------------------

    private static string ManifestPath(string backupPath) => backupPath + ".json";

    private static BackupEntry Write(string path, byte[] bytes, string reason, int entryCount,
        FileFormat format, bool isOriginal)
    {
        File.WriteAllBytes(path, bytes);

        var entry = new BackupEntry
        {
            FilePath = path,
            Timestamp = DateTimeOffset.Now,
            Bytes = bytes.LongLength,
            Sha256 = HostsDocument.Sha256(bytes),
            Reason = reason,
            EntryCount = entryCount,
            Encoding = format.Describe(),
            IsOriginal = isOriginal,
        };

        File.WriteAllText(ManifestPath(path),
            JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true }));

        return entry;
    }

    private static BackupEntry? ReadManifest(string backupPath)
    {
        var manifest = ManifestPath(backupPath);
        if (!File.Exists(manifest)) return null;

        try
        {
            var entry = JsonSerializer.Deserialize<BackupEntry>(File.ReadAllText(manifest));
            // The manifest records the path at write time; trust the path we found it at.
            return entry is null ? null : entry with { FilePath = backupPath };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Builds an entry for a .bak whose manifest is missing or corrupt.</summary>
    private static BackupEntry Reconstruct(string backupPath)
    {
        var info = new FileInfo(backupPath);
        return new BackupEntry
        {
            FilePath = backupPath,
            Timestamp = info.LastWriteTime,
            Bytes = info.Length,
            Sha256 = HostsDocument.Sha256(File.ReadAllBytes(backupPath)),
            Reason = "Unknown",
            IsOriginal = string.Equals(info.Name, OriginalFileName, StringComparison.OrdinalIgnoreCase),
        };
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // A locked backup is not worth failing a save over.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
