using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SteamViewer.Client.Core.Network;

/// <summary>
/// AES-256-GCM encryption for transport data.
/// Both host and viewer derive the same key from the shared password hash + session nonce.
///
/// Wire format: [12 bytes nonce][N bytes ciphertext][16 bytes GCM tag]
/// Nonce = [1 byte direction][3 bytes zero][8 bytes counter big-endian]
/// Direction: 0x00 = host→viewer, 0x01 = viewer→host
/// </summary>
public sealed class TransportEncryption : IDisposable
{
    private readonly AesGcm _aes;
    private readonly byte _direction;
    private long _sendCounter;
    private bool _disposed;

    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int Overhead = NonceSize + TagSize; // 28 bytes

    /// <param name="passwordHashHex">Lowercase hex BLAKE3 hash of the password (shared secret).</param>
    /// <param name="sessionNonce">Random per-session nonce (32 bytes) for key uniqueness.</param>
    /// <param name="isHost">True for host→viewer direction, false for viewer→host.</param>
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
    }

    /// <summary>
    /// Encrypt plaintext. Returns [nonce][ciphertext][tag].
    /// </summary>
    public byte[] Encrypt(byte[] plaintext, int offset, int length)
    {
        var counter = Interlocked.Increment(ref _sendCounter);
        var nonce = new byte[NonceSize];
        nonce[0] = _direction;
        BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), counter);

        var output = new byte[NonceSize + length + TagSize];
        nonce.CopyTo(output.AsSpan());

        _aes.Encrypt(
            nonce,
            plaintext.AsSpan(offset, length),
            output.AsSpan(NonceSize, length),
            output.AsSpan(NonceSize + length, TagSize));

        return output;
    }

    /// <summary>
    /// Decrypt [nonce][ciphertext][tag]. Returns plaintext.
    /// </summary>
    public byte[] Decrypt(byte[] data, int offset, int length)
    {
        if (length < Overhead)
            throw new CryptographicException("Data too short for AES-GCM");

        var nonce = data.AsSpan(offset, NonceSize);
        var plaintextLength = length - Overhead;
        var ciphertext = data.AsSpan(offset + NonceSize, plaintextLength);
        var tag = data.AsSpan(offset + NonceSize + plaintextLength, TagSize);

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
