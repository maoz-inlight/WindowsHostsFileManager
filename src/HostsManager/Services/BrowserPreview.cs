using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using HostsManager.Core;

namespace HostsManager.Services;

public enum ChromiumBrowserKind { Edge, Chrome }

public sealed record ChromiumBrowser(
    ChromiumBrowserKind Kind,
    string DisplayName,
    string ExecutablePath);

public sealed class BrowserPreviewSession : IDisposable
{
    private readonly Process _process;
    private readonly System.Threading.Timer _windowMonitor;
    private int _sawWindow;
    private int _ended;
    private int _disposed;

    internal BrowserPreviewSession(Process process, ChromiumBrowser browser, string description)
    {
        _process = process;
        Browser = browser;
        Description = description;

        _windowMonitor = new System.Threading.Timer(
            CheckWindow, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _process.Exited += OnProcessExited;
        _process.EnableRaisingEvents = true;

        // Chromium can keep its process alive after its last window closes. Watch the
        // window as well as the process so the app does not show a stale active preview.
        _windowMonitor.Change(TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
        CheckWindow(null);
    }

    public ChromiumBrowser Browser { get; }
    public string Description { get; }
    public bool HasExited
    {
        get
        {
            try { return _process.HasExited; }
            catch (InvalidOperationException) { return true; }
        }
    }

    public bool HasVisibleWindow
    {
        get
        {
            if (HasExited) return false;

            try
            {
                _process.Refresh();
                return _process.MainWindowHandle != IntPtr.Zero;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public bool IsEnded =>
        Volatile.Read(ref _ended) != 0
        || HasExited
        || (Volatile.Read(ref _sawWindow) != 0 && !HasVisibleWindow);

    public event Action? Ended;

    public bool RequestClose()
    {
        if (IsEnded)
        {
            SignalEnded();
            return true;
        }

        try { return _process.CloseMainWindow(); }
        catch (InvalidOperationException)
        {
            SignalEnded();
            return true;
        }
    }

    private void CheckWindow(object? state)
    {
        if (HasExited)
        {
            SignalEnded();
            return;
        }

        if (HasVisibleWindow)
        {
            Volatile.Write(ref _sawWindow, 1);
            return;
        }

        if (Volatile.Read(ref _sawWindow) != 0) SignalEnded();
    }

    private void OnProcessExited(object? sender, EventArgs e) => SignalEnded();

    private void SignalEnded()
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;

        try { _windowMonitor.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
        Ended?.Invoke();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _process.Exited -= OnProcessExited;
        _windowMonitor.Dispose();
        _process.Dispose();
    }
}

public sealed class BrowserPreviewService : IDisposable
{
    private BrowserPreviewSession? _active;

    public IReadOnlyList<ChromiumBrowser> FindInstalledBrowsers()
    {
        var found = new List<ChromiumBrowser>();
        Add(ChromiumBrowserKind.Edge, "Microsoft Edge", "msedge.exe", new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft", "Edge", "Application", "msedge.exe"),
        });
        Add(ChromiumBrowserKind.Chrome, "Google Chrome", "chrome.exe", new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Google", "Chrome", "Application", "chrome.exe"),
        });

        return found;

        void Add(ChromiumBrowserKind kind, string displayName, string executableName,
            IEnumerable<string> fallbackPaths)
        {
            var path = FindAppPath(executableName)
                ?? fallbackPaths.FirstOrDefault(File.Exists);

            if (path is not null && found.All(b =>
                    !string.Equals(b.ExecutablePath, path, StringComparison.OrdinalIgnoreCase)))
                found.Add(new ChromiumBrowser(kind, displayName, path));
        }
    }

    public BrowserPreviewSession Launch(ChromiumBrowser browser,
        IReadOnlyList<BrowserOverride> overrides, IReadOnlyList<Uri> startUris)
    {
        if (_active is { IsEnded: false })
            throw new InvalidOperationException(
                "An isolated browser is already running. Close it before starting a different preview.");

        if (startUris.Count == 0)
            throw new ArgumentException("Select at least one URL to open.", nameof(startUris));

        if (startUris.Any(uri => uri.Scheme is not ("http" or "https")))
            throw new ArgumentException(
                "Every preview URL must start with http:// or https://.", nameof(startUris));

        _active?.Dispose();
        _active = null;

        var rules = BrowserOverrideRules.Build(overrides);
        var profileKey = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(rules)))[..12].ToLowerInvariant();
        var profile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HostsManager", "browser-preview", browser.Kind.ToString().ToLowerInvariant(), profileKey);
        Directory.CreateDirectory(profile);

        var arguments = new List<string>
        {
            $"--user-data-dir={profile}",
            $"--host-resolver-rules={rules}",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-background-mode",
            "--new-window",
        };
        arguments.AddRange(startUris.Select(uri => uri.AbsoluteUri));

        var process = UnelevatedProcessLauncher.Start(browser.ExecutablePath, arguments);
        var distinctHosts = overrides.Select(o => o.Hostname)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var distinctTargets = overrides.Select(o => o.Target).Distinct().Count();
        var description = distinctHosts == 1
            ? $"{overrides[0].Hostname} → {overrides[0].Target}"
            : distinctTargets == 1
                ? $"{distinctHosts} domains → {overrides[0].Target}"
                : $"{distinctHosts} domains across {distinctTargets} targets";

        var session = new BrowserPreviewSession(process, browser, description);
        session.Ended += () =>
        {
            if (ReferenceEquals(_active, session)) _active = null;
        };
        _active = session;

        if (session.IsEnded)
        {
            _active = null;
            session.Dispose();
            throw new InvalidOperationException("The isolated browser exited before its window opened.");
        }

        return session;
    }

    private static string? FindAppPath(string executable)
    {
        const string appPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey($@"{appPaths}\{executable}");
                if (key?.GetValue(null) is string path && File.Exists(path.Trim('"')))
                    return path.Trim('"');
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return null;
    }

    public void Dispose()
    {
        _active?.Dispose();
        _active = null;
    }
}
