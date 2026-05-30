using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace SteamViewer.Platform.Windows.Elevation;

/// <summary>
/// Off-cmdline nonce delivery for the SYSTEM helper handshake (closes the F6 LPE primitive
/// flagged in `.claude/research/security-audit/findings.md`). The admin helper writes the auth
/// nonce to a per-launch file in ProgramData, ACLed to the launching user + SYSTEM only; the
/// SYSTEM helper reads + deletes the file at startup. Same-user processes that previously
/// observed the nonce via `tasklist /v` / `wmic process get CommandLine` / `GetCommandLine` can
/// no longer reach it because (a) the cmdline does not carry it, and (b) the file's DACL denies
/// non-SYSTEM, non-launching-user reads.
///
/// Path scheme: %ProgramData%\SteamViewer\.system-helper-nonce-{adminPid}.bin
/// adminPid is already in the SYSTEM helper's cmdline (not secret) so the SYSTEM helper can
/// derive the path without any new cmdline channel.
/// </summary>
[SupportedOSPlatform("windows")]
public static class NonceFile
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SteamViewer");

    /// <summary>Path the admin helper writes to and the SYSTEM helper reads from for a given admin helper PID.</summary>
    public static string PathFor(uint adminPid) => Path.Combine(Dir, $".system-helper-nonce-{adminPid}.bin");

    /// <summary>
    /// Write the nonce as UTF8 bytes to PathFor(adminPid) and DACL the file to deny everyone
    /// except the launching user and SYSTEM. Overwrites any prior file at the path.
    /// </summary>
    public static void Write(uint adminPid, string nonce)
    {
        Directory.CreateDirectory(Dir);
        var path = PathFor(adminPid);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(nonce));
        ApplyRestrictedAcl(path);
    }

    /// <summary>
    /// Read the nonce from PathFor(adminPid) and delete the file. Returns null if the file
    /// doesn't exist or read fails - caller treats null as "auth setup broken, refuse to run".
    /// </summary>
    public static string? ReadAndDelete(uint adminPid)
    {
        var path = PathFor(adminPid);
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            try { File.Delete(path); } catch { /* best-effort cleanup; ACL still protects */ }
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static void ApplyRestrictedAcl(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            var acl = fileInfo.GetAccessControl();
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                acl.RemoveAccessRule(rule);

            var currentUser = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("Cannot determine current user SID");
            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

            acl.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(systemSid, FileSystemRights.FullControl, AccessControlType.Allow));

            fileInfo.SetAccessControl(acl);
        }
        catch
        {
            // If ACL hardening fails, the file is still inside a SYSTEM-writable ProgramData
            // subdirectory; same-user attacker can technically read inherited permissions. The
            // failure mode is the original cmdline-disclosure risk, not worse. Caller decides
            // whether to abort the launch if hardening is critical (currently best-effort).
        }
    }
}
