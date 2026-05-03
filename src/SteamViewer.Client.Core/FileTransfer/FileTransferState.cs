namespace SteamViewer.Client.Core.FileTransfer;

/// <summary>
/// Represents the state of a single file transfer.
/// </summary>
public sealed class FileTransferState
{
    /// <summary>
    /// Unique identifier for this transfer.
    /// </summary>
    public Guid TransferId { get; }

    /// <summary>
    /// Name of the file being transferred.
    /// </summary>
    public string Filename { get; }

    /// <summary>
    /// Total size of the file in bytes.
    /// </summary>
    public ulong FileSize { get; }

    /// <summary>
    /// Direction of the transfer.
    /// </summary>
    public FileTransferDirection Direction { get; }

    /// <summary>
    /// Current status of the transfer.
    /// </summary>
    public FileTransferStatus Status { get; set; }

    /// <summary>
    /// Number of bytes transferred so far.
    /// </summary>
    public ulong BytesTransferred { get; set; }

    /// <summary>
    /// Local file path (for sending: source, for receiving: destination).
    /// </summary>
    public string? LocalPath { get; set; }

    /// <summary>
    /// File stream for reading/writing.
    /// </summary>
    public FileStream? FileStream { get; set; }

    /// <summary>
    /// Time when transfer started.
    /// </summary>
    public DateTimeOffset? StartTime { get; set; }

    /// <summary>
    /// Error message if transfer failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Chunk size in bytes (64KB).
    /// </summary>
    public const int ChunkSize = 64 * 1024;

    /// <summary>
    /// Expected number of chunks.
    /// </summary>
    public ulong ExpectedChunks => (FileSize + ChunkSize - 1) / ChunkSize;

    /// <summary>
    /// Transfer progress as percentage (0-100).
    /// </summary>
    public double ProgressPercent => FileSize > 0 ? (double)BytesTransferred / FileSize * 100 : 0;

    /// <summary>
    /// Transfer speed in bytes per second.
    /// </summary>
    public double SpeedBytesPerSecond
    {
        get
        {
            if (StartTime == null || BytesTransferred == 0)
                return 0;
            var elapsed = (DateTimeOffset.UtcNow - StartTime.Value).TotalSeconds;
            return elapsed > 0 ? BytesTransferred / elapsed : 0;
        }
    }

    /// <summary>
    /// Estimated time remaining.
    /// </summary>
    public TimeSpan? EstimatedTimeRemaining
    {
        get
        {
            var speed = SpeedBytesPerSecond;
            if (speed <= 0 || BytesTransferred >= FileSize)
                return null;
            var remaining = FileSize - BytesTransferred;
            return TimeSpan.FromSeconds(remaining / speed);
        }
    }

    public FileTransferState(Guid transferId, string filename, ulong fileSize, FileTransferDirection direction)
    {
        TransferId = transferId;
        Filename = filename;
        FileSize = fileSize;
        Direction = direction;
        Status = FileTransferStatus.Pending;
    }

    /// <summary>
    /// Close and cleanup resources.
    /// </summary>
    public void Cleanup()
    {
        FileStream?.Dispose();
        FileStream = null;
    }
}

/// <summary>
/// Direction of file transfer.
/// </summary>
public enum FileTransferDirection
{
    /// <summary>Sending a file to the peer.</summary>
    Sending,

    /// <summary>Receiving a file from the peer.</summary>
    Receiving
}

/// <summary>
/// Status of a file transfer.
/// </summary>
public enum FileTransferStatus
{
    /// <summary>Transfer requested, waiting for response.</summary>
    Pending,

    /// <summary>Transfer in progress.</summary>
    InProgress,

    /// <summary>Transfer completed successfully.</summary>
    Completed,

    /// <summary>Transfer failed.</summary>
    Failed,

    /// <summary>Transfer was rejected.</summary>
    Rejected,

    /// <summary>Transfer was cancelled.</summary>
    Cancelled
}
