using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using HostsManager.Core;
using Microsoft.Win32.SafeHandles;

namespace HostsManager.Services;

/// <summary>
/// The elevated helper serves one private pipe. Both ends check the peer process.
/// Backup I/O impersonates the initiating client, including alternate-account UAC.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ElevatedHostsFileCommitter : IHostsWriteCommitter
{
    public const string HelperArgument = "--elevated-write-request";
    private const int MaximumMessageBytes = 64 * 1024 * 1024;
    private readonly string _allowedTarget;
    private readonly Func<ProcessStartInfo, Process?> _start;

    public ElevatedHostsFileCommitter() : this(HostsFileWriter.DefaultHostsPath, Process.Start) { }
    // Used by the integration host to exercise the actual protocol against fixtures.
    internal ElevatedHostsFileCommitter(string allowedTarget, Func<ProcessStartInfo, Process?> start)
    {
        _allowedTarget = Path.GetFullPath(allowedTarget);
        _start = start;
    }

    public SaveResult Commit(PreparedHostsWrite request)
    {
        ValidateWriteScope(request, _allowedTarget);
        using var identity = WindowsIdentity.GetCurrent();
        using var parent = Process.GetCurrentProcess();
        var endpoint = new Endpoint(Guid.NewGuid().ToString("N"), parent.Id,
            parent.StartTime.ToUniversalTime().Ticks, identity.User!.Value);
        var executable = Environment.ProcessPath
            ?? throw new HostsWriteException("Could not locate Hosts Manager.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = true, Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        start.ArgumentList.Add(HelperArgument);
        start.ArgumentList.Add(endpoint.ToArgument());

        try
        {
            using var helper = _start(start) ?? throw new HostsWriteException("Windows did not start the save helper.");
            using var pipe = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            _ = helper.Handle;
            var connecting = pipe.ConnectAsync(timeout.Token);
            var exited = helper.WaitForExitAsync(timeout.Token);
            Task.WhenAny(connecting, exited).GetAwaiter().GetResult();
            if (!connecting.IsCompletedSuccessfully && helper.HasExited)
            {
                timeout.Cancel();
                throw new HostsWriteException($"The save helper exited with code {helper.ExitCode} before connecting. The hosts file was not changed.");
            }
            connecting.GetAwaiter().GetResult();
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) || serverPid != helper.Id)
                throw new HostsWriteException("The save connection does not belong to the launched helper.");
            // Holding the process handle also prevents accepting a reused PID.
            _ = helper.Handle;
            WriteMessage(pipe, request, timeout.Token);
            var response = ReadMessage<HelperResponse>(pipe, timeout.Token);
            if (response.Success)
                return new SaveResult(true, response.Message, response.BackupPath, response.RolledBack);
            if (response.ErrorKind == nameof(HostsDriftException)) throw new HostsDriftException(response.Message);
            throw new HostsWriteException(response.Message);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new HostsWriteException("Administrator approval was cancelled. The hosts file was not changed.", ex);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // Never kill a helper that might already be committing a file.
            throw new HostsWriteException(
                "The save helper did not return a confirmed result. The final file state is unknown. " +
                "Reload the file and inspect its backups before retrying.", ex);
        }
    }

    public static int RunHelper(string argument) =>
        RunHelperCore(argument, HostsFileWriter.DefaultHostsPath, requireAdministrator: true);

    internal static int RunHelperCore(string argument, string allowedTarget, bool requireAdministrator)
    {
        NamedPipeServerStream? pipe = null;
        try
        {
            var endpoint = Endpoint.Parse(argument);
            if (requireAdministrator && !ProcessPrivileges.IsAdministrator)
                throw new HostsWriteException("The save helper did not receive administrator permission.");
            pipe = CreateServerPipe(endpoint);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            pipe.WaitForConnectionAsync(timeout.Token).GetAwaiter().GetResult();
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientPid)
                || clientPid != endpoint.ParentId)
                throw new HostsWriteException("The save connection does not belong to the initiating process.");

            var request = ReadMessage<PreparedHostsWrite>(pipe, timeout.Token);
            ValidateWriteScope(request, allowedTarget);
            var access = new ClientBackupAccess(pipe, endpoint.UserSid);
            // Query the caller under its own token. A different administrator may not
            // have access to the caller's process DACL, even though UAC was approved.
            // Keep this handle alive so an exited caller's PID cannot be reused here.
            using var caller = access.Run(() => HoldCallerProcess(endpoint));
            var backups = new BackupManager(request.BackupsRoot, hostsPath: request.HostsPath, access: access);
            var writer = new HostsFileWriter(request.HostsPath, backups);
            var result = writer.CommitPrepared(request);
            WriteMessage(pipe, new HelperResponse(true, result.Message, result.BackupPath,
                result.RolledBack, null), timeout.Token);
            return 0;
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
            if (pipe?.IsConnected == true)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    WriteMessage(pipe, new HelperResponse(false, ex.Message, null, false, ex.GetType().Name), timeout.Token);
                }
                catch (Exception) { /* The parent reports an unconfirmed outcome. */ }
            }
            return 1;
        }
        finally { pipe?.Dispose(); }
    }

    public static bool IsDefaultHostsPath(string? path) =>
        TargetIdentity.PathsEqual(TargetIdentity.CanonicalPath(path ?? HostsFileWriter.DefaultHostsPath),
            Path.GetFullPath(HostsFileWriter.DefaultHostsPath));

    internal static void ValidateWriteScope(PreparedHostsWrite request, string allowedTarget)
    {
        // Check both lexical and resolved paths: a reparse point must not redirect the
        // privileged write. Custom files are written by the ordinary process instead.
        if (!TargetIdentity.PathsEqual(request.HostsPath, allowedTarget)
            || !TargetIdentity.PathsEqual(TargetIdentity.CanonicalPath(request.HostsPath), allowedTarget))
            throw new HostsWriteException("The elevated helper is restricted to the Windows hosts file.");
        if (!Path.IsPathFullyQualified(request.BackupsRoot))
            throw new HostsWriteException("The backup root must be an absolute path.");
        if (request.Bytes is null || request.Bytes.Length == 0 || !Enum.IsDefined(request.Operation))
            throw new HostsWriteException("Invalid hosts write request.");
    }

    private sealed class ClientBackupAccess(NamedPipeServerStream pipe, string userSid) : IBackupFileAccess
    {
        public T Run<T>(Func<T> operation)
        {
            T result = default!;
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent(true);
                if (identity?.User?.Value != userSid || identity.ImpersonationLevel < TokenImpersonationLevel.Impersonation)
                    throw new HostsWriteException("The initiating user's backup permissions could not be established.");
                result = operation();
            });
            return result;
        }
    }

    private static NamedPipeServerStream CreateServerPipe(Endpoint endpoint)
    {
        // Set the medium integrity label at creation. The managed ACL factory treats
        // any SACL as an auditing request and asks for SeSecurityPrivilege, which is
        // unnecessary for this mandatory label and is not normally enabled.
        var descriptor = new RawSecurityDescriptor(
            $"D:P(D;;GA;;;NU)(A;;GA;;;BA)(A;;GA;;;SY)(A;;GRGW;;;{endpoint.UserSid})S:(ML;;NW;;;ME)");
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pinned.AddrOfPinnedObject(),
            };
            // Duplex, overlapped, first-instance-only; reject remote clients.
            var handle = CreateNamedPipe(@"\\.\pipe\" + endpoint.PipeName,
                0x00000003 | 0x40000000 | 0x00080000, 0x00000008, 1, 4096, 4096, 0, ref attributes);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }
            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle); }
            catch { handle.Dispose(); throw; }
        }
        finally { pinned.Free(); }
    }

    private static SafeProcessHandle HoldCallerProcess(Endpoint endpoint)
    {
        var handle = OpenProcess(0x00101000, false, endpoint.ParentId); // synchronize + limited query
        try
        {
            if (handle.IsInvalid || !GetProcessTimes(handle, out var created, out _, out _, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (DateTime.FromFileTimeUtc(created).Ticks != endpoint.ParentStartTicks
                || WaitForSingleObject(handle, 0) != 0x102)
                throw new HostsWriteException("The initiating process is no longer available.");
            var executable = new StringBuilder(32768);
            var length = executable.Capacity;
            if (!QueryFullProcessImageName(handle, 0, executable, ref length)
                || Environment.ProcessPath is null
                || !TargetIdentity.PathsEqual(executable.ToString(), Environment.ProcessPath))
                throw new HostsWriteException("The initiating process is not this Hosts Manager executable.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    internal sealed record Endpoint(string Nonce, int ParentId, long ParentStartTicks, string UserSid)
    {
        public string PipeName => "HostsManager.Write." + Nonce;
        public string ToArgument() => $"{Nonce},{ParentId},{ParentStartTicks},{UserSid}";
        public static Endpoint Parse(string argument)
        {
            var parts = argument.Split(',');
            if (parts.Length != 4 || !Guid.TryParseExact(parts[0], "N", out _)
                || !int.TryParse(parts[1], out var pid) || pid <= 0
                || !long.TryParse(parts[2], out var ticks) || ticks <= 0)
                throw new HostsWriteException("Invalid helper endpoint.");
            var sid = new SecurityIdentifier(parts[3]);
            return new Endpoint(parts[0], pid, ticks, sid.Value);
        }
    }

    internal static void WriteMessage<T>(Stream pipe, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaximumMessageBytes) throw new HostsWriteException("The write request is too large.");
        pipe.WriteAsync(BitConverter.GetBytes(bytes.Length), token).AsTask().GetAwaiter().GetResult();
        pipe.WriteAsync(bytes, token).AsTask().GetAwaiter().GetResult();
        pipe.FlushAsync(token).GetAwaiter().GetResult();
    }

    internal static T ReadMessage<T>(Stream pipe, CancellationToken token)
    {
        var header = new byte[4];
        pipe.ReadExactlyAsync(header, token).AsTask().GetAwaiter().GetResult();
        var length = BitConverter.ToInt32(header);
        if (length <= 0 || length > MaximumMessageBytes) throw new HostsWriteException("Invalid helper message length.");
        var bytes = new byte[length];
        pipe.ReadExactlyAsync(bytes, token).AsTask().GetAwaiter().GetResult();
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new HostsWriteException("The helper message was empty.");
    }

    private sealed record HelperResponse(bool Success, string Message, string? BackupPath, bool RolledBack, string? ErrorKind);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out int processId);
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode,
        uint maxInstances, uint outBuffer, uint inBuffer, uint timeout, ref SecurityAttributes attributes);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint timeout);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
}
