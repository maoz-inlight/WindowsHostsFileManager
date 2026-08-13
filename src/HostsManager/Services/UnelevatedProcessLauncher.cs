using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace HostsManager.Services;

/// <summary>
/// Starts a child with the interactive shell's medium-integrity token when this app is
/// elevated. A browser must never inherit Hosts Manager's administrator token.
/// </summary>
internal static class UnelevatedProcessLauncher
{
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint MaximumAllowed = 0x02000000;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint DisableMaxPrivilege = 0x00000001;
    private const uint LuaToken = 0x00000004;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;

    public static Process Start(string executable, IReadOnlyList<string> arguments)
    {
        if (!IsProcessElevated(Process.GetCurrentProcess().Handle))
            return StartNormally(executable, arguments);

        return StartWithShellToken(executable, arguments);
    }

    private static Process StartNormally(string executable, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        return Process.Start(start)
            ?? throw new InvalidOperationException("Windows did not start the browser process.");
    }

    private static Process StartWithShellToken(string executable, IReadOnlyList<string> arguments)
    {
        var shellWindow = GetShellWindow();
        if (shellWindow == IntPtr.Zero)
            throw new InvalidOperationException("Could not find the Windows desktop shell needed for a safe browser launch.");

        GetWindowThreadProcessId(shellWindow, out var shellPid);
        var shellProcess = OpenProcess(ProcessQueryLimitedInformation, false, shellPid);
        if (shellProcess == IntPtr.Zero) ThrowLastWin32("Could not open the Windows desktop shell");

        IntPtr shellToken = IntPtr.Zero;
        IntPtr primaryToken = IntPtr.Zero;
        IntPtr restrictedToken = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        ProcessInformation processInfo = default;

        try
        {
            if (!OpenProcessToken(shellProcess,
                    TokenAssignPrimary | TokenDuplicate | TokenQuery, out shellToken))
                ThrowLastWin32("Could not read the Windows desktop shell token");

            if (IsTokenElevated(shellToken))
                throw new InvalidOperationException(
                    "Windows did not provide a non-administrator desktop token, so the browser was not opened.");

            if (!CreateEnvironmentBlock(out environment, shellToken, false))
                ThrowLastWin32("Could not create the browser environment");

            var startup = new StartupInfo
            {
                Cb = Marshal.SizeOf<StartupInfo>(),
                Desktop = @"winsta0\default",
            };
            var workingDirectory = Path.GetDirectoryName(executable);
            var flags = CreateUnicodeEnvironment | CreateSuspended;
            var failures = new List<string>();

            // Windows offers two ways to hand the shell's token to a new process, and machines
            // differ in which one they allow. CreateProcessAsUser is tried first because it asks
            // the kernel directly; CreateProcessWithTokenW goes through the Secondary Logon
            // service, which answers ERROR_ACCESS_DENIED on some configurations even when the
            // caller holds every privilege the API documents.
            if (DuplicateTokenEx(shellToken, MaximumAllowed, IntPtr.Zero,
                    SecurityImpersonation, TokenPrimary, out primaryToken))
            {
                if (!CreateProcessAsUser(primaryToken, executable, NewCommandLine(),
                        IntPtr.Zero, IntPtr.Zero, false, flags, environment, workingDirectory,
                        ref startup, out processInfo))
                {
                    failures.Add(DescribeLastError("Starting it directly as the desktop user"));
                    processInfo = default;
                }
            }
            else
            {
                failures.Add(DescribeLastError("Copying the desktop user's token"));
                primaryToken = IntPtr.Zero;
            }

            if (processInfo.Process == IntPtr.Zero
                && !CreateProcessWithTokenW(shellToken, LogonWithProfile, executable, NewCommandLine(),
                    flags, environment, workingDirectory, ref startup, out processInfo))
            {
                failures.Add(DescribeLastError("Starting it through the Secondary Logon service"));
                processInfo = default;
            }

            // Both routes above hand over a token this process did not create, and the kernel
            // demands SeAssignPrimaryTokenPrivilege for that unless the token is a child or
            // sibling of our own. An elevated app holds a duplicate of the full administrator
            // token, so the shell's token is neither and the attempts fail with
            // ERROR_PRIVILEGE_NOT_HELD. A restricted copy of our own token *is* a child of it,
            // so it can always be assigned. Stripping Administrators and dropping to medium
            // integrity is exactly how Windows builds the desktop user's own filtered token,
            // and it is only equivalent while we are running as that same user.
            if (processInfo.Process == IntPtr.Zero && RunsAsSameUserAs(shellToken))
            {
                if (TryCreateUnprivilegedToken(out restrictedToken))
                {
                    if (!CreateProcessAsUser(restrictedToken, executable, NewCommandLine(),
                            IntPtr.Zero, IntPtr.Zero, false, flags, environment, workingDirectory,
                            ref startup, out processInfo))
                    {
                        failures.Add(DescribeLastError("Starting it with a non-administrator copy of this app's token"));
                        processInfo = default;
                    }
                }
                else
                {
                    failures.Add(DescribeLastError("Building a non-administrator copy of this app's token"));
                    restrictedToken = IntPtr.Zero;
                }
            }

            if (processInfo.Process == IntPtr.Zero)
                throw new InvalidOperationException(
                    "Could not start the browser with the desktop user's permissions."
                    + Environment.NewLine + string.Join(Environment.NewLine,
                        failures.Select(failure => "  • " + failure)));

            // Verify before any browser code runs. If Windows ever gives us an elevated
            // child despite the requested token, terminate it while it is still suspended.
            if (IsProcessElevated(processInfo.Process))
            {
                TerminateProcess(processInfo.Process, 1);
                throw new InvalidOperationException(
                    "The isolated browser would have run as administrator, so launch was cancelled.");
            }

            if (ResumeThread(processInfo.Thread) == uint.MaxValue)
            {
                TerminateProcess(processInfo.Process, 1);
                ThrowLastWin32("Could not resume the isolated browser");
            }

            return Process.GetProcessById((int)processInfo.ProcessId);
        }
        finally
        {
            if (processInfo.Thread != IntPtr.Zero) CloseHandle(processInfo.Thread);
            if (processInfo.Process != IntPtr.Zero) CloseHandle(processInfo.Process);
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            if (restrictedToken != IntPtr.Zero) CloseHandle(restrictedToken);
            if (primaryToken != IntPtr.Zero) CloseHandle(primaryToken);
            if (shellToken != IntPtr.Zero) CloseHandle(shellToken);
            CloseHandle(shellProcess);
        }

        // CreateProcess is allowed to write into the command line buffer, so each attempt
        // needs its own copy.
        StringBuilder NewCommandLine() => new(BuildCommandLine(executable, arguments));
    }

