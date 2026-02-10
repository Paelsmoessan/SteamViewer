using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SteamViewer.Client.Core.Session;

/// <summary>
/// Encrypts and persists session credentials for auto-reconnect after reboot.
/// One-time use: file is deleted immediately after reading.
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
    /// </summary>
    public static void Save(string clientId, string passwordHash, string viewerPeerId)
    {
        var key = DeriveKey(viewerPeerId, clientId);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.GenerateIV();

        var plainText = Encoding.UTF8.GetBytes(passwordHash);
        var encrypted = aes.EncryptCbc(plainText, aes.IV, PaddingMode.PKCS7);

        var data = new ReconnectData
        {
            ClientId = clientId,
            ViewerPeerId = viewerPeerId,
            IV = Convert.ToBase64String(aes.IV),
            EncryptedHash = Convert.ToBase64String(encrypted)
        };

        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(data));
    }

    /// <summary>
    /// Load and decrypt session credentials. Deletes the file after reading.
    /// Returns null if no reconnect data exists or decryption fails.
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

            var key = DeriveKey(data.ViewerPeerId, data.ClientId);
            var iv = Convert.FromBase64String(data.IV);
            var encrypted = Convert.FromBase64String(data.EncryptedHash);

            using var aes = Aes.Create();
            aes.Key = key;

            var decrypted = aes.DecryptCbc(encrypted, iv, PaddingMode.PKCS7);
            var passwordHash = Encoding.UTF8.GetString(decrypted);

            return new ReconnectResult(data.ClientId, passwordHash, data.ViewerPeerId);
        }
        catch
        {
            return null;
        }
        finally
        {
            // Always delete the file — one-time use
            try { File.Delete(FilePath); } catch { }
        }
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
    }

    public sealed record ReconnectResult(string ClientId, string PasswordHash, string ViewerPeerId);
}
