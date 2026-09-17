using HostsManager.Core;

namespace HostsManager.Tests;

public sealed class BrowserPreviewProfilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "preview-tests-" + Guid.NewGuid());
    private BrowserPreviewProfiles Store => new(_root);
    private BrowserPreviewProfile Create(string browser = "edge", string key = "abcdef123456")
    {
        var path = Path.Combine(_root, browser, key);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "Cookies"), "fixture");
        return new(browser, key, path);
    }

    [Fact]
    public void OnlyRecognizedProfilesAreListedAndOnlySelectedProfileIsDeleted()
    {
        var selected = Create();
        var other = Create("chrome");
        var unknown = Create("edge", "Default");
        Assert.Equal(2, Store.List().Count);
        Store.Delete(selected, _ => false);
        Assert.False(Directory.Exists(selected.Path));
        Assert.True(File.Exists(Path.Combine(other.Path, "Cookies")));
        Assert.True(File.Exists(Path.Combine(unknown.Path, "Cookies")));
    }

    [Fact]
    public void RunningBrowserOrUnknownProcessStatusPreservesData()
    {
        var profile = Create();
        Assert.Throws<InvalidOperationException>(() => Store.Delete(profile, _ => true));
        Assert.Throws<IOException>(() => Store.Delete(profile, _ => throw new IOException("Cannot enumerate")));
        Assert.Equal("fixture", File.ReadAllText(Path.Combine(profile.Path, "Cookies")));
    }

    [Fact]
    public void ForgedPathAndUnknownKeysAreRefused()
    {
        var profile = Create();
        Assert.Throws<InvalidOperationException>(() => Store.Delete(profile with { Path = _root }, _ => false));
        Assert.Throws<InvalidOperationException>(() => Store.Delete(profile with { Key = ".." }, _ => false));
        Assert.Throws<InvalidOperationException>(() => Store.Delete(profile with { Browser = "firefox" }, _ => false));
        Assert.True(Directory.Exists(profile.Path));
    }

    [Fact]
    public void EmptyStoreDoesNotCreateDirectories()
    {
        Assert.Empty(Store.List());
        Assert.False(Directory.Exists(_root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
