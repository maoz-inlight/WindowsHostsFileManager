using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HostsManager.Core;

/// <summary>Coordinates our processes only; unrelated tools do not participate.</summary>
internal sealed class TargetWriteLock : IDisposable
{
    private readonly Mutex _mutex;
    private TargetWriteLock(Mutex mutex) => _mutex = mutex;

    public static TargetWriteLock Acquire(string canonicalPath)
    {
        var name = "HostsManager.Write." + TargetIdentity.Key(canonicalPath);
        var mutex = OperatingSystem.IsWindows() ? CreateWindowsMutex(@"Global\" + name) : new Mutex(false, name);
        try
        {
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(30)))
                    throw new HostsWriteException("Another Hosts Manager write is still running. Try again after it finishes.");
            }
            catch (AbandonedMutexException) { /* Re-read all state after a previous writer died. */ }
            return new TargetWriteLock(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }

    private static Mutex CreateWindowsMutex(string name)
    {
        // Authenticated users may wait/release, including a different UAC administrator.
        // The lock grants no file access. Denial of access fails closed, never falls back
        // to a different lock namespace. Medium integrity permits an ordinary UI to join.
        const string descriptor = "D:P(A;;0x00100001;;;AU)(A;;GA;;;SY)S:(ML;;NW;;;ME)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(descriptor, 1, out var security, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = security };
            var handle = CreateMutexEx(ref attributes, name, 0, 0x00100001);
            if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            var mutex = new Mutex(false);
            mutex.SafeWaitHandle = handle;
            return mutex;
        }
        finally { LocalFree(security); }
    }

    public void Dispose() { _mutex.ReleaseMutex(); _mutex.Dispose(); }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll", EntryPoint = "CreateMutexExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateMutexEx(ref SecurityAttributes attributes, string name, uint flags, uint access);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
