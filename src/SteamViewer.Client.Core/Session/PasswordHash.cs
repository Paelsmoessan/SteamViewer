using System.Text;
using Blake3;

namespace SteamViewer.Client.Core.Session;

/// <summary>
/// Computes the salted password hash used for signaling auth.
/// Both the host (during register) and viewer (during connect request) compute the
/// same hash; the server only stores and compares hashes, never sees plaintext passwords.
///
/// Salt is the host's clientId, which makes the hash unique per host (so a server-side
/// dump of stored hashes cannot be reused across hosts) and prevents rainbow-table
/// attacks against weak passwords.
///
/// Domain-separation tag prevents cross-protocol replay if the same password is used
/// for some other purpose elsewhere.
/// </summary>
public static class PasswordHash
{
    private const string DomainTag = "SteamViewer-v1\0";

    /// <summary>
    /// Computes hex-encoded BLAKE3 hash for the given clientId+password pair.
    /// </summary>
    public static string Compute(string clientId, string password)
    {
        var input = Encoding.UTF8.GetBytes(DomainTag + clientId + "\0" + password);
        var hash = Hasher.Hash(input);
        return Convert.ToHexString(hash.AsSpan()).ToLowerInvariant();
    }
}
