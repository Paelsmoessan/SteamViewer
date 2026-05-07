using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace SteamViewer.Platform.Windows.Elevation;

/// <summary>
/// Helpers for authenticating named pipe clients beyond ACL alone.
/// ACL restricts to a user SID; this layer further restricts to a specific PID,
/// preventing same-user processes (e.g. malware running as the host user) from
/// hijacking the pipe.
/// </summary>
internal static class PipeAuth
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr hPipe, out uint clientProcessId);

    /// <summary>
    /// Get the PID of the process at the other end of an accepted pipe connection.
    /// Uses DangerousAddRef/DangerousRelease so the SafeHandle cannot be released
    /// concurrently while we're calling the underlying Win32 API.
    /// </summary>
    public static bool TryGetClientProcessId(NamedPipeServerStream pipe, out uint pid)
    {
        pid = 0;
        var handle = pipe.SafePipeHandle;
        if (handle == null || handle.IsInvalid) return false;

        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            if (!added) return false;
            return GetNamedPipeClientProcessId(handle.DangerousGetHandle(), out pid);
        }
        finally
        {
            if (added) handle.DangerousRelease();
        }
    }

    /// <summary>
    /// Constant-time string compare to prevent timing attacks on auth tokens.
    /// </summary>
    public static bool CryptographicEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (int i = 0; i < a.Length; i++)
            diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
