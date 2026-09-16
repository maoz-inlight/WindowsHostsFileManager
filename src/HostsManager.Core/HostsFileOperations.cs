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
        if (permissions is null) return false;
        var actual = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
        return AccessRulesMatch(actual, permissions);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static bool AccessRulesMatch(FileSecurity actual, FileSecurity expected)
    {
        // Windows may update inheritance bookkeeping when applying a DACL. Compare
        // every ACE and its order, plus inheritance protection, rather than the AI/AR
        // descriptor flags which do not themselves grant or deny access.
        var left = new RawSecurityDescriptor(actual.GetSecurityDescriptorBinaryForm(), 0);
        var right = new RawSecurityDescriptor(expected.GetSecurityDescriptorBinaryForm(), 0);
        const ControlFlags relevant = ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected;
        if ((left.ControlFlags & relevant) != (right.ControlFlags & relevant)) return false;
        if (left.DiscretionaryAcl is null || right.DiscretionaryAcl is null)
            return left.DiscretionaryAcl is null && right.DiscretionaryAcl is null;
        var leftBytes = new byte[left.DiscretionaryAcl.BinaryLength];
        var rightBytes = new byte[right.DiscretionaryAcl.BinaryLength];
        left.DiscretionaryAcl.GetBinaryForm(leftBytes, 0);
        right.DiscretionaryAcl.GetBinaryForm(rightBytes, 0);
        return leftBytes.AsSpan().SequenceEqual(rightBytes);
    }
}
