using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace HostsManager.Services;

public enum AppTheme { Light, Dark }

/// <summary>
/// Follows the system light/dark setting: picks the matching palette at startup and
/// swaps it live when the user changes the Windows setting, without restarting the app.
/// </summary>
public static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightTheme = "AppsUseLightTheme";

    // DWMWA_USE_IMMERSIVE_DARK_MODE. Windows 10 builds before 19041 used 19 for the
    // same attribute, so both are attempted.
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeLegacy = 19;

    private static readonly List<Window> Tracked = new();

    public static AppTheme Current { get; private set; } = AppTheme.Light;

    public static AppTheme ReadSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            // Missing value means a system old enough to predate dark mode.
            return key?.GetValue(AppsUseLightTheme) is int value && value == 0
                ? AppTheme.Dark
                : AppTheme.Light;
        }
        catch (Exception)
        {
            return AppTheme.Light;
        }
    }

    /// <summary>Applies the system theme and starts listening for changes to it.</summary>
    public static void Start()
    {
        Apply(ReadSystemTheme());
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public static void Stop() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    /// <summary>Registers a window so its title bar follows the theme too.</summary>
    public static void Track(Window window)
    {
        if (!Tracked.Contains(window)) Tracked.Add(window);
        window.Closed += (_, _) => Tracked.Remove(window);

        if (window.IsLoaded) ApplyTitleBar(window, Current);
        else window.SourceInitialized += (_, _) => ApplyTitleBar(window, Current);
    }

    public static void Apply(AppTheme theme)
    {
        Current = theme;

        var app = Application.Current;
        if (app is null) return;

        var uri = new Uri(
            theme == AppTheme.Dark
                ? "pack://application:,,,/Themes/Palette.Dark.xaml"
                : "pack://application:,,,/Themes/Palette.Light.xaml");

        var palette = new ResourceDictionary { Source = uri };

        // The palette is always first so control styles merged after it resolve against
        // the new colours; replacing it in place is what makes DynamicResource re-evaluate.
        var dictionaries = app.Resources.MergedDictionaries;
        if (dictionaries.Count == 0) dictionaries.Add(palette);
        else dictionaries[0] = palette;

        foreach (var window in Tracked.ToList()) ApplyTitleBar(window, theme);
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;

        var theme = ReadSystemTheme();
        if (theme == Current) return;

        Application.Current?.Dispatcher.Invoke(() => Apply(theme));
    }

    /// <summary>
    /// Paints the non-client title bar dark. Without this a dark window keeps a white
    /// caption bar, which looks broken rather than deliberate.
    /// </summary>
    private static void ApplyTitleBar(Window window, AppTheme theme)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            var useDark = theme == AppTheme.Dark ? 1 : 0;
            if (DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref useDark, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, DwmUseImmersiveDarkModeLegacy, ref useDark, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // Desktop Window Manager is unavailable; the window still works.
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [DllImport("dwmapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
