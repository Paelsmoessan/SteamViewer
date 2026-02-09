using System.Runtime.InteropServices;
using System.Security.Principal;

// SteamViewer SAS Helper
// Sends the Secure Attention Sequence (Ctrl+Alt+Del) via sas.dll.
// Must be run elevated (admin) — exits with code 1 if not.

if (!IsElevated())
{
    Console.Error.WriteLine("SasHelper: Not running as administrator. Exiting.");
    return 1;
}

try
{
    SendSAS(false);
    Console.WriteLine("SasHelper: SendSAS succeeded.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"SasHelper: SendSAS failed: {ex.Message}");
    return 2;
}

static bool IsElevated()
{
    using var identity = WindowsIdentity.GetCurrent();
    var principal = new WindowsPrincipal(identity);
    return principal.IsInRole(WindowsBuiltInRole.Administrator);
}

[DllImport("sas.dll", SetLastError = true)]
static extern void SendSAS(bool asUser);
