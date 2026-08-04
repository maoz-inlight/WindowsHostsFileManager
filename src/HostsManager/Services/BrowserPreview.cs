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

    internal BrowserPreviewSession(Process process, ChromiumBrowser browser, string description)
    {
        _process = process;
        Browser = browser;
        Description = description;

        _process.EnableRaisingEvents = true;
        _process.Exited += OnExited;
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

    public event Action? Exited;

    public bool RequestClose()
    {
        try { return HasExited || _process.CloseMainWindow(); }
        catch (InvalidOperationException) { return true; }
    }

    private void OnExited(object? sender, EventArgs e) => Exited?.Invoke();

    public void Dispose()
    {
        _process.Exited -= OnExited;
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
        IReadOnlyList<BrowserOverride> overrides, Uri startUri)
    {
        if (_active is { HasExited: false })
            throw new InvalidOperationException(
                "An isolated browser is already running. Close it before starting a different preview.");

        if (startUri.Scheme is not ("http" or "https"))
            throw new ArgumentException("The preview URL must start with http:// or https://.", nameof(startUri));

        _active?.Dispose();
        _active = null;

        var rules = BrowserOverrideRules.Build(overrides);
        var profileKey = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(rules)))[..12].ToLowerInvariant();
        var profile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HostsManager", "browser-preview", browser.Kind.ToString().ToLowerInvariant(), profileKey);
        Directory.CreateDirectory(profile);

        var arguments = new[]
        {
            $"--user-data-dir={profile}",
            $"--host-resolver-rules={rules}",
            "--no-first-run",
            "--no-default-browser-check",
            "--new-window",
            startUri.AbsoluteUri,
        };

        var process = UnelevatedProcessLauncher.Start(browser.ExecutablePath, arguments);
        var distinctHosts = overrides.Select(o => o.Hostname)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var description = distinctHosts == 1
            ? $"{overrides[0].Hostname} → {overrides[0].Target}"
            : $"{distinctHosts} domains → {overrides[0].Target}";

        var session = new BrowserPreviewSession(process, browser, description);
        session.Exited += () =>
        {
            if (ReferenceEquals(_active, session)) _active = null;
        };
        _active = session;
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
