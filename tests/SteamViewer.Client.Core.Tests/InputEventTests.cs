using System.Text.Json;
using SteamViewer.Common.Protocol;

namespace SteamViewer.Client.Core.Tests;

public class InputEventTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    [Fact]
    public void MouseMove_SerializesCorrectly()
    {
        // Arrange
        var evt = new InputEvent.MouseMove(100.5, 200.75);

        // Act
        var json = JsonSerializer.Serialize<InputEvent>(evt, Options);
        var deserialized = JsonSerializer.Deserialize<InputEvent>(json, Options);

        // Assert
        var move = Assert.IsType<InputEvent.MouseMove>(deserialized);
        Assert.Equal(100.5, move.X);
        Assert.Equal(200.75, move.Y);
    }

    [Fact]
    public void MouseDown_SerializesCorrectly()
    {
        // Arrange
        var evt = new InputEvent.MouseDown(MouseButton.Left, 150.0, 250.0);

        // Act
        var json = JsonSerializer.Serialize<InputEvent>(evt, Options);
        var deserialized = JsonSerializer.Deserialize<InputEvent>(json, Options);

        // Assert
        var down = Assert.IsType<InputEvent.MouseDown>(deserialized);
        Assert.Equal(MouseButton.Left, down.Button);
        Assert.Equal(150.0, down.X);
        Assert.Equal(250.0, down.Y);
    }

    [Fact]
    public void MouseUp_SerializesCorrectly()
    {
        // Arrange
        var evt = new InputEvent.MouseUp(MouseButton.Right, 300.0, 400.0);

        // Act
        var json = JsonSerializer.Serialize<InputEvent>(evt, Options);
        var deserialized = JsonSerializer.Deserialize<InputEvent>(json, Options);

        // Assert
        var up = Assert.IsType<InputEvent.MouseUp>(deserialized);
        Assert.Equal(MouseButton.Right, up.Button);
        Assert.Equal(300.0, up.X);
        Assert.Equal(400.0, up.Y);
    }

    [Fact]
    public void MouseWheel_SerializesCorrectly()
    {
        // Arrange
        var evt = new InputEvent.MouseWheel(10.0, -20.0);

        // Act
        var json = JsonSerializer.Serialize<InputEvent>(evt, Options);
        var deserialized = JsonSerializer.Deserialize<InputEvent>(json, Options);

        // Assert
        var wheel = Assert.IsType<InputEvent.MouseWheel>(deserialized);
        Assert.Equal(10.0, wheel.DeltaX);
        Assert.Equal(-20.0, wheel.DeltaY);
    }

    [Fact]
    public void KeyDown_SerializesCorrectly()
    {
        // Arrange
        var modifiers = new KeyModifiers(Ctrl: true, Shift: false, Alt: false, Meta: false);
        var evt = new InputEvent.KeyDown("a", modifiers);

        // Act
        var json = JsonSerializer.Serialize<InputEvent>(evt, Options);
        var deserialized = JsonSerializer.Deserialize<InputEvent>(json, Options);

        // Assert
        var keyDown = Assert.IsType<InputEvent.KeyDown>(deserialized);
        Assert.Equal("a", keyDown.Key);
        Assert.True(keyDown.Modifiers.Ctrl);
        Assert.False(keyDown.Modifiers.Shift);
    }

    [Fact]
    public void KeyUp_SerializesCorrectly()
    {
        // Arrange
        var modifiers = new KeyModifiers(Ctrl: false, Shift: true, Alt: true, Meta: false);
        var evt = new InputEvent.KeyUp("Escape", modifiers);

        // Act
        var json = JsonSerializer.Serialize<InputEvent>(evt, Options);
        var deserialized = JsonSerializer.Deserialize<InputEvent>(json, Options);

        // Assert
        var keyUp = Assert.IsType<InputEvent.KeyUp>(deserialized);
        Assert.Equal("Escape", keyUp.Key);
        Assert.False(keyUp.Modifiers.Ctrl);
        Assert.True(keyUp.Modifiers.Shift);
        Assert.True(keyUp.Modifiers.Alt);
    }

    [Fact]
    public void MouseMove_ContainsTypeDiscriminator()
    {
        // Arrange
        var evt = new InputEvent.MouseMove(100.0, 200.0);

        // Act
        var json = JsonSerializer.Serialize<InputEvent>(evt, Options);

        // Assert
        Assert.Contains("\"type\":\"mouse_move\"", json);
    }

    [Fact]
    public void KeyDown_ContainsTypeDiscriminator()
    {
        // Arrange
        var evt = new InputEvent.KeyDown("Enter", KeyModifiers.None);

        // Act
        var json = JsonSerializer.Serialize<InputEvent>(evt, Options);

        // Assert
        Assert.Contains("\"type\":\"key_down\"", json);
    }
}
