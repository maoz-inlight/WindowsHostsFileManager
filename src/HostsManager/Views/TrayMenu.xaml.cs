using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace HostsManager.Views;

public sealed record TrayHeaderRow(string Label, Action OnClick);
public sealed record TrayActionRow(string Label, Action OnClick);
public sealed record TrayToggleRow(string Label, string Tooltip, bool IsOn, Action OnToggle);
public sealed record TrayTextRow(string Label);
public sealed record TraySeparator;

/// <summary>
/// A themed replacement for the tray icon's context menu. <see cref="System.Windows.Forms.
/// ContextMenuStrip"/> only ever renders in stock OS chrome, so this is a borderless WPF
/// popup styled with the same palette as the rest of the app instead.
/// </summary>
public partial class TrayMenu : Window
{
    public TrayMenu(IReadOnlyList<object> rows)
    {
        InitializeComponent();
        Rows.ItemsSource = rows;

        Deactivated += (_, _) => Close();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    /// <summary>
    /// Positions and shows the popup near a screen point, flipping which corner it
    /// anchors from so it stays fully inside <paramref name="workingArea"/> — the usual
    /// behaviour for a menu anchored to a taskbar tray icon, regardless of which edge of
    /// the screen the taskbar is actually on.
    /// </summary>
    public void ShowNear(System.Drawing.Point point, System.Drawing.Rectangle workingArea)
    {
        // Realize the window off-screen first: its size isn't known until it has been
        // shown once (SizeToContent), and showing it at the target position before that
        // would flash at the wrong spot for a frame.
        Left = -10000;
        Top = -10000;
        Show();
        UpdateLayout();

        var handle = new WindowInteropHelper(this).Handle;

        // Deliberately all in physical pixels via Win32, not WPF's Left/Top. The click
        // point and the screen's working area are physical, while Left/Top are
        // device-independent units - on a scaled display those are different numbers, and
        // mixing them puts the window far off-screen (which looks exactly like a menu that
        // never opens). GetWindowRect/SetWindowPos keep everything in one unit system.
        GetWindowRect(handle, out var bounds);
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;

        var left = point.X + width <= workingArea.Right ? point.X : point.X - width;
        var top = point.Y + height <= workingArea.Bottom ? point.Y : point.Y - height;

        left = Math.Max(workingArea.Left, Math.Min(left, workingArea.Right - width));
        top = Math.Max(workingArea.Top, Math.Min(top, workingArea.Bottom - height));

        SetWindowPos(handle, IntPtr.Zero, left, top, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);

        // Activate() alone isn't enough: the click that opened this went to the shell, so
        // the shell owns the foreground and Windows blocks a background process from
        // taking it - leaving the popup unactivated behind the taskbar flyout.
        SetForegroundWindow(handle);
        Activate();
    }

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect32 rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y,
        int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is TrayHeaderRow row) row.OnClick();
        Close();
    }

    private void OnActionClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is TrayActionRow row) row.OnClick();
        Close();
    }

    private void OnToggleClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is TrayToggleRow row) row.OnToggle();
        Close();
    }
}
