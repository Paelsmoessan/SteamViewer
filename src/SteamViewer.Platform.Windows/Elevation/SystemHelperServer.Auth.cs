using SteamViewer.Common.Protocol;
using System.Text.Json;

namespace SteamViewer.Platform.Windows.Elevation;

// Pipe-handshake authentication for SystemHelperServer: nonce validation +
// constant-time comparison.
public static partial class SystemHelperServer
{
    private static bool Authenticate(StreamReader reader, StreamWriter writer, string expectedNonce)
    {
        try
        {
            var line = reader.ReadLine();
            if (line == null)
            {
                DebugLog("Authentication: client disconnected before sending nonce.");
                return false;
            }

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            var command = root.TryGetProperty("command", out var cmdProp) ? cmdProp.GetString() : null;
            var nonce = root.TryGetProperty("nonce", out var nonceProp) ? nonceProp.GetString() : null;

            if (command != "authenticate" || nonce == null)
            {
                DebugLog($"Authentication: expected authenticate command, got: {command}");
                writer.WriteLine(JsonSerializer.Serialize(new HelperResponse(false, "Expected authenticate command")));
                return false;
            }

            // Constant-time comparison to prevent timing attacks
            if (!CryptographicEquals(nonce, expectedNonce))
            {
                DebugLog("Authentication: nonce mismatch");
                writer.WriteLine(JsonSerializer.Serialize(new HelperResponse(false, "Invalid nonce")));
                return false;
            }

            writer.WriteLine(JsonSerializer.Serialize(new HelperResponse(true, null)));
            return true;
        }
        catch (Exception ex)
        {
            DebugLog($"Authentication error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Constant-time string comparison to prevent timing attacks on nonce validation.
    /// </summary>
    private static bool CryptographicEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (int i = 0; i < a.Length; i++)
            diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
