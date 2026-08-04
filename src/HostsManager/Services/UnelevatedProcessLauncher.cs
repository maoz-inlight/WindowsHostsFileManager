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
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;

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

            var commandLine = new StringBuilder(BuildCommandLine(executable, arguments));
            var startup = new StartupInfo
            {
                Cb = Marshal.SizeOf<StartupInfo>(),
                Desktop = @"winsta0\default",
            };
            var workingDirectory = Path.GetDirectoryName(executable);

            if (!CreateProcessWithTokenW(shellToken, LogonWithProfile, executable, commandLine,
                    CreateUnicodeEnvironment | CreateSuspended, environment, workingDirectory,
                    ref startup, out processInfo))
                ThrowLastWin32("Could not start the browser with the desktop user's permissions");

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
            if (shellToken != IntPtr.Zero) CloseHandle(shellToken);
            CloseHandle(shellProcess);
        }
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

    private static void ThrowLastWin32(string action) =>
        throw new Win32Exception(Marshal.GetLastWin32Error(), action);

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

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags,
        string applicationName, StringBuilder commandLine, uint creationFlags, IntPtr environment,
        string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
