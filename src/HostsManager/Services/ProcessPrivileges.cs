using System.Security.Principal;

namespace HostsManager.Services;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class ProcessPrivileges
{
    public static bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity)
                .IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}
