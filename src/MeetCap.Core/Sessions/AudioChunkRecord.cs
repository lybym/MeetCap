namespace MeetCap.Core.Sessions;

using MeetCap.Core.Capture;

/// <summary>
/// One audio chunk as indexed in SQLite (docs/DATA_MODEL.md section 5), plus the
/// device-timing anchors that make the recording timeline auditable.
/// </summary>
public sealed class AudioChunkRecord
{
    public required string Id { get; init; }

    public required string SessionId { get; init; }

    public required AudioSource Source { get; init; }

    /// <summary>1-based chunk number within the track.</summary>
    public required int Sequence { get; init; }

    /// <summary>
    /// Path relative to the session directory, e.g. <c>audio/mic/000001.wav</c>.
    /// Relative so a moved data root does not invalidate the index.
    /// </summary>
    public required string RelativePath { get; init; }

    public required long StartMs { get; init; }

    public required long EndMs { get; init; }

    public required AudioFormat Format { get; init; }

    public required long ByteLength { get; init; }

    public required string Status { get; set; }

    /// <summary>Device position of this chunk's first frame.</summary>
    public long? DevicePositionFrames { get; init; }

    /// <summary>Device QPC reading for this chunk's first frame, in 100-ns units.</summary>
    public long? QpcPositionTicks { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>Duration of the chunk in milliseconds.</summary>
    public long DurationMs => EndMs - StartMs;

    /// <summary>Builds the deterministic chunk id for a track position.</summary>
    public static string BuildId(string sessionId, AudioSource source, int sequence)
        => $"chk_{sessionId}_{source.ToWireName()}_{sequence:D6}";
}
