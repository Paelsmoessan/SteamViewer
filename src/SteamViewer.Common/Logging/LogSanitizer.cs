using System.Text.RegularExpressions;

namespace SteamViewer.Common.Logging;

/// <summary>
/// Helpers for redacting secret values out of strings before they hit a logger.
/// Used at log sites that emit raw JSON pipe payloads (helper-pipe sends/receives) where
/// the typed-record approach used by SignalingSerializer.SanitizeForLog cannot apply because
/// the message is just a string at the log site.
/// </summary>
public static class LogSanitizer
{
    // Pre-compiled for the helper-pipe hot paths. Matches "key":"value" for the listed
    // secret keys. Tolerates whitespace around the colon. Value must not contain a
    // literal double-quote (helper-pipe payloads never include base64/hex values that do).
    private static readonly Regex HelperPipeSecretsRegex = new(
        @"""(nonce|passwordHash|turnUsername|turnCredential)""\s*:\s*""[^""]*""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Masks the values of known secret keys in helper-pipe JSON strings.
    /// Keys masked: nonce, passwordHash, turnUsername, turnCredential.
    /// Non-secret keys and non-JSON input pass through unchanged.
    /// Returns the input unchanged if it is null or contains no matches (no allocation on the common path).
    /// </summary>
    public static string MaskJsonSecrets(string? json)
    {
        if (string.IsNullOrEmpty(json)) return json ?? "";
        return HelperPipeSecretsRegex.Replace(json, @"""$1"":""***""");
    }
}
