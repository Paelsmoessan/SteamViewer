using System.Text.Json;
using SteamViewer.Common.Protocol;

namespace SteamViewer.Client.Core.Tests;

public class FileTransferMessageTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    [Fact]
    public void Request_SerializesCorrectly()
    {
        // Arrange
        var transferId = Guid.NewGuid();
        var message = new FileTransferMessage.Request(transferId, "document.pdf", 1024000);

        // Act
        var json = JsonSerializer.Serialize<FileTransferMessage>(message, Options);
        var deserialized = JsonSerializer.Deserialize<FileTransferMessage>(json, Options);

        // Assert
        var request = Assert.IsType<FileTransferMessage.Request>(deserialized);
        Assert.Equal(transferId, request.TransferId);
        Assert.Equal("document.pdf", request.Filename);
        Assert.Equal((ulong)1024000, request.FileSize);
    }

    [Fact]
    public void Accept_SerializesCorrectly()
    {
        // Arrange
        var transferId = Guid.NewGuid();
        var message = new FileTransferMessage.Accept(transferId);

        // Act
        var json = JsonSerializer.Serialize<FileTransferMessage>(message, Options);
        var deserialized = JsonSerializer.Deserialize<FileTransferMessage>(json, Options);

        // Assert
        var accept = Assert.IsType<FileTransferMessage.Accept>(deserialized);
        Assert.Equal(transferId, accept.TransferId);
    }

    [Fact]
    public void Reject_SerializesCorrectly()
    {
        // Arrange
        var transferId = Guid.NewGuid();
        var message = new FileTransferMessage.Reject(transferId, "Not enough disk space");

        // Act
        var json = JsonSerializer.Serialize<FileTransferMessage>(message, Options);
        var deserialized = JsonSerializer.Deserialize<FileTransferMessage>(json, Options);

        // Assert
        var reject = Assert.IsType<FileTransferMessage.Reject>(deserialized);
        Assert.Equal(transferId, reject.TransferId);
        Assert.Equal("Not enough disk space", reject.Reason);
    }

    [Fact]
    public void Chunk_SerializesCorrectly()
    {
        // Arrange
        var transferId = Guid.NewGuid();
        var data = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };
        var message = new FileTransferMessage.Chunk(transferId, 0, data);

        // Act
        var json = JsonSerializer.Serialize<FileTransferMessage>(message, Options);
        var deserialized = JsonSerializer.Deserialize<FileTransferMessage>(json, Options);

        // Assert
        var chunk = Assert.IsType<FileTransferMessage.Chunk>(deserialized);
        Assert.Equal(transferId, chunk.TransferId);
        Assert.Equal((ulong)0, chunk.ChunkIndex);
        Assert.Equal(data, chunk.Data);
    }

    [Fact]
    public void Complete_SerializesCorrectly()
    {
        // Arrange
        var transferId = Guid.NewGuid();
        var message = new FileTransferMessage.Complete(transferId);

        // Act
        var json = JsonSerializer.Serialize<FileTransferMessage>(message, Options);
        var deserialized = JsonSerializer.Deserialize<FileTransferMessage>(json, Options);

        // Assert
        var complete = Assert.IsType<FileTransferMessage.Complete>(deserialized);
        Assert.Equal(transferId, complete.TransferId);
    }

    [Fact]
    public void FileError_SerializesCorrectly()
    {
        // Arrange
        var transferId = Guid.NewGuid();
        var message = new FileTransferMessage.FileError(transferId, "Transfer interrupted");

        // Act
        var json = JsonSerializer.Serialize<FileTransferMessage>(message, Options);
        var deserialized = JsonSerializer.Deserialize<FileTransferMessage>(json, Options);

        // Assert
        var error = Assert.IsType<FileTransferMessage.FileError>(deserialized);
        Assert.Equal(transferId, error.TransferId);
        Assert.Equal("Transfer interrupted", error.Message);
    }

    [Fact]
    public void Progress_SerializesCorrectly()
    {
        // Arrange
        var transferId = Guid.NewGuid();
        var message = new FileTransferMessage.Progress(transferId, 512000, 1024000);

        // Act
        var json = JsonSerializer.Serialize<FileTransferMessage>(message, Options);
        var deserialized = JsonSerializer.Deserialize<FileTransferMessage>(json, Options);

        // Assert
        var progress = Assert.IsType<FileTransferMessage.Progress>(deserialized);
        Assert.Equal(transferId, progress.TransferId);
        Assert.Equal((ulong)512000, progress.BytesTransferred);
        Assert.Equal((ulong)1024000, progress.TotalBytes);
    }

    [Fact]
    public void Progress_CalculatesPercentageCorrectly()
    {
        // Arrange
        var progress = new FileTransferMessage.Progress(Guid.NewGuid(), 512000, 1024000);

        // Act
        var percentage = (double)progress.BytesTransferred / progress.TotalBytes * 100;

        // Assert
        Assert.Equal(50.0, percentage);
    }

    [Fact]
    public void Request_ContainsTypeDiscriminator()
    {
        // Arrange
        var message = new FileTransferMessage.Request(Guid.NewGuid(), "test.txt", 100);

        // Act
        var json = JsonSerializer.Serialize<FileTransferMessage>(message, Options);

        // Assert
        Assert.Contains("\"type\":\"request\"", json);
    }
}
