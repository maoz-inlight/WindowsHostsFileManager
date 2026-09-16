using System.Security.AccessControl;

namespace HostsManager.Core;

/// <summary>Small fault-injection boundary for the transaction's OS operations.</summary>
internal class HostsFileOperations
{
    public virtual byte[] Read(string path) => File.ReadAllBytes(path);
    public virtual void Replace(string temp, string destination, string backup) =>
        File.Replace(temp, destination, backup, ignoreMetadataErrors: false);
    public virtual void Move(string temp, string destination) => File.Move(temp, destination, overwrite: true);

    public virtual FileSecurity? CapturePermissions(string path) => OperatingSystem.IsWindows()
        ? new FileInfo(path).GetAccessControl(AccessControlSections.Access) : null;

    public virtual void ApplyPermissions(string path, FileSecurity? permissions)
    {
        if (OperatingSystem.IsWindows())
        {
            if (permissions is null) throw new HostsWriteException("Original file permissions are unavailable. Replacement refused.");
            // A descriptor returned by GetAccessControl has no modified sections.
            // Materialize the captured DACL as a change so SetAccessControl writes it.
            var applied = new FileSecurity();
            applied.SetSecurityDescriptorBinaryForm(permissions.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            new FileInfo(path).SetAccessControl(applied);
        }
    }

    public virtual bool PermissionsMatch(string path, FileSecurity? permissions)
    {
        if (!OperatingSystem.IsWindows()) return true;
        return permissions is not null && new FileInfo(path).GetAccessControl(AccessControlSections.Access)
            .GetSecurityDescriptorSddlForm(AccessControlSections.Access)
            == permissions.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
    }
}
