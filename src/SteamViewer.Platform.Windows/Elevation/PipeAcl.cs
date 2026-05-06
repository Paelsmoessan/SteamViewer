using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SteamViewer.Platform.Windows.Elevation;

/// <summary>
/// Builds restrictive PipeSecurity descriptors for helper pipes.
/// Replaces the previous "Authenticated Users" ACL which allowed any local user
/// to connect to elevated/SYSTEM helper pipes (CVE-class local privilege escalation).
/// </summary>
internal static class PipeAcl
{
    /// <summary>
    /// PipeSecurity granting ReadWrite to the current process's user SID only.
    /// Use this when the pipe server runs as the same user as the intended client
    /// (admin helper case — UAC elevation does not change user identity).
    /// </summary>
    public static PipeSecurity CurrentUserOnly()
    {
        var sid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Cannot determine current user SID");
        return ForUserSid(sid);
    }

    /// <summary>
    /// PipeSecurity granting ReadWrite to a specific user SID. Use this when the pipe
    /// server runs as SYSTEM but the intended client runs as a regular user.
    /// </summary>
    public static PipeSecurity ForUserSid(SecurityIdentifier userSid)
    {
        var sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(userSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return sec;
    }
}
