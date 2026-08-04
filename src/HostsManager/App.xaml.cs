using System.Windows;
using HostsManager.Core;
using HostsManager.Services;
using HostsManager.ViewModels;

namespace HostsManager;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = CommandLineOptions.Parse(e.Args);
        var backups = new BackupManager(options.BackupsDirectory);
        var writer = new HostsFileWriter(options.HostsPath, backups);

        // Headless recovery runs regardless of an existing instance: it is the path you
        // reach for when something is already wrong, so it must not be refusable.
        if (options.RestoreOriginal || options.RestoreLatest)
        {
            RunHeadlessRestore(writer, backups, options.RestoreOriginal);
            Shutdown();
            return;
        }

        // Two instances would each hold their own view of the file and their own drift
        // hash, so the second one hands off to the first and leaves.
        if (!SingleInstance.TryAcquire(out var single))
        {
            Shutdown();
            return;
        }

        _singleInstance = single;
        _singleInstance!.ActivationRequested += () => Dispatcher.Invoke(ShowMainWindow);

        if (options.ForcedTheme is { } forced) ThemeManager.Apply(forced);
        else ThemeManager.Start();

        var viewModel = new MainViewModel(writer);
        var window = new MainWindow(viewModel);
        MainWindow = window;

        _tray = new TrayIcon(viewModel, ShowMainWindow, ExitApplication);
        window.TrayIcon = _tray;

        window.Show();
    }

    private SingleInstance? _singleInstance;
    private TrayIcon? _tray;

    private void ShowMainWindow()
    {
        if (MainWindow is not { } window) return;

        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;

        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    private void ExitApplication()
    {
        if (MainWindow is MainWindow window && !window.ConfirmExit()) return;

        _tray?.Dispose();
        ThemeManager.Stop();
        _singleInstance?.Dispose();
        Shutdown();
    }

    private static void RunHeadlessRestore(HostsFileWriter writer, BackupManager backups, bool original)
    {
        try
        {
            writer.Load();

            var all = backups.List();
            var target = original
                ? all.FirstOrDefault(b => b.IsOriginal)
                : all.FirstOrDefault(b => !b.IsOriginal) ?? all.FirstOrDefault();

            if (target is null)
            {
                MessageBox.Show($"No backups found in {backups.Directory}.",
                    "Nothing to restore", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = writer.Restore(target);
            MessageBox.Show($"{result.Message}\n\n{writer.HostsPath}",
                "Restored", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Restore failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

public sealed record CommandLineOptions
{
    /// <summary>Overrides the hosts file location. Used to rehearse changes against a copy.</summary>
    public string? HostsPath { get; init; }

    public string? BackupsDirectory { get; init; }
    public bool RestoreLatest { get; init; }
    public bool RestoreOriginal { get; init; }

    /// <summary>Pins the theme instead of following Windows. Null means follow the system.</summary>
    public AppTheme? ForcedTheme { get; init; }

    public static CommandLineOptions Parse(string[] args)
    {
        string? hostsPath = null;
        string? backupsDirectory = null;
        var restoreLatest = false;
        var restoreOriginal = false;
        AppTheme? forcedTheme = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--hosts-path" when i + 1 < args.Length:
                    hostsPath = args[++i];
                    break;
                case "--backups-dir" when i + 1 < args.Length:
                    backupsDirectory = args[++i];
                    break;
                case "--restore-latest":
                    restoreLatest = true;
                    break;
                case "--restore-original":
                    restoreOriginal = true;
                    break;
                case "--theme" when i + 1 < args.Length:
                    forcedTheme = args[++i].ToLowerInvariant() switch
                    {
                        "dark" => AppTheme.Dark,
                        "light" => AppTheme.Light,
                        _ => null,
                    };
                    break;
            }
        }

        return new CommandLineOptions
        {
            HostsPath = hostsPath,
            BackupsDirectory = backupsDirectory,
            RestoreLatest = restoreLatest,
            RestoreOriginal = restoreOriginal,
            ForcedTheme = forcedTheme,
        };
    }
}
