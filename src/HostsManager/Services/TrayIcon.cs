// System.Windows is deliberately not imported: it collides with System.Windows.Forms on
// MessageBox, Application and Size. The few WPF touchpoints here are fully qualified.
using System.Drawing;
using System.Windows.Forms;
using HostsManager.Core;
using HostsManager.ViewModels;

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

    // Built once. The menu is rebuilt on every open, and a font allocated per rebuild
    // would leak a GDI handle each time in an app designed to sit in the tray for days.
    private readonly Font _boldFont;

    public TrayIcon(MainViewModel viewModel, Action showWindow, Action exit, Action showAbout)
    {
        _viewModel = viewModel;
        _showWindow = showWindow;
        _exit = exit;
        _showAbout = showAbout;
        _boldFont = new Font(System.Drawing.SystemFonts.MenuFont!, System.Drawing.FontStyle.Bold);

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Hosts manager",
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip(),
        };

        _icon.DoubleClick += (_, _) => _showWindow();
        _icon.ContextMenuStrip.Opening += (_, _) => BuildMenu();
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

    private void BuildMenu()
    {
        var menu = _icon.ContextMenuStrip!;

        // Clear() detaches items without disposing them. Snapshot and clear before
        // disposing, since disposing an item removes it from the collection it is in.
        var stale = menu.Items.Cast<ToolStripItem>().ToArray();
        menu.Items.Clear();
        foreach (var item in stale) item.Dispose();

        var open = new ToolStripMenuItem("Open hosts manager", null, (_, _) => _showWindow())
        {
            Font = _boldFont,
        };
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());

        AddToggleSection(menu);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Flush DNS cache", null, (_, _) =>
        {
            var (success, message) = DnsFlusher.Flush();
            ShowMessage(success ? "Hosts manager" : "Could not flush DNS", message);
        }));
        menu.Items.Add(new ToolStripMenuItem("About", null, (_, _) => _showAbout()));
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => _exit()));
    }

    private void AddToggleSection(ContextMenuStrip menu)
    {
        var document = _viewModel.Writer.Document;
        if (document is null) return;

        // Toggling from the tray writes immediately, since there is no Save button out
        // here. That would also commit whatever is pending in the window, so while the
        // window is dirty the quick toggles stand down rather than save on the user's behalf.
        if (_viewModel.IsDirty)
        {
            menu.Items.Add(new ToolStripMenuItem("Unsaved changes — open the app to save") { Enabled = false });
            return;
        }

        var entries = document.Lines.Where(l => l.IsEntry && !l.IsReadOnly).ToList();
        if (entries.Count == 0)
        {
            menu.Items.Add(new ToolStripMenuItem("No entries") { Enabled = false });
            return;
        }

        foreach (var entry in entries)
        {
            var label = entry.PrimaryHostname ?? entry.Body.Trim();
            var item = new ToolStripMenuItem(label, null, (_, _) => QuickToggle(entry))
            {
                Checked = entry.IsEnabled,
                CheckOnClick = false,
                ToolTipText = $"{entry.Ip}  {string.Join(' ', entry.Hostnames)}",
            };

            menu.Items.Add(item);
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
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _boldFont.Dispose();
    }
}
