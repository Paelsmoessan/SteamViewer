using System.Threading.Channels;
using SteamViewer.Common.Protocol;
using SteamViewer.Server.Services;

namespace SteamViewer.Server.Tests;

public class ClientRegistryTests
{
    private static ChannelWriter<SignalingMessage> CreateMessageWriter()
    {
        var channel = Channel.CreateUnbounded<SignalingMessage>();
        return channel.Writer;
    }

    [Fact]
    public void TryRegister_WithNewClientId_ReturnsTrue()
    {
        // Arrange
        var registry = new ClientRegistry();
        var connectionId = Guid.NewGuid();

        // Act
        var result = registry.TryRegister("123456789", "password_hash", CreateMessageWriter(), connectionId);

        // Assert
        Assert.True(result);
        Assert.Equal(1, registry.ClientCount);
        Assert.True(registry.IsOnline("123456789"));
    }

    [Fact]
    public void TryRegister_WithDuplicateClientId_ReturnsFalse()
    {
        // Arrange
        var registry = new ClientRegistry();
        var connectionId1 = Guid.NewGuid();
        var connectionId2 = Guid.NewGuid();

        registry.TryRegister("123456789", "hash1", CreateMessageWriter(), connectionId1);

        // Act
        var result = registry.TryRegister("123456789", "hash2", CreateMessageWriter(), connectionId2);

        // Assert
        Assert.False(result);
        Assert.Equal(1, registry.ClientCount);
    }

    [Fact]
    public void GetClient_WithExistingId_ReturnsClientInfo()
    {
        // Arrange
        var registry = new ClientRegistry();
        var connectionId = Guid.NewGuid();
        registry.TryRegister("123456789", "password_hash", CreateMessageWriter(), connectionId);

        // Act
        var client = registry.GetClient("123456789");

        // Assert
        Assert.NotNull(client);
        Assert.Equal("123456789", client.ClientId);
        Assert.Equal("password_hash", client.PasswordHash);
        Assert.Equal(connectionId, client.ConnectionId);
    }

    [Fact]
    public void GetClient_WithNonExistingId_ReturnsNull()
    {
        // Arrange
        var registry = new ClientRegistry();

        // Act
        var client = registry.GetClient("nonexistent");

        // Assert
        Assert.Null(client);
    }

    [Fact]
    public void GetClientIdByConnection_WithExistingConnection_ReturnsClientId()
    {
        // Arrange
        var registry = new ClientRegistry();
        var connectionId = Guid.NewGuid();
        registry.TryRegister("123456789", "hash", CreateMessageWriter(), connectionId);

        // Act
        var clientId = registry.GetClientIdByConnection(connectionId);

        // Assert
        Assert.Equal("123456789", clientId);
    }

    [Fact]
    public void GetClientIdByConnection_WithNonExistingConnection_ReturnsNull()
    {
        // Arrange
        var registry = new ClientRegistry();

        // Act
        var clientId = registry.GetClientIdByConnection(Guid.NewGuid());

        // Assert
        Assert.Null(clientId);
    }

