// System.Windows is deliberately not imported: it collides with System.Windows.Forms on
// MessageBox, Application and Size. The few WPF touchpoints here are fully qualified.
using System.Drawing;
using System.Windows.Forms;
using HostsManager.Core;
using HostsManager.ViewModels;
using HostsManager.Views;

namespace HostsManager.Services;

/// <summary>
/// Tray presence for the app: somewhere to park it between edits, and a way to flip a
/// domain without opening the window at all.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly MainViewModel _viewModel;
    private readonly Action _showWindow;
    private readonly Action _exit;
    private readonly Action _showAbout;

    // The popup shown on right-click. Closed and rebuilt fresh on every open, same as the
    // old ContextMenuStrip was, so the toggle rows always reflect the current file state.
    private TrayMenu? _menu;

    public TrayIcon(MainViewModel viewModel, Action showWindow, Action exit, Action showAbout)
    {
        _viewModel = viewModel;
        _showWindow = showWindow;
        _exit = exit;
        _showAbout = showAbout;

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Hosts manager",
            Visible = true,
            // Kept only as a reliable right-click signal, never actually shown (Opening
            // cancels it below). An icon sitting in the taskbar's hidden-icons overflow is
            // reached through a different message path than a directly-visible one, and
            // MouseUp's WM_RBUTTONUP doesn't reliably arrive there - ContextMenuStrip's own
            // Opening event is what Windows forwards correctly through that overflow proxy
            // regardless, since that's how the icon's right-click has always been wired.
            ContextMenuStrip = new ContextMenuStrip(),
        };

        _icon.DoubleClick += (_, _) => _showWindow();
        _icon.ContextMenuStrip!.Opening += (_, e) =>
        {
            e.Cancel = true;

            // Capture now, while the click point is still accurate, but defer actually
            // showing our window to the next dispatcher cycle. Cancelling from inside
            // Opening happens while Explorer is still in the middle of its own teardown
            // for this interaction (closing the hidden-icons overflow, in particular) -
            // showing a window synchronously here races that and it can lose activation
            // and close itself before it's ever visible.
            var point = Cursor.Position;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => ShowMenu(point)));
        };
    }

    public void ShowMessage(string title, string body) =>
        _icon.ShowBalloonTip(3000, title, body, ToolTipIcon.None);

    private static Icon LoadIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri("Assets/app.ico", UriKind.Relative));

        if (resource is not null)
        {
            using var stream = resource.Stream;
            return new Icon(stream, new System.Drawing.Size(32, 32));
        }

        return SystemIcons.Application;
    }

    private void ShowMenu(System.Drawing.Point point)
    {
        // A stray second right-click while one is already open would otherwise stack a
        // new popup on top of it instead of replacing it.
        _menu?.Close();

        _menu = new TrayMenu(BuildRows());

        var workingArea = Screen.FromPoint(point).WorkingArea;
        _menu.ShowNear(point, workingArea);
    }

    private List<object> BuildRows()
    {
        var rows = new List<object>
        {
            new TrayHeaderRow("Open hosts manager", _showWindow),
            new TraySeparator(),
        };

        AddToggleRows(rows);

        rows.Add(new TraySeparator());
        rows.Add(new TrayActionRow("Flush DNS cache", () =>
        {
            var (success, message) = DnsFlusher.Flush();
            ShowMessage(success ? "Hosts manager" : "Could not flush DNS", message);
        }));
        rows.Add(new TrayActionRow("About", _showAbout));
        rows.Add(new TrayActionRow("Exit", _exit));

        return rows;
    }

    private void AddToggleRows(List<object> rows)
    {
        var document = _viewModel.Writer.Document;
        if (document is null) return;

        // Toggling from the tray writes immediately, since there is no Save button out
        // here. That would also commit whatever is pending in the window, so while the
        // window is dirty the quick toggles stand down rather than save on the user's behalf.
        if (_viewModel.IsDirty)
        {
            rows.Add(new TrayTextRow("Unsaved changes — open the app to save"));
            return;
        }

        var entries = document.Lines.Where(l => l.IsEntry && !l.IsReadOnly).ToList();
        if (entries.Count == 0)
        {
            rows.Add(new TrayTextRow("No entries"));
            return;
        }

        foreach (var entry in entries)
        {
            var label = entry.PrimaryHostname ?? entry.Body.Trim();
            var tooltip = $"{entry.Ip}  {string.Join(' ', entry.Hostnames)}";
            rows.Add(new TrayToggleRow(label, tooltip, entry.IsEnabled, () => QuickToggle(entry)));
        }
    }

    private void QuickToggle(HostsLine entry)
    {
        var document = _viewModel.Writer.Document;
        if (document is null) return;

        try
        {
            document.Toggle(entry);
            _viewModel.SaveFromTray($"Tray toggle: {entry.PrimaryHostname}");

            ShowMessage("Hosts manager",
                $"{entry.PrimaryHostname} {(entry.IsEnabled ? "enabled" : "disabled")}.");
        }
        catch (Exception ex)
        {
            // Put the change back so the tray menu keeps matching the file. Toggling
            // back re-marks the line as modified, which would leave the window claiming
            // an unsaved change that no longer exists and cannot be saved, so the
            // pending markers are cleared too.
            document.Toggle(entry);
            document.ClearPendingMarkers();
            _viewModel.RefreshRows();

            System.Windows.Forms.MessageBox.Show(ex.Message, "Could not save",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    public void Dispose()
    {
        _menu?.Close();
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
