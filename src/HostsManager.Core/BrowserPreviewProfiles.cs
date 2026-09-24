using System.Text.RegularExpressions;
using System.Text.Json;

namespace HostsManager.Core;

public sealed record BrowserPreviewProfile(string Browser, string Key, string Path,
    BrowserPreviewProfileMetadata? Metadata = null)
{
    public string ColorSwatch => Metadata?.Color ?? "#808080";
    public string DisplayName => (Metadata?.Name is { Length: > 0 } name ? name + " · " : "") + MappingDescription;
    private string MappingDescription => Metadata is { Mappings.Length: > 0 }
        ? $"{Browser} — {string.Join(", ", Metadata.Mappings.Take(3))}" +
          (Metadata.Mappings.Length > 3 ? $" (+{Metadata.Mappings.Length - 3} more)" : "") +
          $" · last launched {Metadata.LastLaunchedUtc.ToLocalTime():g}"
        : $"{Browser} — {Key} · mappings unknown (older profile)";
}

public sealed record BrowserPreviewProfileMetadata(string[] Mappings, string[] Flags, DateTime LastLaunchedUtc,
    string? Name = null, string? Color = null, int ThemeVersion = 0);

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
            throw new InvalidOperationException("This preview profile is still running. Close its windows, or use Close background preview, then retry. No profile was deleted.");
        Directory.Delete(expected, recursive: true);
    }

    public void RecordLaunch(BrowserPreviewProfile profile, IEnumerable<string> mappings, IEnumerable<string> flags)
    {
        var path = ValidatePath(profile);
        var old = ReadMetadata(path);
        var metadata = new BrowserPreviewProfileMetadata(mappings.ToArray(), flags.ToArray(), DateTime.UtcNow,
            old?.Name, old?.Color, old?.ThemeVersion ?? 0);
        WriteMetadata(path, metadata);
    }

    public BrowserPreviewProfile Get(string browser, string key)
    {
        var profile = new BrowserPreviewProfile(browser, key, Path.Combine(Root, browser, key));
        if (!Directory.Exists(profile.Path)) return profile;
        ValidatePath(profile);
        return profile with { Metadata = ReadMetadata(profile.Path) };
    }

    public void SaveAppearance(BrowserPreviewProfile profile, BrowserPreviewAppearance appearance,
        Func<string, bool> browserIsRunning)
    {
        appearance = appearance.Normalize();
        var path = ValidatePath(profile);
        var old = ReadMetadata(path);
        var themeVersion = BrowserPreviewAppearance.ThemeVersionFor(profile.Browser);
        if (appearance.Color != old?.Color || (appearance.Color is not null && old?.ThemeVersion != themeVersion))
        {
            var preferencesDirectory = Path.Combine(path, "Default");
            var preferences = Path.Combine(preferencesDirectory, "Preferences");
            if (Directory.Exists(preferencesDirectory))
            {
                CheckParents(preferencesDirectory);
                if (browserIsRunning(profile.Browser))
                    throw new InvalidOperationException("This preview profile is still running, possibly in the background. Close its windows, or select it in Browser preview data and choose Close background preview, then retry the color change.");
            }
            if (File.Exists(preferences) && (File.GetAttributes(preferences) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Browser preferences must not be a linked file.");
            var json = File.Exists(preferences) ? File.ReadAllText(preferences) : "{}";
            var updated = appearance.UpdatePreferences(json, profile.Browser);
            Directory.CreateDirectory(preferencesDirectory);
            WriteAtomic(preferences, updated);
        }
        WriteMetadata(path, new(old?.Mappings ?? [], old?.Flags ?? [], old?.LastLaunchedUtc ?? default,
            appearance.Name, appearance.Color, themeVersion));
    }

    private static void WriteMetadata(string path, BrowserPreviewProfileMetadata metadata) =>
        WriteAtomic(Path.Combine(path, MetadataFile), JsonSerializer.Serialize(metadata));

    private static void WriteAtomic(string destination, string content)
    {
        var path = Path.GetDirectoryName(destination)!;
        var temporary = Path.Combine(path, $".preview-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, destination, overwrite: true);
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
            $"Name: {metadata?.Name ?? "Not named"}    Color: {metadata?.Color ?? "Browser default"}",
            $"Folder created: {Directory.GetCreationTime(path):g}",
            metadata is null || metadata.LastLaunchedUtc == default ? "Last launch: unknown (no saved preview details)"
                : $"Last launched: {metadata.LastLaunchedUtc.ToLocalTime():g}",
            metadata is null || metadata.Mappings.Length == 0 ? "Mappings: unknown; details will be recorded when this profile is reused."
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
            if (value is not { Mappings: not null, Flags: not null }) return null;
            var appearance = new BrowserPreviewAppearance(value.Name, value.Color).Normalize();
            return value with { Name = appearance.Name, Color = appearance.Color };
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
        catch (ArgumentException) { return null; }
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