    [Fact]
    public void VerifyPassword_WithCorrectHash_ReturnsTrue()
    {
        // Arrange
        var registry = new ClientRegistry();
        registry.TryRegister("123456789", "correct_hash", CreateMessageWriter(), Guid.NewGuid());

        // Act
        var result = registry.VerifyPassword("123456789", "correct_hash");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void VerifyPassword_WithIncorrectHash_ReturnsFalse()
    {
        // Arrange
        var registry = new ClientRegistry();
        registry.TryRegister("123456789", "correct_hash", CreateMessageWriter(), Guid.NewGuid());

        // Act
        var result = registry.VerifyPassword("123456789", "wrong_hash");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void VerifyPassword_WithNonExistingClient_ReturnsFalse()
    {
        // Arrange
        var registry = new ClientRegistry();

        // Act
        var result = registry.VerifyPassword("nonexistent", "any_hash");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void UnregisterByConnection_WithExistingConnection_RemovesClientAndReturnsId()
    {
        // Arrange
        var registry = new ClientRegistry();
        var connectionId = Guid.NewGuid();
        registry.TryRegister("123456789", "hash", CreateMessageWriter(), connectionId);

        // Act
        var removedId = registry.UnregisterByConnection(connectionId);

        // Assert
        Assert.Equal("123456789", removedId);
        Assert.Equal(0, registry.ClientCount);
        Assert.False(registry.IsOnline("123456789"));
    }

    [Fact]
    public void UnregisterByConnection_WithNonExistingConnection_ReturnsNull()
    {
        // Arrange
        var registry = new ClientRegistry();

        // Act
        var removedId = registry.UnregisterByConnection(Guid.NewGuid());

        // Assert
        Assert.Null(removedId);
    }

    [Fact]
    public void SetPeer_UpdatesPeerIdCorrectly()
    {
        // Arrange
        var registry = new ClientRegistry();
        registry.TryRegister("123456789", "hash", CreateMessageWriter(), Guid.NewGuid());

        // Act
        registry.SetPeer("123456789", "987654321");
        var client = registry.GetClient("123456789");

        // Assert
        Assert.NotNull(client);
        Assert.Equal("987654321", client.PeerId);
    }

    [Fact]
    public void SetPeer_ToNull_ClearsPeerId()
    {
        // Arrange
        var registry = new ClientRegistry();
        registry.TryRegister("123456789", "hash", CreateMessageWriter(), Guid.NewGuid());
        registry.SetPeer("123456789", "987654321");

        // Act
        registry.SetPeer("123456789", null);
        var client = registry.GetClient("123456789");

        // Assert
        Assert.NotNull(client);
        Assert.Null(client.PeerId);
    }

    [Fact]
    public void TrySendToClient_WithExistingClient_ReturnsTrue()
    {
        // Arrange
        var registry = new ClientRegistry();
        var channel = Channel.CreateUnbounded<SignalingMessage>();
        registry.TryRegister("123456789", "hash", channel.Writer, Guid.NewGuid());

        // Act
        var result = registry.TrySendToClient("123456789", new SignalingMessage.Ping());

        // Assert
        Assert.True(result);
        Assert.True(channel.Reader.TryRead(out var message));
        Assert.IsType<SignalingMessage.Ping>(message);
    }

    [Fact]
    public void TrySendToClient_WithNonExistingClient_ReturnsFalse()
    {
        // Arrange
        var registry = new ClientRegistry();

        // Act
        var result = registry.TrySendToClient("nonexistent", new SignalingMessage.Ping());

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void IsOnline_WithRegisteredClient_ReturnsTrue()
    {
        // Arrange
        var registry = new ClientRegistry();
        registry.TryRegister("123456789", "hash", CreateMessageWriter(), Guid.NewGuid());

        // Act & Assert
        Assert.True(registry.IsOnline("123456789"));
    }

    [Fact]
    public void IsOnline_WithUnregisteredClient_ReturnsFalse()
    {
        // Arrange
        var registry = new ClientRegistry();

        // Act & Assert
        Assert.False(registry.IsOnline("123456789"));
    }

    [Fact]
    public void ClientCount_ReturnsCorrectCount()
    {
        // Arrange
        var registry = new ClientRegistry();

        // Act & Assert
        Assert.Equal(0, registry.ClientCount);

        registry.TryRegister("111111111", "hash", CreateMessageWriter(), Guid.NewGuid());
        Assert.Equal(1, registry.ClientCount);

        registry.TryRegister("222222222", "hash", CreateMessageWriter(), Guid.NewGuid());
        Assert.Equal(2, registry.ClientCount);

        registry.TryRegister("333333333", "hash", CreateMessageWriter(), Guid.NewGuid());
        Assert.Equal(3, registry.ClientCount);
    }
}
