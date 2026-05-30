using SteamViewer.Common.Protocol;

namespace SteamViewer.Server.Tests;

public class SignalingMessageTests
{
    [Fact]
    public void Register_SerializesCorrectly()
    {
        // Arrange
        var message = new SignalingMessage.Register("123456789", "password_hash");

        // Act
        var json = SignalingSerializer.Serialize(message);
        var deserialized = SignalingSerializer.Deserialize(json);

        // Assert
        var register = Assert.IsType<SignalingMessage.Register>(deserialized);
        Assert.Equal("123456789", register.ClientId);
        Assert.Equal("password_hash", register.PasswordHash);
    }

    [Fact]
    public void RegisterSuccess_SerializesCorrectly()
    {
        // Arrange
        var message = new SignalingMessage.RegisterSuccess("123456789");

        // Act
        var json = SignalingSerializer.Serialize(message);
        var deserialized = SignalingSerializer.Deserialize(json);

        // Assert
        var success = Assert.IsType<SignalingMessage.RegisterSuccess>(deserialized);
        Assert.Equal("123456789", success.ClientId);
    }

    [Fact]
    public void RegisterFailed_SerializesCorrectly()
    {
        // Arrange
        var message = new SignalingMessage.RegisterFailed("Client ID already exists");

        // Act
        var json = SignalingSerializer.Serialize(message);
        var deserialized = SignalingSerializer.Deserialize(json);

        // Assert
        var failed = Assert.IsType<SignalingMessage.RegisterFailed>(deserialized);
        Assert.Equal("Client ID already exists", failed.Reason);
    }

    [Fact]
    public void ConnectRequest_SerializesCorrectly()
    {
        // Arrange
        var message = new SignalingMessage.ConnectRequest("987654321", "abcdef0123456789");

        // Act
        var json = SignalingSerializer.Serialize(message);
        var deserialized = SignalingSerializer.Deserialize(json);

        // Assert
        var request = Assert.IsType<SignalingMessage.ConnectRequest>(deserialized);
        Assert.Equal("987654321", request.TargetId);
        Assert.Equal("abcdef0123456789", request.PasswordHash);
    }

    [Fact]
    public void IncomingConnection_SerializesCorrectly()
    {
        // Arrange
        var message = new SignalingMessage.IncomingConnection("123456789");

        // Act
        var json = SignalingSerializer.Serialize(message);
        var deserialized = SignalingSerializer.Deserialize(json);

        // Assert
        var incoming = Assert.IsType<SignalingMessage.IncomingConnection>(deserialized);
        Assert.Equal("123456789", incoming.FromId);
    }

    [Fact]
    public void ConnectionResponse_SerializesCorrectly()
    {
        // Arrange
        var message = new SignalingMessage.ConnectionResponse("123456789", true);

        // Act
        var json = SignalingSerializer.Serialize(message);
        var deserialized = SignalingSerializer.Deserialize(json);

        // Assert
        var response = Assert.IsType<SignalingMessage.ConnectionResponse>(deserialized);
        Assert.Equal("123456789", response.TargetId);
        Assert.True(response.Approved);
    }

    [Fact]
    public void Connected_SerializesCorrectly()
    {
        // Arrange
        var message = new SignalingMessage.Connected("987654321");

        // Act
        var json = SignalingSerializer.Serialize(message);
        var deserialized = SignalingSerializer.Deserialize(json);

        // Assert
        var connected = Assert.IsType<SignalingMessage.Connected>(deserialized);
        Assert.Equal("987654321", connected.PeerId);
    }

    [Fact]
    public void Disconnect_SerializesCorrectly()
    {
        // Arrange
        var message = new SignalingMessage.Disconnect("987654321");

        // Act
        var json = SignalingSerializer.Serialize(message);
        var deserialized = SignalingSerializer.Deserialize(json);

        // Assert
        var disconnect = Assert.IsType<SignalingMessage.Disconnect>(deserialized);
        Assert.Equal("987654321", disconnect.PeerId);
    }

    [Fact]
    public void Disconnected_SerializesCorrectly_WithReason()
    {
        // Arrange
        var message = new SignalingMessage.Disconnected("987654321", "Connection lost");

        // Act
        var json = SignalingSerializer.Serialize(message);
        var deserialized = SignalingSerializer.Deserialize(json);

        // Assert
        var disconnected = Assert.IsType<SignalingMessage.Disconnected>(deserialized);
        Assert.Equal("987654321", disconnected.PeerId);
        Assert.Equal("Connection lost", disconnected.Reason);
    }

