using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using SteamViewer.Common.Protocol;

namespace SteamViewer.Platform.Windows.Elevation;

// CODE-HEALTH-EXEMPT (shelved-feature cap)
// SAS / Ctrl+Alt+Del + token-privilege management for SystemHelperServer.
//
// FROZEN: SAS is deferred until installer pipeline exists per TODO.md L370.
// UIAccess flag bypass requires Authenticode-signed binary in a trusted
// location; dev builds satisfy neither. Do NOT refactor method bodies here
// until installer-service rewrite lands - the work will be replaced as a
// unit (per .claude/research/sendsas-ctrl-alt-del/research.md Option A), not
// patched in place.
// See: .claude/research/codescene-clean-delivery/intrinsic-caps.md
public static partial class SystemHelperServer
{
    [DllImport("sas.dll", SetLastError = true)]
    private static extern void SendSAS(bool asUser);

    // Token privilege management — needed to enable SeTcbPrivilege for SendSAS
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;
    private const string SE_TCB_NAME = "SeTcbPrivilege";

    // Runtime UIAccess — set TokenUIAccess flag on process token to qualify for SendSAS(true).
    // Requires SeTcbPrivilege (SYSTEM has it). Bypasses signing + protected location checks.
    // Source: .claude/research/sendsas-ctrl-alt-del/research.md (Tyranid's Lair, James Forshaw)
    private const int TokenUIAccess = 26;

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetTokenInformation(IntPtr tokenHandle,
        int tokenInformationClass, ref uint tokenInformation, uint tokenInformationLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle, [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState, int bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    // Winlogon token impersonation — fallback for SendSAS when process token lacks SeTcbPrivilege
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImpersonateLoggedOnUser(IntPtr hToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RevertToSelf();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess,
        IntPtr lpTokenAttributes, int impersonationLevel, int tokenType, out IntPtr phNewToken);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint MAXIMUM_ALLOWED = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenImpersonation = 2;

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public int PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privileges;
    }

    private static string HandleSendSAS()
    {
        try
        {
            // Re-check registry before each call — GPO can overwrite between calls
            EnsureSoftwareSASEnabled();

            if (!EnableTcbPrivilege())
            {
                DebugLog("SAS: SeTcbPrivilege not available — trying winlogon impersonation");
                if (!CallSendSASWithImpersonation())
                    return JsonSerializer.Serialize(new HelperResponse(false, "SeTcbPrivilege unavailable and impersonation failed"));
                return JsonSerializer.Serialize(new HelperResponse(true, null));
            }

            // Option E: Set UIAccess flag on our process token at runtime.
            // Requires SeTcbPrivilege (SYSTEM has it). Bypasses signing + protected location checks.
            // Then SendSAS(true) — we're now a UIAccess app.
            // Source: .claude/research/sendsas-ctrl-alt-del/research.md
            if (SetUIAccessOnProcessToken())
            {
                DebugLog("Calling SendSAS(true) as UIAccess app...");
                SendSAS(true);
                DebugLog("SendSAS(true) returned — SAS should have fired");
            }
            else
            {
                // Fallback: try SendSAS(false) anyway — won't work unless we're a service, but log the attempt
                DebugLog("SetUIAccess failed — falling back to SendSAS(false) (unlikely to work)");
                SendSAS(false);
                DebugLog("SendSAS(false) returned (fallback)");
            }

            return JsonSerializer.Serialize(new HelperResponse(true, null));
        }
        catch (Exception ex)
        {
            DebugLog($"SendSAS failed: {ex.Message}");
            return JsonSerializer.Serialize(new HelperResponse(false, ex.Message));
        }
    }

    /// <summary>
    /// Set the UIAccess flag on the current process token at runtime.
    /// This makes Windows treat our process as a UIAccess app, qualifying for SendSAS(true).
    /// Requires SeTcbPrivilege — only SYSTEM processes have it.
    /// Bypasses the signing + protected location checks enforced by AppInfo during CreateProcess.
    /// Source: Tyranid's Lair (James Forshaw, Google Project Zero)
    /// </summary>
    private static bool SetUIAccessOnProcessToken()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
        {
            DebugLog($"SetUIAccess: OpenProcessToken failed ({Marshal.GetLastWin32Error()})");
            return false;
        }

        try
        {
            uint uiAccess = 1;
            if (!SetTokenInformation(token, TokenUIAccess, ref uiAccess, 4))
            {
                DebugLog($"SetUIAccess: SetTokenInformation(TokenUIAccess=1) failed ({Marshal.GetLastWin32Error()})");
                return false;
            }
            DebugLog("SetUIAccess: TokenUIAccess flag set — process is now UIAccess");
            return true;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>
    /// Ensure SoftwareSASGeneration registry value is set to 3 (services + applications).
    /// Required for SendSAS(false) from sas.dll to work. SYSTEM has HKLM write access.
    /// Called at startup and before each SendSAS call (GPO can overwrite between calls).
    /// </summary>
    private static void EnsureSoftwareSASEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Policies\System", writable: true);
            if (key != null)
            {
                var current = key.GetValue("SoftwareSASGeneration");
                if (current == null || (int)current < 3)
                {
                    key.SetValue("SoftwareSASGeneration", 3, Microsoft.Win32.RegistryValueKind.DWord);
                    DebugLog("Set SoftwareSASGeneration=3 (enable software SAS)");
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog($"Failed to set SoftwareSASGeneration: {ex.Message}");
        }
    }

    /// <summary>
    /// Enable SeTcbPrivilege on the current process token.
    /// Required for SendSAS(false) from sas.dll — SYSTEM tokens have it but it may be disabled.
    /// </summary>
    private static bool EnableTcbPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
        {
            DebugLog($"EnableTcbPrivilege: OpenProcessToken failed (error {Marshal.GetLastWin32Error()})");
            return false;
        }

        try
        {
            if (!LookupPrivilegeValue(null, SE_TCB_NAME, out var luid))
            {
                DebugLog($"EnableTcbPrivilege: LookupPrivilegeValue failed (error {Marshal.GetLastWin32Error()})");
                return false;
            }

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new LUID_AND_ATTRIBUTES
                {
                    Luid = luid,
                    Attributes = SE_PRIVILEGE_ENABLED
                }
            };

            if (AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)
                && Marshal.GetLastWin32Error() == 0)
            {
                DebugLog("SeTcbPrivilege enabled successfully");
                return true;
            }

            DebugLog($"EnableTcbPrivilege: AdjustTokenPrivileges failed (error {Marshal.GetLastWin32Error()})");
            return false;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>
    /// Enable a named privilege on a specific token handle.
    /// </summary>
    private static bool EnablePrivilegeOnToken(IntPtr token, string privilegeName)
    {
        if (!LookupPrivilegeValue(null, privilegeName, out var luid))
            return false;

        var tp = new TOKEN_PRIVILEGES
        {
            PrivilegeCount = 1,
            Privileges = new LUID_AND_ATTRIBUTES
            {
                Luid = luid,
                Attributes = SE_PRIVILEGE_ENABLED
            }
        };

        AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        var err = Marshal.GetLastWin32Error();
        if (err == 0)
        {
            DebugLog($"EnablePrivilegeOnToken({privilegeName}): enabled on impersonation token");
            return true;
        }
        DebugLog($"EnablePrivilegeOnToken({privilegeName}): failed (error {err})");
        return false;
    }

    /// <summary>
    /// Impersonate winlogon.exe's token (which has SeTcbPrivilege), call SendSAS, revert.
    /// Fallback when the process's own token lacks SeTcbPrivilege.
    /// </summary>
    private static bool CallSendSASWithImpersonation()
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        Process? winlogon = null;
        foreach (var p in Process.GetProcessesByName("winlogon"))
        {
            try
            {
                if (p.SessionId == (int)sessionId) { winlogon = p; break; }
            }
            catch { /* Access denied for some processes */ }
        }
        if (winlogon == null)
        {
            DebugLog($"CallSendSASWithImpersonation: winlogon.exe not found in session {sessionId}");
            return false;
        }

        DebugLog($"CallSendSASWithImpersonation: found winlogon PID {winlogon.Id} in session {sessionId}");

        var hProcess = OpenProcess(PROCESS_QUERY_INFORMATION, false, (uint)winlogon.Id);
        if (hProcess == IntPtr.Zero)
        {
            DebugLog($"CallSendSASWithImpersonation: OpenProcess failed ({Marshal.GetLastWin32Error()})");
            return false;
        }

        try
        {
            if (!OpenProcessToken(hProcess, TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ADJUST_PRIVILEGES, out var hToken))
            {
                DebugLog($"CallSendSASWithImpersonation: OpenProcessToken failed ({Marshal.GetLastWin32Error()})");
                return false;
            }

            try
            {
                // Duplicate as impersonation token (SecurityImpersonation level)
                if (!DuplicateTokenEx(hToken, MAXIMUM_ALLOWED, IntPtr.Zero,
                    SecurityImpersonation, TokenImpersonation, out var hDup))
                {
                    DebugLog($"CallSendSASWithImpersonation: DuplicateTokenEx failed ({Marshal.GetLastWin32Error()})");
                    return false;
                }

                try
                {
                    // Enable SeTcbPrivilege on the impersonation token
                    EnablePrivilegeOnToken(hDup, SE_TCB_NAME);

                    // Impersonate winlogon
                    if (!ImpersonateLoggedOnUser(hDup))
                    {
                        DebugLog($"CallSendSASWithImpersonation: ImpersonateLoggedOnUser failed ({Marshal.GetLastWin32Error()})");
                        return false;
                    }

                    try
                    {
                        DebugLog("Calling SendSAS(false) under winlogon impersonation...");
                        SendSAS(false);
                        DebugLog("SendSAS(false) returned under impersonation");
                        return true;
                    }
                    finally
                    {
                        RevertToSelf();
                    }
                }
                finally { CloseHandle(hDup); }
            }
            finally { CloseHandle(hToken); }
        }
        finally { CloseHandle(hProcess); }
    }
}
