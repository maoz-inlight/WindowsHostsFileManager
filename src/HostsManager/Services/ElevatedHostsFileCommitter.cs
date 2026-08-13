using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using HostsManager.Core;

namespace HostsManager.Services;

/// <summary>
/// Hands one already verified hosts-file replacement to a short-lived elevated copy of
/// this executable. The helper accepts only the real Windows hosts path and the app's
/// own backup directory, so the command line cannot be repurposed as an arbitrary
/// administrator file writer.
/// </summary>
internal sealed class ElevatedHostsFileCommitter : IHostsWriteCommitter
{
    public const string HelperArgument = "--elevated-write-request";
    private const int UacCancelled = 1223;

    private static string RequestDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HostsManager", "elevation");

    public SaveResult Commit(PreparedHostsWrite request)
    {
        ValidateWriteScope(request);

        Directory.CreateDirectory(RequestDirectory);
        var id = Guid.NewGuid().ToString("N");
        var requestPath = Path.Combine(RequestDirectory, $"write-{id}.json");
        var resultPath = ResultPath(requestPath);
        File.WriteAllText(requestPath, JsonSerializer.Serialize(request));

        try
        {
            var executable = Environment.ProcessPath
                ?? throw new HostsWriteException("Could not locate the running Hosts Manager executable.");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
            };
            start.ArgumentList.Add(HelperArgument);
            start.ArgumentList.Add(requestPath);

            using var process = Process.Start(start)
                ?? throw new HostsWriteException("Windows did not start the elevated save helper.");
            process.WaitForExit();

            if (!File.Exists(resultPath))
                throw new HostsWriteException(
                    $"The elevated save helper exited with code {process.ExitCode} without returning a result. " +
                    "The hosts file was not reported as saved.");

            var response = JsonSerializer.Deserialize<HelperResponse>(File.ReadAllText(resultPath))
                ?? throw new HostsWriteException("The elevated save helper returned an empty result.");

            if (response.Success)
                return new SaveResult(true, response.Message, response.BackupPath, response.RolledBack);

            if (response.ErrorKind == nameof(HostsDriftException))
                throw new HostsDriftException(response.Message);

            throw new HostsWriteException(response.Message);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == UacCancelled)
        {
            throw new HostsWriteException(
                "Administrator approval was cancelled. The hosts file was not changed.", ex);
        }
        finally
        {
            TryDelete(requestPath);
            TryDelete(resultPath);
        }
    }

    /// <summary>Runs before any UI or single-instance state is created.</summary>
    public static int RunHelper(string requestPath)
    {
        if (!IsAllowedRequestPath(requestPath)) return 2;

        var resultPath = ResultPath(requestPath);
        try
        {
            if (!ProcessPrivileges.IsAdministrator)
                throw new HostsWriteException("The save helper did not receive administrator permission.");

            var request = JsonSerializer.Deserialize<PreparedHostsWrite>(File.ReadAllText(requestPath))
                ?? throw new HostsWriteException("The elevated write request was empty.");
            ValidateWriteScope(request);

            var writer = new HostsFileWriter(
                request.HostsPath,
                new BackupManager(request.BackupsDirectory));
            var result = writer.CommitPrepared(request);

            WriteResponse(resultPath, new HelperResponse(
                true, result.Message, result.BackupPath, result.RolledBack, null));
            return 0;
        }
        catch (Exception ex)
        {
            WriteResponse(resultPath, new HelperResponse(
                false, ex.Message, null, false, ex.GetType().Name));
            return 1;
        }
    }

    public static bool IsDefaultHostsPath(string? hostsPath)
    {
        var candidate = hostsPath ?? HostsFileWriter.DefaultHostsPath;
        return PathsEqual(candidate, HostsFileWriter.DefaultHostsPath);
    }

    private static void ValidateWriteScope(PreparedHostsWrite request)
    {
        if (!PathsEqual(request.HostsPath, HostsFileWriter.DefaultHostsPath))
            throw new HostsWriteException(
                "The elevated helper is restricted to the Windows hosts file.");

        if (!PathsEqual(request.BackupsDirectory, BackupManager.DefaultDirectory))
            throw new HostsWriteException(
                "The elevated helper is restricted to the Hosts Manager backup directory.");

        if (request.Bytes.Length == 0)
            throw new HostsWriteException("Refusing to replace the hosts file with an empty request.");
    }

    private static bool IsAllowedRequestPath(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            return string.Equals(Path.GetDirectoryName(fullPath), Path.GetFullPath(RequestDirectory),
                       StringComparison.OrdinalIgnoreCase)
                   && Path.GetFileName(fullPath).StartsWith("write-", StringComparison.Ordinal)
                   && string.Equals(Path.GetExtension(fullPath), ".json", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string ResultPath(string requestPath) =>
        Path.Combine(Path.GetDirectoryName(requestPath)!,
            Path.GetFileNameWithoutExtension(requestPath) + ".result.json");

    private static bool PathsEqual(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second),
            StringComparison.OrdinalIgnoreCase);

    private static void WriteResponse(string path, HelperResponse response)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(response)); }
        catch { /* The parent reports a missing result without trusting an exit code. */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record HelperResponse(
        bool Success,
        string Message,
        string? BackupPath,
        bool RolledBack,
        string? ErrorKind);
}
