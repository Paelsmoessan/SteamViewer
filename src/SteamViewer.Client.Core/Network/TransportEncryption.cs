using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SteamViewer.Client.Core.Network;

/// <summary>
/// AES-256-GCM encryption for transport data with session tag for fast-reject.
/// Both host and viewer derive the same key from the shared password hash + session nonce.
///
/// Wire format: [4 bytes session-tag][12 bytes nonce][N bytes ciphertext][16 bytes GCM tag]
/// Session tag = first 4 bytes of session nonce (shared in RelayReady handshake).
/// Receiver checks session tag before attempting decryption. Mismatch = stale data, silent drop.
/// Same pattern as QUIC Connection ID, WireGuard Receiver Index, DTLS Epoch.
///
/// Nonce = [1 byte direction][3 bytes zero][8 bytes counter big-endian]
/// Direction: 0x00 = host->viewer, 0x01 = viewer->host
/// </summary>
public sealed class TransportEncryption : IDisposable
{
    private readonly AesGcm _aes;
    private readonly byte _direction;
    private readonly byte[] _sessionTag; // 4 bytes, from session nonce
    private long _sendCounter;
    private bool _disposed;

    public const int SessionTagSize = 4;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int Overhead = SessionTagSize + NonceSize + TagSize; // 32 bytes

    /// <param name="passwordHashHex">Lowercase hex BLAKE3 hash of the password (shared secret).</param>
    /// <param name="sessionNonce">Random per-session nonce (32 bytes) for key uniqueness.</param>
    /// <param name="isHost">True for host->viewer direction, false for viewer->host.</param>
    public TransportEncryption(string passwordHashHex, byte[] sessionNonce, bool isHost)
    {
        var ikm = Convert.FromHexString(passwordHashHex);
        var key = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm,
            outputLength: 32,
            salt: sessionNonce,
            info: Encoding.UTF8.GetBytes("steamviewer-transport-v1"));

        _aes = new AesGcm(key, TagSize);
        _direction = isHost ? (byte)0x00 : (byte)0x01;

        // Session tag = first 4 bytes of session nonce (both sides have it from RelayReady)
        _sessionTag = new byte[SessionTagSize];
        Array.Copy(sessionNonce, _sessionTag, SessionTagSize);
    }

    /// <summary>
    /// Encrypt plaintext. Returns [session-tag][nonce][ciphertext][tag].
    /// </summary>
    public byte[] Encrypt(byte[] plaintext, int offset, int length)
    {
        var counter = Interlocked.Increment(ref _sendCounter);
        var nonce = new byte[NonceSize];
        nonce[0] = _direction;
        BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), counter);

        var output = new byte[SessionTagSize + NonceSize + length + TagSize];

        // Prepend session tag (unencrypted, for fast-reject)
        _sessionTag.CopyTo(output.AsSpan());

        // Nonce after session tag
        nonce.CopyTo(output.AsSpan(SessionTagSize));

        _aes.Encrypt(
            nonce,
            plaintext.AsSpan(offset, length),
            output.AsSpan(SessionTagSize + NonceSize, length),
            output.AsSpan(SessionTagSize + NonceSize + length, TagSize));

        return output;
    }

    /// <summary>
    /// Decrypt [session-tag][nonce][ciphertext][tag]. Returns plaintext, or null if session tag mismatches.
    /// Null return = stale data from old session (silent drop, no exception).
    /// </summary>
    public byte[]? Decrypt(byte[] data, int offset, int length)
    {
        if (length < Overhead)
            throw new CryptographicException("Data too short for AES-GCM");

        // Fast-reject: check session tag before attempting decryption (no crypto cost)
        if (data[offset] != _sessionTag[0] ||
            data[offset + 1] != _sessionTag[1] ||
            data[offset + 2] != _sessionTag[2] ||
            data[offset + 3] != _sessionTag[3])
        {
            return null; // Stale session data - silent drop
        }

        var nonce = data.AsSpan(offset + SessionTagSize, NonceSize);
        var plaintextLength = length - Overhead;
        var ciphertext = data.AsSpan(offset + SessionTagSize + NonceSize, plaintextLength);
        var tag = data.AsSpan(offset + SessionTagSize + NonceSize + plaintextLength, TagSize);

        var plaintext = new byte[plaintextLength];
        _aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _aes.Dispose();
    }
}
