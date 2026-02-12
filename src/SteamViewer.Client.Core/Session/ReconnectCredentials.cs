using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SteamViewer.Client.Core.Session;

/// <summary>
/// Encrypts and persists session credentials for auto-reconnect after reboot.
/// Read by both SYSTEM helper (at boot, before login) and main app (after login).
/// Call Delete() explicitly after main app takes over the connection.
/// Encryption key is derived from viewerPeerId + clientId (session-bound).
/// </summary>
public static class ReconnectCredentials
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SteamViewer", "reconnect.json");

    /// <summary>Check if reconnect data exists (without loading/deleting it).</summary>
    public static bool Exists() => File.Exists(FilePath);

    /// <summary>
    /// Encrypt and save session credentials for post-reboot reconnection.
    /// Includes signaling server URL and ICE server config for boot relay WebRTC.
    /// </summary>
    public static void Save(string clientId, string passwordHash, string viewerPeerId,
        string? serverUrl = null, string[]? stunUrls = null,
        string[]? turnUrls = null, string? turnUsername = null, string? turnCredential = null)
    {
        var key = DeriveKey(viewerPeerId, clientId);

        // Encrypt the sensitive payload (passwordHash + ICE credentials)
        var payload = new ReconnectPayload
        {
            PasswordHash = passwordHash,
            TurnUsername = turnUsername ?? "",
            TurnCredential = turnCredential ?? ""
        };
        var payloadJson = JsonSerializer.Serialize(payload);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();

        var plainText = Encoding.UTF8.GetBytes(payloadJson);
        var encrypted = aes.EncryptCbc(plainText, aes.IV, PaddingMode.PKCS7);

        var data = new ReconnectData
        {
            ClientId = clientId,
            ViewerPeerId = viewerPeerId,
            IV = Convert.ToBase64String(aes.IV),
            EncryptedHash = Convert.ToBase64String(encrypted),
            SavedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ServerUrl = serverUrl ?? "",
            StunUrls = stunUrls ?? [],
            TurnUrls = turnUrls ?? []
        };

        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(data));
    }

    /// <summary>
    /// Load and decrypt session credentials. Does NOT delete the file —
    /// both SYSTEM helper and main app may need to read it.
    /// Call Delete() explicitly after the main app takes over.
    /// Returns null if no reconnect data exists, is stale, or decryption fails.
    /// </summary>
    public static ReconnectResult? Load()
    {
        if (!File.Exists(FilePath))
            return null;

        try
        {
            var json = File.ReadAllText(FilePath);
            var data = JsonSerializer.Deserialize<ReconnectData>(json);

            if (data == null || string.IsNullOrEmpty(data.ClientId) ||
                string.IsNullOrEmpty(data.ViewerPeerId) ||
                string.IsNullOrEmpty(data.IV) ||
                string.IsNullOrEmpty(data.EncryptedHash))
                return null;

            // Reject stale reconnect data (older than 60 minutes — Windows Update reboots can take 30+ min)
            if (data.SavedAtUnixMs > 0)
            {
                var savedAt = DateTimeOffset.FromUnixTimeMilliseconds(data.SavedAtUnixMs);
                if (DateTimeOffset.UtcNow - savedAt > TimeSpan.FromMinutes(60))
                    return null;
            }

            var key = DeriveKey(data.ViewerPeerId, data.ClientId);
            var iv = Convert.FromBase64String(data.IV);
            var encrypted = Convert.FromBase64String(data.EncryptedHash);

            using var aes = Aes.Create();
            aes.Key = key;

            var decrypted = aes.DecryptCbc(encrypted, iv, PaddingMode.PKCS7);
            var decryptedStr = Encoding.UTF8.GetString(decrypted);

            // Try new payload format first (JSON with PasswordHash + TURN creds)
            string passwordHash;
            string turnUsername = "";
            string turnCredential = "";
            try
            {
                var payload = JsonSerializer.Deserialize<ReconnectPayload>(decryptedStr);
                if (payload != null && !string.IsNullOrEmpty(payload.PasswordHash))
                {
                    passwordHash = payload.PasswordHash;
                    turnUsername = payload.TurnUsername;
                    turnCredential = payload.TurnCredential;
                }
                else
                {
                    // Fallback: old format where decrypted string is just the passwordHash
                    passwordHash = decryptedStr;
                }
            }
            catch
            {
                // Fallback: old format where decrypted string is just the passwordHash
                passwordHash = decryptedStr;
            }

            return new ReconnectResult(data.ClientId, passwordHash, data.ViewerPeerId,
                data.ServerUrl, data.StunUrls, data.TurnUrls, turnUsername, turnCredential);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Delete the reconnect credentials file. Call after main app takes over the connection.
    /// </summary>
    public static void Delete()
    {
        try { File.Delete(FilePath); } catch { }
    }

    private static byte[] DeriveKey(string viewerPeerId, string clientId)
    {
        var material = Encoding.UTF8.GetBytes(viewerPeerId + clientId);
        return SHA256.HashData(material);
    }

    private sealed class ReconnectData
    {
        public string ClientId { get; set; } = "";
        public string ViewerPeerId { get; set; } = "";
        public string IV { get; set; } = "";
        public string EncryptedHash { get; set; } = "";
        public long SavedAtUnixMs { get; set; }
        public string ServerUrl { get; set; } = "";
        public string[] StunUrls { get; set; } = [];
        public string[] TurnUrls { get; set; } = [];
    }

    /// <summary>Encrypted payload inside EncryptedHash — contains sensitive credentials.</summary>
    private sealed class ReconnectPayload
    {
        public string PasswordHash { get; set; } = "";
        public string TurnUsername { get; set; } = "";
        public string TurnCredential { get; set; } = "";
    }

    public sealed record ReconnectResult(
        string ClientId, string PasswordHash, string ViewerPeerId,
        string? ServerUrl = null, string[]? StunUrls = null, string[]? TurnUrls = null,
        string? TurnUsername = null, string? TurnCredential = null);
}
