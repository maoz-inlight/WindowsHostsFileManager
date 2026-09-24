using System.Diagnostics;
using System.IO;

namespace HostsManager.Services;

internal static class BrowserPreviewPreferences
{
    private static readonly string PathName = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HostsManager", "preview-browser.txt");
    private static ChromiumBrowserKind? _lastSelected;

    public static ChromiumBrowserKind? LastSelected
    {
        get
        {
            if (_lastSelected is not null) return _lastSelected;
            try
            {
                if (File.Exists(PathName) && Enum.TryParse<ChromiumBrowserKind>(
                        File.ReadAllText(PathName).Trim(), out var browser) && Enum.IsDefined(browser))
                    _lastSelected = browser;
            }
            catch (IOException ex) { Debug.WriteLine(ex); }
            catch (UnauthorizedAccessException ex) { Debug.WriteLine(ex); }
            return _lastSelected;
        }
    }

    public static void Remember(ChromiumBrowserKind browser)
    {
        _lastSelected = browser;
        var temporary = PathName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            File.WriteAllText(temporary, browser.ToString());
            File.Move(temporary, PathName, overwrite: true);
        }
        catch (IOException ex) { Debug.WriteLine(ex); }
        catch (UnauthorizedAccessException ex) { Debug.WriteLine(ex); }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException ex) { Debug.WriteLine(ex); }
            catch (UnauthorizedAccessException ex) { Debug.WriteLine(ex); }
        }
    }
}
