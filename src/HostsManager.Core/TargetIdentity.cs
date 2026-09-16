using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HostsManager.Core;

/// <summary>Existing-file identity for backup scopes and cooperating writer locks.</summary>
public static class TargetIdentity
{
    public static string CanonicalPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) return full;
        if (!OperatingSystem.IsWindows())
            return new FileInfo(full).ResolveLinkTarget(true)?.FullName ?? full;

        using var handle = File.OpenHandle(full, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
        var resolved = buffer.ToString();
        if (resolved.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + resolved[8..];
        return resolved.StartsWith(@"\\?\", StringComparison.Ordinal) ? resolved[4..] : resolved;
    }

    public static bool PathsEqual(string first, string second) => string.Equals(
        Path.GetFullPath(first), Path.GetFullPath(second),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public static string Key(string canonicalPath) => HostsDocument.Sha256(Encoding.UTF8.GetBytes(
        OperatingSystem.IsWindows() ? canonicalPath.ToUpperInvariant() : canonicalPath));

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
}
