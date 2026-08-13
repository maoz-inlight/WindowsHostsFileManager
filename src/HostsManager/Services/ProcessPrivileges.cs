using System.Security.Principal;

namespace HostsManager.Services;

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
