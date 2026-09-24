using HostsManager.Core;
using HostsManager.Services;

namespace HostsManager.Tests;

public class BrowserProfileProcessesTests
{
    [Fact]
    public void MatchesOnlyExactProfileRoots()
    {
        var path = Path.Combine(Path.GetTempPath(), "preview profile", "abc123");
        Assert.True(BrowserProfileProcesses.Matches(["chrome.exe", "--user-data-dir=" + path], path));
        Assert.True(BrowserProfileProcesses.Matches(["chrome.exe", "--user-data-dir", path], path));
        Assert.False(BrowserProfileProcesses.Matches(["chrome.exe", "--user-data-dir=" + path + "-other"], path));
        Assert.False(BrowserProfileProcesses.Matches(["chrome.exe", "--user-data-dir=" + path, "--type=renderer"], path));
        Assert.False(BrowserProfileProcesses.Matches(["chrome.exe", "--type=gpu-process", "--user-data-dir=" + path], path));
        Assert.False(BrowserProfileProcesses.Matches(["chrome.exe", "https://example.test"], path));
        Assert.False(BrowserProfileProcesses.Matches(["chrome.exe", "--user-data-dir=relative"], path));
    }

    [Fact]
    public void WindowsProcessQueryDoesNotMatchAnUnlaunchedProfile()
    {
        if (!OperatingSystem.IsWindows()) return;
        var profile = new BrowserPreviewProfile("chrome", "abcdef123456",
            Path.Combine(Path.GetTempPath(), "unlaunched-" + Guid.NewGuid()));
        Assert.Empty(BrowserProfileProcesses.Find(profile));
    }
}
