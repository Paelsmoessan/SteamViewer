using System.Text.Json.Serialization;

namespace SteamViewer.Common.Protocol;

/// <summary>
/// Clipboard file transfer messages sent over the dedicated file WebRTC data channel.
/// Follows the RDP CLIPRDR virtual channel pattern: metadata first, then on-demand streaming.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(FormatList), "clipboard_file_format_list")]
[JsonDerivedType(typeof(FileContentsRequest), "clipboard_file_contents_request")]
[JsonDerivedType(typeof(FileContentsResponse), "clipboard_file_contents_response")]
[JsonDerivedType(typeof(TransferProgress), "clipboard_file_transfer_progress")]
[JsonDerivedType(typeof(StartStreaming), "clipboard_file_start_streaming")]
[JsonDerivedType(typeof(StopStreaming), "clipboard_file_stop_streaming")]
public abstract record ClipboardFileMessage
{
    /// <summary>
    /// Sent when the source machine detects files on its clipboard (CF_HDROP).
    /// Contains file metadata so the receiver can present them as pasteable immediately.
    /// </summary>
    public sealed record FormatList(
        [property: JsonPropertyName("files")] ClipboardFileInfo[] Files
    ) : ClipboardFileMessage;

    /// <summary>
    /// Sent by the receiver's IStream::Read when Explorer pastes — requests a chunk of a specific file.
    /// The source reads from disk and responds with FileContentsResponse.
    /// </summary>
    public sealed record FileContentsRequest(
        [property: JsonPropertyName("stream_id")] int StreamId,
        [property: JsonPropertyName("file_index")] int FileIndex,
        [property: JsonPropertyName("position")] long Position,
        [property: JsonPropertyName("bytes_requested")] int BytesRequested
    ) : ClipboardFileMessage;

    /// <summary>
    /// Response to FileContentsRequest — contains the requested file bytes.
    /// </summary>
    public sealed record FileContentsResponse(
        [property: JsonPropertyName("stream_id")] int StreamId,
        [property: JsonPropertyName("data")] byte[]? Data,
        [property: JsonPropertyName("is_error")] bool IsError = false,
        [property: JsonPropertyName("error_message")] string? ErrorMessage = null
    ) : ClipboardFileMessage;

    /// <summary>
    /// Periodic progress update sent over the file channel during active transfers.
    /// Sent by the serving side so the receiver can track speed.
    /// </summary>
    public sealed record TransferProgress(
        [property: JsonPropertyName("file_index")] int FileIndex,
        [property: JsonPropertyName("file_name")] string FileName,
        [property: JsonPropertyName("bytes_transferred")] long BytesTransferred,
        [property: JsonPropertyName("total_bytes")] long TotalBytes,
        [property: JsonPropertyName("speed_mbps")] double SpeedMBps
    ) : ClipboardFileMessage;

    /// <summary>
    /// Sent by the receiver to tell the sender to start push-streaming a file.
    /// Triggers a background push loop that sends chunks without waiting for individual requests.
    /// </summary>
    public sealed record StartStreaming(
        [property: JsonPropertyName("file_index")] int FileIndex
    ) : ClipboardFileMessage;

    /// <summary>
    /// Sent by the receiver to tell the sender to stop push-streaming a file.
    /// </summary>
    public sealed record StopStreaming(
        [property: JsonPropertyName("file_index")] int FileIndex
    ) : ClipboardFileMessage;
}

/// <summary>
/// Metadata for a single file on the clipboard.
/// </summary>
public sealed record ClipboardFileInfo(
    [property: JsonPropertyName("file_name")] string FileName,
    [property: JsonPropertyName("file_size")] long FileSize,
    [property: JsonPropertyName("file_attributes")] uint FileAttributes = 0,
    [property: JsonPropertyName("last_write_time")] long LastWriteTime = 0
);