    [Fact]
    public void Disconnected_SerializesCorrectly_WithoutReason()
    {
        // Arrange
        var message = new SignalingMessage.Disconnected("987654321", null);

        // Act
        var json = SignalingSerializer.Serialize(message);
        var deserialized = SignalingSerializer.Deserialize(json);

        // Assert
        var disconnected = Assert.IsType<SignalingMessage.Disconnected>(deserialized);
        Assert.Equal("987654321", disconnected.PeerId);
        Assert.Null(disconnected.Reason);
    }

    [Fact]
    public void Error_SerializesCorrectly()
    {
        // Arrange
        var message = new SignalingMessage.Error("Something went wrong");

        // Act
        var json = SignalingSerializer.Serialize(message);
        var deserialized = SignalingSerializer.Deserialize(json);

        // Assert
        var error = Assert.IsType<SignalingMessage.Error>(deserialized);
        Assert.Equal("Something went wrong", error.Message);
    }

    [Fact]
    public void Ping_SerializesCorrectly()
    {
        // Arrange
        var message = new SignalingMessage.Ping();

        // Act
        var json = SignalingSerializer.Serialize(message);
        var deserialized = SignalingSerializer.Deserialize(json);

        // Assert
        Assert.IsType<SignalingMessage.Ping>(deserialized);
    }

    [Fact]
    public void Pong_SerializesCorrectly()
    {
        // Arrange
        var message = new SignalingMessage.Pong();

        // Act
        var json = SignalingSerializer.Serialize(message);
        var deserialized = SignalingSerializer.Deserialize(json);

        // Assert
        Assert.IsType<SignalingMessage.Pong>(deserialized);
    }

    [Fact]
    public void Deserialize_WithInvalidJson_ReturnsNull()
    {
        // Act
        var result = SignalingSerializer.Deserialize("not valid json");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void Deserialize_WithUnknownType_ReturnsNull()
    {
        // Act
        var result = SignalingSerializer.Deserialize("{\"type\":\"unknown_type\",\"data\":\"test\"}");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void Register_Json_ContainsSnakeCasePropertyNames()
    {
        // Arrange
        var message = new SignalingMessage.Register("123456789", "password_hash");

        // Act
        var json = SignalingSerializer.Serialize(message);

        // Assert
        Assert.Contains("\"type\":\"register\"", json);
        Assert.Contains("\"client_id\":\"123456789\"", json);
        Assert.Contains("\"password_hash\":\"password_hash\"", json);
    }

    [Fact]
    public void SanitizeForLog_Register_MasksPasswordHash()
    {
        var message = new SignalingMessage.Register("client1", "deadbeefcafebabe");

        var sanitized = (SignalingMessage.Register)SignalingSerializer.SanitizeForLog(message);

        Assert.Equal("***", sanitized.PasswordHash);
        Assert.Equal("client1", sanitized.ClientId);
    }

    [Fact]
    public void SanitizeForLog_ConnectRequest_MasksPasswordHash()
    {
        var message = new SignalingMessage.ConnectRequest("target1", "deadbeefcafebabe");

        var sanitized = (SignalingMessage.ConnectRequest)SignalingSerializer.SanitizeForLog(message);

        Assert.Equal("***", sanitized.PasswordHash);
        Assert.Equal("target1", sanitized.TargetId);
    }

    [Fact]
    public void SanitizeForLog_RelayReady_MasksEncryptionNonce()
    {
        var message = new SignalingMessage.RelayReady("target1", "abcdef0123456789");

        var sanitized = (SignalingMessage.RelayReady)SignalingSerializer.SanitizeForLog(message);

        Assert.Equal("***", sanitized.EncryptionNonce);
        Assert.Equal("target1", sanitized.TargetId);
    }

    [Fact]
    public void SanitizeForLog_NonSecretType_ReturnsSameInstance()
    {
        SignalingMessage message = new SignalingMessage.Ping();

        var sanitized = SignalingSerializer.SanitizeForLog(message);

        Assert.Same(message, sanitized);
    }

    [Fact]
    public void SanitizeForLog_SerializedOutput_DoesNotContainOriginalSecret()
    {
        var message = new SignalingMessage.Register("client1", "actual_hash_value_xyz");

        var json = SignalingSerializer.Serialize(SignalingSerializer.SanitizeForLog(message));

        Assert.DoesNotContain("actual_hash_value_xyz", json);
        Assert.Contains("\"password_hash\":\"***\"", json);
    }
}
