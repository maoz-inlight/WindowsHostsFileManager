using System.Diagnostics;

namespace HostsManager.Services;

public static class DnsFlusher
{
    /// <summary>
    /// Clears the resolver cache so hosts-file changes take effect immediately.
    /// Without this a toggled domain can keep resolving to its old address until the
    /// cached entry expires, which looks exactly like the app not having worked.
    /// </summary>
    public static (bool Success, string Message) Flush()
    {
        try
        {
            var info = new ProcessStartInfo("ipconfig", "/flushdns")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = Process.Start(info);
            if (process is null) return (false, "Could not start ipconfig.");

            process.WaitForExit(10_000);
            return process.ExitCode == 0
                ? (true, "DNS cache flushed.")
                : (false, $"ipconfig /flushdns exited with code {process.ExitCode}.");
        }
        catch (Exception ex)
        {
            return (false, $"Could not flush the DNS cache: {ex.Message}");
        }
    }
}
