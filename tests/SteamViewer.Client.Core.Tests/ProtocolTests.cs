using SteamViewer.Common.Protocol;

namespace SteamViewer.Client.Core.Tests;

public class ProtocolTests
{
    [Theory]
    [InlineData(ConnectionState.Idle)]
    [InlineData(ConnectionState.Registering)]
    [InlineData(ConnectionState.Registered)]
    [InlineData(ConnectionState.Connecting)]
    [InlineData(ConnectionState.Connected)]
    [InlineData(ConnectionState.Disconnected)]
    [InlineData(ConnectionState.Error)]
    public void ConnectionState_AllStatesAreDefined(ConnectionState state)
    {
        // Assert that each state is valid
        Assert.True(Enum.IsDefined(state));
    }

    [Fact]
    public void Role_Swap_HostBecomesViewer()
    {
        // Arrange
        var role = Role.Host;

        // Act
        var swapped = role.Swap();

        // Assert
        Assert.Equal(Role.Viewer, swapped);
    }

    [Fact]
    public void Role_Swap_ViewerBecomesHost()
    {
        // Arrange
        var role = Role.Viewer;

        // Act
        var swapped = role.Swap();

        // Assert
        Assert.Equal(Role.Host, swapped);
    }

    [Fact]
    public void Role_Swap_TwiceReturnsOriginal()
    {
        // Arrange
        var original = Role.Host;

        // Act
        var swappedTwice = original.Swap().Swap();

        // Assert
        Assert.Equal(original, swappedTwice);
    }

    [Fact]
    public void MonitorInfo_CreatesCorrectly()
    {
        // Arrange & Act
        var monitor = new MonitorInfo(
            Id: 1,
            Name: "Primary Display",
            Width: 1920,
            Height: 1080,
            X: 0,
            Y: 0,
            IsPrimary: true);

        // Assert
        Assert.Equal((uint)1, monitor.Id);
        Assert.Equal("Primary Display", monitor.Name);
        Assert.Equal((uint)1920, monitor.Width);
        Assert.Equal((uint)1080, monitor.Height);
        Assert.Equal(0, monitor.X);
        Assert.Equal(0, monitor.Y);
        Assert.True(monitor.IsPrimary);
    }

    [Fact]
    public void KeyModifiers_None_HasAllFalse()
    {
        // Act
        var modifiers = KeyModifiers.None;

        // Assert
        Assert.False(modifiers.Ctrl);
        Assert.False(modifiers.Shift);
        Assert.False(modifiers.Alt);
        Assert.False(modifiers.Meta);
    }

    [Fact]
    public void KeyModifiers_WithCtrl_HasCorrectValue()
    {
        // Arrange & Act
        var modifiers = new KeyModifiers(Ctrl: true);

        // Assert
        Assert.True(modifiers.Ctrl);
        Assert.False(modifiers.Shift);
        Assert.False(modifiers.Alt);
        Assert.False(modifiers.Meta);
    }

    [Fact]
    public void KeyModifiers_WithMultiple_HasCorrectValues()
    {
        // Arrange & Act
        var modifiers = new KeyModifiers(Ctrl: true, Shift: true, Alt: false, Meta: true);

        // Assert
        Assert.True(modifiers.Ctrl);
        Assert.True(modifiers.Shift);
        Assert.False(modifiers.Alt);
        Assert.True(modifiers.Meta);
    }

    [Fact]
    public void ChatMessage_CreatesCorrectly()
    {
        // Arrange
        var id = Guid.NewGuid();
        var timestamp = DateTimeOffset.UtcNow;

        // Act
        var message = new ChatMessage(id, Role.Host, "Hello, world!", timestamp);

        // Assert
        Assert.Equal(id, message.Id);
        Assert.Equal(Role.Host, message.Sender);
        Assert.Equal("Hello, world!", message.Content);
        Assert.Equal(timestamp, message.Timestamp);
    }

    [Theory]
    [InlineData(MouseButton.Left)]
    [InlineData(MouseButton.Right)]
    [InlineData(MouseButton.Middle)]
    public void MouseButton_AllButtonsAreDefined(MouseButton button)
    {
        Assert.True(Enum.IsDefined(button));
    }
}
