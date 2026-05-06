using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace SteamViewer.Client.Core.Session;

/// <summary>
/// Encrypts and persists session credentials for auto-reconnect after reboot.
///
/// Security model (v2):
/// - Encryption via Windows DPAPI with LocalMachine scope (so the SYSTEM-context boot
///   relay process can decrypt before any user is logged in).
/// - File ACL restricts read/write to the saving user + SYSTEM. Other regular users on
///   the same machine cannot open the file.
/// - Admin malware on the same machine can defeat both layers - acceptable, since
///   admin malware can already do anything to the machine. The threats this layer
///   defends against are: (a) regular user privilege escalation via reading creds,
///   (b) file exfiltration off-machine (DPAPI binds to the machine's master key).
/// - Old v1 files (which had a self-derived AES-CBC key, derivable from the file's
///   own contents) are deleted on first run of v2 code via path migration.
///
/// File path: C:\ProgramData\SteamViewer\reconnect.v2.json
/// </summary>
public static class ReconnectCredentials
{
    private static readonly string DirPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SteamViewer");

    private static readonly string FilePath = Path.Combine(DirPath, "reconnect.v2.json");

    // Old v1 file (broken self-decrypting AES-CBC) — delete on save/load to migrate.
    private static readonly string LegacyFilePath = Path.Combine(DirPath, "reconnect.json");

    private const string EntropyDomainTag = "SteamViewer-reconnect-v2:";

    /// <summary>Check if reconnect data exists (without loading/deleting it).</summary>
    public static bool Exists()
    {
        TryDeleteLegacy();
        return File.Exists(FilePath);
    }

    /// <summary>
    /// Encrypt and save session credentials for post-reboot reconnection.
    /// Includes signaling server URL and ICE server config for boot relay WebRTC.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void Save(string clientId, string passwordHash, string viewerPeerId,
        string? serverUrl = null, string[]? stunUrls = null,
        string[]? turnUrls = null, string? turnUsername = null, string? turnCredential = null)
    {
        var payload = new ReconnectPayload
        {
            PasswordHash = passwordHash,
            TurnUsername = turnUsername ?? "",
            TurnCredential = turnCredential ?? ""
        };
        var payloadJson = JsonSerializer.Serialize(payload);

        var entropy = BuildEntropy(viewerPeerId, clientId);
        var encrypted = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(payloadJson),
            entropy,
            DataProtectionScope.LocalMachine);

        var data = new ReconnectData
        {
            ClientId = clientId,
            ViewerPeerId = viewerPeerId,
            EncryptedBlob = Convert.ToBase64String(encrypted),
            SavedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ServerUrl = serverUrl ?? "",
            StunUrls = stunUrls ?? [],
            TurnUrls = turnUrls ?? []
        };

        Directory.CreateDirectory(DirPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(data));
        ApplyRestrictiveAcl(FilePath);
        TryDeleteLegacy();
    }

    /// <summary>
    /// Load and decrypt session credentials. Does NOT delete the file -
    /// both SYSTEM helper and main app may need to read it.
    /// Call Delete() explicitly after the main app takes over.
    /// Returns null if no reconnect data exists, is stale, or decryption fails.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static ReconnectResult? Load()
    {
        TryDeleteLegacy();

        if (!File.Exists(FilePath))
            return null;

        try
        {
            var json = File.ReadAllText(FilePath);
            var data = JsonSerializer.Deserialize<ReconnectData>(json);

            if (data == null || string.IsNullOrEmpty(data.ClientId) ||
                string.IsNullOrEmpty(data.ViewerPeerId) ||
                string.IsNullOrEmpty(data.EncryptedBlob))
                return null;

            // Reject stale reconnect data (older than 60 minutes — Windows Update reboots can take 30+ min)
            if (data.SavedAtUnixMs > 0)
            {
                var savedAt = DateTimeOffset.FromUnixTimeMilliseconds(data.SavedAtUnixMs);
                if (DateTimeOffset.UtcNow - savedAt > TimeSpan.FromMinutes(60))
                    return null;
            }

            var entropy = BuildEntropy(data.ViewerPeerId, data.ClientId);
            var encrypted = Convert.FromBase64String(data.EncryptedBlob);
            byte[] decrypted;
            try
            {
                decrypted = ProtectedData.Unprotect(encrypted, entropy, DataProtectionScope.LocalMachine);
            }
            catch (CryptographicException)
            {
                return null;
            }

            var decryptedStr = Encoding.UTF8.GetString(decrypted);
            ReconnectPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<ReconnectPayload>(decryptedStr);
            }
            catch
            {
                return null;
            }
            if (payload == null || string.IsNullOrEmpty(payload.PasswordHash))
                return null;

            return new ReconnectResult(data.ClientId, payload.PasswordHash, data.ViewerPeerId,
                data.ServerUrl, data.StunUrls, data.TurnUrls, payload.TurnUsername, payload.TurnCredential);
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
        TryDeleteLegacy();
    }

    /// <summary>
    /// Domain-separated entropy. Tag prefix prevents cross-protocol replay; clientId/viewerPeerId
    /// bind the blob to a specific session (so a leaked blob from a different session can't be replayed
    /// without altering the file's plaintext fields, which would break decryption).
    /// </summary>
    private static byte[] BuildEntropy(string viewerPeerId, string clientId)
    {
        return Encoding.UTF8.GetBytes(EntropyDomainTag + viewerPeerId + ":" + clientId);
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyRestrictiveAcl(string filePath)
    {
        try
        {
            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser == null) return;

            var fileInfo = new FileInfo(filePath);
            var security = fileInfo.GetAccessControl();

            // Disable inheritance and remove inherited rules
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            // Clear any existing rules so we have a clean slate
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                security.RemoveAccessRule(rule);

            // Owner: full control
            security.AddAccessRule(new FileSystemAccessRule(
                currentUser, FileSystemRights.FullControl,
                InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));

            // SYSTEM: full control (needed for boot relay)
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl,
                InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));

            fileInfo.SetAccessControl(security);
        }
        catch
        {
            // Best effort - if ACL setting fails, the file still exists with default ACL.
            // DPAPI LocalMachine entropy continues to provide some protection.
        }
    }

    private static void TryDeleteLegacy()
    {
        try { if (File.Exists(LegacyFilePath)) File.Delete(LegacyFilePath); } catch { }
    }

    private sealed class ReconnectData
    {
        public string ClientId { get; set; } = "";
        public string ViewerPeerId { get; set; } = "";
        public string EncryptedBlob { get; set; } = "";
        public long SavedAtUnixMs { get; set; }
        public string ServerUrl { get; set; } = "";
        public string[] StunUrls { get; set; } = [];
        public string[] TurnUrls { get; set; } = [];
    }

    /// <summary>Encrypted payload inside EncryptedBlob — contains sensitive credentials.</summary>
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
