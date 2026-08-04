using System.Diagnostics;
using System.IO;
using System.Windows;

namespace HostsManager.Views;

public partial class AboutDialog : Window
{
    private readonly string _backupsDirectory;

    public AboutDialog(string backupsDirectory)
    {
        InitializeComponent();

        _backupsDirectory = backupsDirectory;

        // Matches installer/build.ps1's -p:Version, which also becomes the MSI's
        // ProductVersion, so this is the same number Programs and Features shows.
        var exePath = Environment.ProcessPath;
        var version = exePath is not null
            ? FileVersionInfo.GetVersionInfo(exePath).ProductVersion
            : null;
        VersionText.Text = $"Version {version ?? "unknown"}";
        InstallPathText.Text = exePath ?? "unknown";
        InstallPathText.ToolTip = exePath;
    }

    private void OnOpenBackups(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_backupsDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_backupsDirectory}\"")
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
