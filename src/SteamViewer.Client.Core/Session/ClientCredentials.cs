using Blake3;
using System.Security.Cryptography;
using System.Text;

namespace SteamViewer.Client.Core.Session;

/// <summary>
/// Client credentials for authentication.
/// </summary>
public sealed class ClientCredentials
{
    /// <summary>
    /// Unique client ID (9-digit number).
    /// </summary>
    public string ClientId { get; }

    /// <summary>
    /// Password for connection authentication (6 characters).
    /// </summary>
    public string Password { get; }

    /// <summary>
    /// Creates credentials with the specified values.
    /// </summary>
    public ClientCredentials(string clientId, string password)
    {
        ClientId = clientId;
        Password = password;
    }

    /// <summary>
    /// Generates new random credentials.
    /// </summary>
    public static ClientCredentials Generate()
    {
        // Generate 9-digit client ID
        var clientId = GenerateClientId();

        // Generate 6-character password
        var password = GeneratePassword();

        return new ClientCredentials(clientId, password);
    }

    /// <summary>
    /// Gets the salted BLAKE3 hash of the password for authentication.
    /// Salt is the clientId — matches what the server stores and what the viewer sends.
    /// </summary>
    public string PasswordHash()
    {
        return Session.PasswordHash.Compute(ClientId, Password);
    }

    private static string GenerateClientId()
    {
        // Generate a 9-digit number (100000000 to 999999999)
        var bytes = new byte[4];
        RandomNumberGenerator.Fill(bytes);
        var value = BitConverter.ToUInt32(bytes, 0);
        var id = 100_000_000 + (value % 900_000_000);
        return id.ToString();
    }

    private static string GeneratePassword()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var password = new char[6];
        var bytes = new byte[6];
        RandomNumberGenerator.Fill(bytes);

        for (var i = 0; i < 6; i++)
        {
            password[i] = chars[bytes[i] % chars.Length];
        }

        return new string(password);
    }
}