    private static bool IsProcessElevated(IntPtr process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token))
            ThrowLastWin32("Could not inspect a process security token");

        try { return IsTokenElevated(token); }
        finally { CloseHandle(token); }
    }

    private static bool IsTokenElevated(IntPtr token)
    {
        var size = Marshal.SizeOf<TokenElevation>();
        if (!GetTokenInformation(token, TokenInformationClass.TokenElevation,
                out var elevation, size, out _))
            ThrowLastWin32("Could not inspect process elevation");

        return elevation.TokenIsElevated != 0;
    }

    private static bool RunsAsSameUserAs(IntPtr otherToken)
    {
        IntPtr currentToken = IntPtr.Zero;
        IntPtr currentUser = IntPtr.Zero;
        IntPtr otherUser = IntPtr.Zero;

        try
        {
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TokenQuery, out currentToken))
                ThrowLastWin32("Could not read this app's user token");

            currentUser = ReadTokenUser(currentToken);
            otherUser = ReadTokenUser(otherToken);

            // TOKEN_USER starts with SID_AND_ATTRIBUTES, whose first member is the SID
            // pointer. The backing buffers remain alive for the comparison.
            return EqualSid(Marshal.ReadIntPtr(currentUser), Marshal.ReadIntPtr(otherUser));
        }
        finally
        {
            if (otherUser != IntPtr.Zero) Marshal.FreeHGlobal(otherUser);
            if (currentUser != IntPtr.Zero) Marshal.FreeHGlobal(currentUser);
            if (currentToken != IntPtr.Zero) CloseHandle(currentToken);
        }
    }

    private static IntPtr ReadTokenUser(IntPtr token)
    {
        GetTokenInformationBuffer(token, TokenInformationClass.TokenUser,
            IntPtr.Zero, 0, out var size);
        if (size <= 0) ThrowLastWin32("Could not determine the user-token buffer size");

        var buffer = Marshal.AllocHGlobal(size);
        if (!GetTokenInformationBuffer(token, TokenInformationClass.TokenUser,
                buffer, size, out _))
        {
            Marshal.FreeHGlobal(buffer);
            ThrowLastWin32("Could not inspect a user token");
        }

        return buffer;
    }

    private static bool TryCreateUnprivilegedToken(out IntPtr restrictedToken)
    {
        restrictedToken = IntPtr.Zero;

        if (!OpenProcessToken(Process.GetCurrentProcess().Handle,
                TokenAssignPrimary | TokenDuplicate | TokenQuery, out var currentToken))
            return false;

        try
        {
            // LUA_TOKEN builds the same limited-user form Windows uses for UAC, while
            // DISABLE_MAX_PRIVILEGE removes every remaining privilege except traversal.
            // Because this is derived from our own primary token, CreateProcessAsUser
            // does not require SeAssignPrimaryTokenPrivilege for the result.
            return CreateRestrictedToken(currentToken, DisableMaxPrivilege | LuaToken,
                0, IntPtr.Zero, 0, IntPtr.Zero, 0, IntPtr.Zero, out restrictedToken);
        }
        finally
        {
            CloseHandle(currentToken);
        }
    }

    private static string BuildCommandLine(string executable, IEnumerable<string> arguments) =>
        string.Join(' ', new[] { executable }.Concat(arguments).Select(QuoteArgument));

    internal static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
            return argument;

        var result = new StringBuilder(argument.Length + 2).Append('"');
        var backslashes = 0;

        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
                result.Append('\\', backslashes * 2 + 1).Append('"');
            else
                result.Append('\\', backslashes).Append(character);

            backslashes = 0;
        }

        result.Append('\\', backslashes * 2).Append('"');
        return result.ToString();
    }

    // Win32Exception(code, message) reports only the message, so the number that actually
    // explains the failure has to be spelled out for it to reach the user.
    private static void ThrowLastWin32(string action)
    {
        var error = Marshal.GetLastWin32Error();
        throw new Win32Exception(error, $"{action} ({DescribeError(error)}).");
    }

    private static string DescribeLastError(string action) =>
        $"{action} failed: {DescribeError(Marshal.GetLastWin32Error())}.";

    private static string DescribeError(int error) =>
        $"Windows error {error} — {new Win32Exception(error).Message.TrimEnd('.')}";

    private const uint LogonWithProfile = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Cb;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public IntPtr ReservedPointer, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevation { public int TokenIsElevated; }

    private enum TokenInformationClass { TokenUser = 1, TokenElevation = 20 }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, TokenInformationClass informationClass,
        out TokenElevation information, int informationLength, out int returnLength);

    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    private static extern bool GetTokenInformationBuffer(IntPtr token,
        TokenInformationClass informationClass, IntPtr information,
        int informationLength, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool EqualSid(IntPtr sid1, IntPtr sid2);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess,
        IntPtr tokenAttributes, int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CreateRestrictedToken(IntPtr existingToken, uint flags,
        uint disableSidCount, IntPtr sidsToDisable, uint deletePrivilegeCount,
        IntPtr privilegesToDelete, uint restrictedSidCount, IntPtr sidsToRestrict,
        out IntPtr newToken);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags,
        string applicationName, StringBuilder commandLine, uint creationFlags, IntPtr environment,
        string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUser(IntPtr token, string applicationName,
        StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
