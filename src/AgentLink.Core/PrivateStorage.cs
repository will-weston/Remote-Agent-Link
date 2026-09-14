using System.Security.AccessControl;
using System.Security.Principal;

namespace AgentLink.Core;

public static class PrivateStorage
{
    public static void EnsureDirectory(string directory)
    {
        var info = Directory.CreateDirectory(directory);
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new InvalidOperationException("Missing Windows identity.");
        var security = new DirectorySecurity();
        if (!sid.Equals(info.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier))))
            security.SetOwner(sid);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }
}
