using System.Text.RegularExpressions;

namespace HostsManager.Core;

public sealed record BrowserPreviewProfile(string Browser, string Key, string Path)
{
    public string DisplayName => $"{Browser} — {Key}";
}

/// <summary>Only the two app-owned browser directories and twelve-digit profile keys are eligible.</summary>
public sealed class BrowserPreviewProfiles(string root)
{
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
                    result.Add(new(browser, key, path));
            }
        }
        return result.OrderBy(p => p.DisplayName).ToArray();
    }

    public void Delete(BrowserPreviewProfile profile, Func<string, bool> browserIsRunning)
    {
        if (profile.Browser is not ("edge" or "chrome") ||
            !Regex.IsMatch(profile.Key, "\\A[0-9a-f]{12}\\z"))
            throw new InvalidOperationException("Not an app-owned preview profile.");
        var expected = Path.Combine(Root, profile.Browser, profile.Key);
        if (!string.Equals(Path.GetFullPath(profile.Path), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Profile is outside the preview storage folder.");
        CheckParents(expected);
        // Check the whole tree before deleting anything; never follow a junction or symlink.
        CheckTree(expected);
        if (browserIsRunning(profile.Browser))
            throw new InvalidOperationException($"Close all {profile.Browser} windows and background processes, then retry. No profile was deleted.");
        Directory.Delete(expected, recursive: true);
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
