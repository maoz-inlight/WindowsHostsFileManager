using System.Text.RegularExpressions;
using System.Text.Json;

namespace HostsManager.Core;

public sealed record BrowserPreviewProfile(string Browser, string Key, string Path,
    BrowserPreviewProfileMetadata? Metadata = null)
{
    public string DisplayName => Metadata is { Mappings.Length: > 0 }
        ? $"{Browser} — {string.Join(", ", Metadata.Mappings.Take(3))}" +
          (Metadata.Mappings.Length > 3 ? $" (+{Metadata.Mappings.Length - 3} more)" : "") +
          $" · last launched {Metadata.LastLaunchedUtc.ToLocalTime():g}"
        : $"{Browser} — {Key} · mappings unknown (older profile)";
}

public sealed record BrowserPreviewProfileMetadata(string[] Mappings, string[] Flags, DateTime LastLaunchedUtc);

/// <summary>Only the two app-owned browser directories and twelve-digit profile keys are eligible.</summary>
public sealed class BrowserPreviewProfiles(string root)
{
    private const string MetadataFile = "hostsmanager-preview.json";
    public string Root { get; } = Path.GetFullPath(root);

    public IReadOnlyList<BrowserPreviewProfile> List()
    {
        var result = new List<BrowserPreviewProfile>();
        if (!Directory.Exists(Root)) return result;
        CheckParents(Root);
        foreach (var browser in new[] { "edge", "chrome" })
        {
            var parent = Path.Combine(Root, browser);
            if (!Directory.Exists(parent)) continue;
            CheckParents(parent);
            foreach (var path in Directory.EnumerateDirectories(parent))
            {
                var key = Path.GetFileName(path);
                if (Regex.IsMatch(key, "\\A[0-9a-f]{12}\\z") &&
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                    result.Add(new(browser, key, path, ReadMetadata(path)));
            }
        }
        return result.OrderBy(p => p.DisplayName).ToArray();
    }

    public void Delete(BrowserPreviewProfile profile, Func<string, bool> browserIsRunning)
    {
        var expected = ValidatePath(profile);
        // Check the whole tree before deleting anything; never follow a junction or symlink.
        CheckTree(expected);
        if (browserIsRunning(profile.Browser))
            throw new InvalidOperationException($"Close all {profile.Browser} windows and background processes, then retry. No profile was deleted.");
        Directory.Delete(expected, recursive: true);
    }

    public void RecordLaunch(BrowserPreviewProfile profile, IEnumerable<string> mappings, IEnumerable<string> flags)
    {
        var path = ValidatePath(profile);
        var metadata = new BrowserPreviewProfileMetadata(mappings.ToArray(), flags.ToArray(), DateTime.UtcNow);
        var temporary = Path.Combine(path, $".preview-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(metadata));
            File.Move(temporary, Path.Combine(path, MetadataFile), overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public string Describe(BrowserPreviewProfile profile)
    {
        var path = ValidatePath(profile);
        var metadata = ReadMetadata(path);
        var details = new List<string>
        {
            $"Browser: {profile.Browser}    Profile: {profile.Key}",
            $"Folder created: {Directory.GetCreationTime(path):g}",
            metadata is null ? "Last launch: unknown (no saved preview details)"
                : $"Last launched: {metadata.LastLaunchedUtc.ToLocalTime():g}",
            metadata is null ? "Mappings: unknown; details will be recorded when this profile is reused."
                : "Mappings:\n" + string.Join("\n", metadata.Mappings),
            metadata is null ? "Browser options: unknown"
                : "Browser options: " + (metadata.Flags.Length == 0 ? "Default" : string.Join("\n", metadata.Flags)),
        };
        try
        {
            var bytes = Measure(path);
            details.Add($"Disk usage: {bytes / (1024d * 1024d):N1} MB ({bytes:N0} bytes)");
        }
        catch (IOException) { details.Add("Disk usage: unavailable (locked, changing or linked files)"); }
        catch (UnauthorizedAccessException) { details.Add("Disk usage: unavailable (access denied)"); }
        details.Add("Folder: " + path);
        return string.Join(Environment.NewLine, details);
    }

    private string ValidatePath(BrowserPreviewProfile profile)
    {
        if (profile.Browser is not ("edge" or "chrome") ||
            !Regex.IsMatch(profile.Key, "\\A[0-9a-f]{12}\\z"))
            throw new InvalidOperationException("Not an app-owned preview profile.");
        var expected = Path.Combine(Root, profile.Browser, profile.Key);
        if (!string.Equals(Path.GetFullPath(profile.Path), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Profile is outside the preview storage folder.");
        CheckParents(expected);
        return expected;
    }

    private static BrowserPreviewProfileMetadata? ReadMetadata(string path)
    {
        try
        {
            var file = new FileInfo(Path.Combine(path, MetadataFile));
            if (!file.Exists || file.Length > 65536 || (file.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            var value = JsonSerializer.Deserialize<BrowserPreviewProfileMetadata>(File.ReadAllText(file.FullName));
            return value is { Mappings: not null, Flags: not null } && value.LastLaunchedUtc != default
                ? value : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }

    private static long Measure(string path)
    {
        long bytes = 0;
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked profile content.");
            bytes += entry is DirectoryInfo ? Measure(entry.FullName) : ((FileInfo)entry).Length;
        }
        return bytes;
    }

    private static void CheckParents(string path)
    {
        for (var dir = new DirectoryInfo(path); dir is not null; dir = dir.Parent)
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Preview cleanup does not follow linked folders.");
    }

    private static void CheckTree(string path)
    {
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Preview cleanup does not follow linked files or folders.");
            if (entry is DirectoryInfo) CheckTree(entry.FullName);
        }
    }
}
