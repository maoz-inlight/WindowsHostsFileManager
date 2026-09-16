using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using HostsManager.Core;

namespace HostsManager.Views;

public partial class BackupsDialog : Window
{
    private readonly HostsFileWriter _writer;

    public BackupsDialog(HostsFileWriter writer)
    {
        InitializeComponent();

        _writer = writer;
        LegacyNotice.Text = $"Backups belong to this target only. Earlier unscoped backups remain in {writer.Backups.RootDirectory}. " +
            "Their target is unknown; inspect them before using Import entries.";
        ManualCommand.Text = $"copy /Y \"{writer.Backups.OriginalPath}\" \"{writer.HostsPath}\"";

        Refresh();
    }

    /// <summary>True if the hosts file was restored, so the caller knows to reload.</summary>
    public bool Restored { get; private set; }

    private sealed record Row(BackupEntry Entry, string When, string Reason, string EntryCount, string Size, string Intact);

    private void Refresh()
    {
        BackupGrid.ItemsSource = _writer.Backups.List().Select(b => new Row(
            b,
            b.IsOriginal ? "Original" : b.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
            b.Reason,
            b.EntryCount > 0 ? b.EntryCount.ToString() : "—",
            $"{b.Bytes:N0} B",
            !b.HasRecordedHash ? "Unknown" : _writer.Backups.Verify(b) ? "Yes" : "No")).ToList();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RestoreButton.IsEnabled = BackupGrid.SelectedItem is Row row && _writer.Backups.CanRestore(row.Entry);

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        if (BackupGrid.SelectedItem is not Row row || !_writer.Backups.CanRestore(row.Entry)) return;

        var answer = MessageBox.Show(this,
            $"Replace the current hosts file with this backup?\n\n{row.When} — {row.Reason}\n\n" +
            "The current contents are backed up first, so this can be undone.",
            "Restore backup", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK) return;

        try
        {
            var result = _writer.Restore(row.Entry);
            Restored = true;
            Refresh();

            MessageBox.Show(this, result.Message, "Restored", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Restore failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
        => OpenFolder(_writer.Backups.Directory);

    private void OnOpenLegacyFolder(object sender, RoutedEventArgs e)
        => OpenFolder(_writer.Backups.RootDirectory);

    private void OpenFolder(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not open the folder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
