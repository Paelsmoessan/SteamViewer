using System.Text.Json.Serialization;

namespace SteamViewer.Common.Protocol;

/// <summary>
/// File transfer messages sent over the custom UDP transport's file channel.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Request), "request")]
[JsonDerivedType(typeof(Accept), "accept")]
[JsonDerivedType(typeof(Reject), "reject")]
[JsonDerivedType(typeof(Chunk), "chunk")]
[JsonDerivedType(typeof(Complete), "complete")]
[JsonDerivedType(typeof(FileError), "error")]
[JsonDerivedType(typeof(Progress), "progress")]
public abstract record FileTransferMessage
{
    public sealed record Request(
        [property: JsonPropertyName("transfer_id")] Guid TransferId,
        [property: JsonPropertyName("filename")] string Filename,
        [property: JsonPropertyName("file_size")] ulong FileSize
    ) : FileTransferMessage;

    public sealed record Accept(
        [property: JsonPropertyName("transfer_id")] Guid TransferId
    ) : FileTransferMessage;

    public sealed record Reject(
        [property: JsonPropertyName("transfer_id")] Guid TransferId,
        [property: JsonPropertyName("reason")] string Reason
    ) : FileTransferMessage;

    public sealed record Chunk(
        [property: JsonPropertyName("transfer_id")] Guid TransferId,
        [property: JsonPropertyName("chunk_index")] ulong ChunkIndex,
        [property: JsonPropertyName("data")] byte[] Data
    ) : FileTransferMessage;

    public sealed record Complete(
        [property: JsonPropertyName("transfer_id")] Guid TransferId
    ) : FileTransferMessage;

    public sealed record FileError(
        [property: JsonPropertyName("transfer_id")] Guid TransferId,
        [property: JsonPropertyName("message")] string Message
    ) : FileTransferMessage;

    public sealed record Progress(
        [property: JsonPropertyName("transfer_id")] Guid TransferId,
        [property: JsonPropertyName("bytes_transferred")] ulong BytesTransferred,
        [property: JsonPropertyName("total_bytes")] ulong TotalBytes
    ) : FileTransferMessage;
}
