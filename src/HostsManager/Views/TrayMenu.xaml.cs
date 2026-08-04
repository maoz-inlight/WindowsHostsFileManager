using System.Windows;
using System.Windows.Input;

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
        // Off-screen first: Width/Height aren't known until the window has been shown
        // once (SizeToContent), and showing at the real position before that would flash
        // at the wrong spot for a frame.
        Left = -10000;
        Top = -10000;
        Show();
        UpdateLayout();

        var width = ActualWidth;
        var height = ActualHeight;

        var left = point.X + width <= workingArea.Right ? point.X : point.X - width;
        var top = point.Y + height <= workingArea.Bottom ? point.Y : point.Y - height;

        Left = Math.Max(workingArea.Left, Math.Min(left, workingArea.Right - width));
        Top = Math.Max(workingArea.Top, Math.Min(top, workingArea.Bottom - height));

        Activate();
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
