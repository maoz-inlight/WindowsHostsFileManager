using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using HostsManager.Core;

namespace HostsManager.Services;

/// <summary>Match only browser roots launched with this exact isolated user-data directory.</summary>
internal static class BrowserProfileProcesses
{
    internal static bool Matches(IReadOnlyList<string> arguments, string profilePath)
    {
        string? value = null;
        for (var i = 1; i < arguments.Count; i++)
        {
            var arg = arguments[i];
            if (arg == "--type" || arg.StartsWith("--type=", StringComparison.Ordinal)) return false;
            if (arg.StartsWith("--user-data-dir=", StringComparison.Ordinal)) value = arg[16..];
            else if (arg == "--user-data-dir" && i + 1 < arguments.Count) value = arguments[++i];
        }
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return false;
        return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(profilePath)), StringComparison.OrdinalIgnoreCase);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static IReadOnlyList<int> Find(BrowserPreviewProfile profile)
    {
        var name = profile.Browser switch { "chrome" => "chrome.exe", "edge" => "msedge.exe", _ => throw new ArgumentException("Unsupported browser.") };
        object? locator = null, service = null, rows = null;
        try
        {
            locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator")!);
            service = ((dynamic)locator!).ConnectServer(".", "root\\cimv2");
            using var current = Process.GetCurrentProcess();
            rows = ((dynamic)service).ExecQuery($"SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='{name}' AND SessionId={current.SessionId}");
            var ids = new List<int>();
            foreach (object row in (IEnumerable)rows)
            {
                try
                {
                    string? command = ReadProperty(row, "CommandLine") as string;
                    if (command is null)
                        throw new IOException("Windows could not identify a browser process. Preview profile changes were not applied.");
                    if (Matches(Split(command), profile.Path)) ids.Add(Convert.ToInt32(ReadProperty(row, "ProcessId")));
                }
                finally { Marshal.FinalReleaseComObject(row); }
            }
            return ids;
        }
        catch (COMException ex) { throw new IOException("Could not check whether this preview profile is running. Please retry.", ex); }
        finally
        {
            if (rows is not null) Marshal.FinalReleaseComObject(rows);
            if (service is not null) Marshal.FinalReleaseComObject(service);
            if (locator is not null) Marshal.FinalReleaseComObject(locator);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static object? ReadProperty(object row, string name)
    {
        object properties = ((dynamic)row).Properties_;
        object? property = null;
        try
        {
            property = ((dynamic)properties).Item(name, 0);
            return ((dynamic)property).Value;
        }
        finally
        {
            if (property is not null) Marshal.FinalReleaseComObject(property);
            Marshal.FinalReleaseComObject(properties);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static void CloseBackground(BrowserPreviewProfile profile)
    {
        // Called only after explicit confirmation. Requery ownership immediately before
        // stopping each process; normal profiles and visible preview windows are excluded.
        foreach (var id in Find(profile))
        {
            try
            {
                using var process = Process.GetProcessById(id);
                if (process.MainWindowHandle != IntPtr.Zero)
                    throw new InvalidOperationException("This preview still has an open window. Close it normally first.");
                if (!Find(profile).Contains(id) || process.HasExited) continue;
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero)
                    throw new InvalidOperationException("A preview window opened. Close it normally first.");
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(5000)) throw new IOException("The background preview is still stopping. Please retry shortly.");
            }
            catch (ArgumentException) { /* Process already exited. */ }
        }
    }

    private static string[] Split(string command)
    {
        var buffer = CommandLineToArgvW(command, out var count);
        if (buffer == IntPtr.Zero) throw new IOException("Could not read browser command line.");
        try
        {
            var result = new string[count];
            for (var i = 0; i < count; i++) result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, i * IntPtr.Size))!;
            return result;
        }
        finally { LocalFree(buffer); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
