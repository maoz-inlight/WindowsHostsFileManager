using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using HostsManager.Services;
using HostsManager.ViewModels;
using HostsManager.Views;

namespace HostsManager;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();

        _vm = vm;
        DataContext = vm;

        ThemeManager.Track(this);

        vm.RequestAddEntry = ShowAddEntryDialog;
        vm.ShowBackups = ShowBackupsDialog;
        vm.Confirm = (title, message) =>
            MessageBox.Show(this, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question)
            == MessageBoxResult.OK;
        vm.ShowError = (title, message) =>
            MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

        Loaded += (_, _) => vm.Initialize();
    }

    private AddEntryRequest? ShowAddEntryDialog()
    {
        var dialog = new AddEntryDialog { Owner = this };
        ThemeManager.Track(dialog);
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private void OnAbout(object sender, RoutedEventArgs e) => ShowAbout();

    /// <summary>Also called from the tray's "About" item, which has no dialog owner of its own.</summary>
    public void ShowAbout()
    {
        var dialog = new AboutDialog(_vm.Writer.Backups.Directory) { Owner = this };
        ThemeManager.Track(dialog);
        dialog.ShowDialog();
    }

    private void ShowBackupsDialog()
    {
        var dialog = new BackupsDialog(_vm.Writer) { Owner = this };
        ThemeManager.Track(dialog);
        dialog.ShowDialog();

        if (dialog.Restored) _vm.Reload(discardChanges: true);
        _vm.RaiseAll();
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        vm.Filter = FilterBox.SelectedIndex switch
        {
            1 => EntryFilter.Loopback,
            2 => EntryFilter.Problems,
            _ => EntryFilter.All,
        };
    }

    /// <summary>Set once so the tray's Exit can close the window for real.</summary>
    public TrayIcon? TrayIcon { get; set; }

    private bool _exiting;
    private bool _hintShown;

    /// <summary>
    /// Asks about unsaved work before a genuine exit. Called by the tray's Exit item,
    /// which is the only path that actually ends the process.
    /// </summary>
    public bool ConfirmExit()
    {
        // OnClosing keys its hide-to-tray branch off this flag, so it has to be set on
        // every path that goes on to exit — including the clean one that returns early.
        _exiting = true;

        if (!_vm.IsDirty) return true;

        var answer = MessageBox.Show(this,
            $"{_vm.PendingText} will be lost. Exit anyway?",
            "Unsaved changes", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (answer == MessageBoxResult.OK) return true;

        // Backing out of the exit puts the window back to hiding on close.
        _exiting = false;
        return false;
    }

    /// <summary>
    /// Closing hides to the tray rather than quitting. Nothing is lost by hiding, so
    /// unlike a real exit this deliberately doesn't interrogate the user about pending
    /// changes — they are still there when the window comes back.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_exiting)
        {
            _vm.Dispose();
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        Hide();

        if (!_hintShown)
        {
            _hintShown = true;
            TrayIcon?.ShowMessage("Still running",
                "Hosts manager is in the notification area. Right-click its icon to toggle a domain or exit.");
        }
    }
}
