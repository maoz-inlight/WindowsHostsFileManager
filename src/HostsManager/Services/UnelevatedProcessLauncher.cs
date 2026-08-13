using System.Diagnostics;

namespace HostsManager.Services;

/// <summary>
/// Browsers inherit the ordinary desktop token from the ordinary Hosts Manager UI.
/// A manually elevated app is refused instead of manufacturing a partial token that
/// Chromium's broker and sandbox can reject or crash under.
/// </summary>
internal static class UnelevatedProcessLauncher
{
    public static Process Start(string executable, IReadOnlyList<string> arguments)
    {
        if (ProcessPrivileges.IsAdministrator)
            throw new InvalidOperationException(
                "Hosts Manager is running as administrator. Close it and reopen it normally, " +
                "then open the isolated browser again.");

        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        return Process.Start(start)
            ?? throw new InvalidOperationException("Windows did not start the browser process.");
    }
}
