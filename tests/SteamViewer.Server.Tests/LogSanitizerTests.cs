using SteamViewer.Common.Logging;

namespace SteamViewer.Server.Tests;

public class LogSanitizerTests
{
    [Fact]
    public void MaskJsonSecrets_NonceField_Masked()
    {
        var json = "{\"command\":\"launchSystemHelper\",\"pipeName\":\"foo\",\"nonce\":\"abc123base64==\"}";

        var sanitized = LogSanitizer.MaskJsonSecrets(json);

        Assert.DoesNotContain("abc123base64==", sanitized);
        Assert.Contains("\"nonce\":\"***\"", sanitized);
        Assert.Contains("\"pipeName\":\"foo\"", sanitized);
    }

    [Fact]
    public void MaskJsonSecrets_PasswordHashField_Masked()
    {
        var json = "{\"command\":\"reboot\",\"clientId\":\"viewer1\",\"passwordHash\":\"deadbeef\"}";

        var sanitized = LogSanitizer.MaskJsonSecrets(json);

        Assert.DoesNotContain("deadbeef", sanitized);
        Assert.Contains("\"passwordHash\":\"***\"", sanitized);
        Assert.Contains("\"clientId\":\"viewer1\"", sanitized);
    }

    [Fact]
    public void MaskJsonSecrets_TurnCredentials_Masked()
    {
        var json = "{\"turnUsername\":\"alice\",\"turnCredential\":\"hunter2\",\"clientId\":\"v\"}";

        var sanitized = LogSanitizer.MaskJsonSecrets(json);

        Assert.DoesNotContain("alice", sanitized);
        Assert.DoesNotContain("hunter2", sanitized);
        Assert.Contains("\"turnUsername\":\"***\"", sanitized);
        Assert.Contains("\"turnCredential\":\"***\"", sanitized);
        Assert.Contains("\"clientId\":\"v\"", sanitized);
    }

    [Fact]
    public void MaskJsonSecrets_NonSecretJson_Unchanged()
    {
        var json = "{\"command\":\"ping\",\"clientId\":\"v\"}";

        var sanitized = LogSanitizer.MaskJsonSecrets(json);

        Assert.Equal(json, sanitized);
    }

    [Fact]
    public void MaskJsonSecrets_NullInput_ReturnsEmpty()
    {
        var sanitized = LogSanitizer.MaskJsonSecrets(null);

        Assert.Equal("", sanitized);
    }

    [Fact]
    public void MaskJsonSecrets_EmptyInput_ReturnsEmpty()
    {
        var sanitized = LogSanitizer.MaskJsonSecrets("");

        Assert.Equal("", sanitized);
    }

    [Fact]
    public void MaskJsonSecrets_NonJsonString_Unchanged()
    {
        var input = "[ElevatedHelper] Starting pipe server: SteamViewer-Elevated-abc";

        var sanitized = LogSanitizer.MaskJsonSecrets(input);

        Assert.Equal(input, sanitized);
    }

    [Fact]
    public void MaskJsonSecrets_TolerantOfWhitespaceAroundColon()
    {
        var json = "{\"nonce\" : \"secret_value\"}";

        var sanitized = LogSanitizer.MaskJsonSecrets(json);

        Assert.DoesNotContain("secret_value", sanitized);
        Assert.Contains("***", sanitized);
    }

    [Fact]
    public void MaskJsonSecrets_AllFourSecrets_AllMasked()
    {
        var json = "{\"nonce\":\"n1\",\"passwordHash\":\"p1\",\"turnUsername\":\"u1\",\"turnCredential\":\"c1\"}";

        var sanitized = LogSanitizer.MaskJsonSecrets(json);

        Assert.DoesNotContain("n1", sanitized);
        Assert.DoesNotContain("p1", sanitized);
        Assert.DoesNotContain("u1", sanitized);
        Assert.DoesNotContain("c1", sanitized);
    }
}
