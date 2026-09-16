namespace MeetCap.Core.Sessions;

using MeetCap.Core.Capture;

/// <summary>
/// A capture chunk that has been closed <em>durably</em>: its header was patched,
/// flushed, validated and atomically renamed out of <c>.part</c>, and its index row
/// records it as a durable chunk (<c>docs/ARCHITECTURE.md</c> section 9,
/// <c>docs/RELIABILITY.md</c> section 5).
/// </summary>
/// <remarks>
/// <para>
/// The recording path raises this instead of letting downstream work reach into the
/// chunk spool. Only a durable chunk may enter ASR batching, so announcing the chunk
/// at exactly this point is what keeps a batch from ever naming a file that is not
/// closed yet (<c>docs/ARCHITECTURE.md</c> section 10).
/// </para>
/// <para>
/// The type carries session-relative timing only. It is deliberately free of any
/// provider, HTTP, or job concept, so the recording assembly can raise it without
/// depending on the ASR stack (<c>docs/DEVELOPMENT.md</c> section 4).
/// </para>
/// </remarks>
public sealed record ClosedAudioChunk
{
    public required string SessionId { get; init; }

    /// <summary>Source track, e.g. <c>mic</c>.</summary>
    public required string Source { get; init; }

    /// <summary>1-based chunk number within the track.</summary>
    public required int Sequence { get; init; }

    /// <summary>Absolute path of the durable <c>.wav</c> chunk.</summary>
    public required string FilePath { get; init; }

    /// <summary>
    /// Path relative to the session directory with forward slashes, e.g.
    /// <c>audio/mic/000001.wav</c>. This is the form the artifact contract stores, so
    /// it survives moving the data root (<c>docs/DATA_MODEL.md</c> section 6).
    /// </summary>
    public required string RelativePath { get; init; }

    /// <summary>Session-relative position of the chunk's first frame.</summary>
    public required long StartMs { get; init; }

    /// <summary>Session-relative exclusive end of the chunk's audio.</summary>
    public required long EndMs { get; init; }

    /// <summary>Audio bytes in the chunk, excluding the 44-byte WAV header.</summary>
    public required long DataBytes { get; init; }

    /// <summary>The track's native capture format, which the chunk's header describes.</summary>
    public required AudioFormat Format { get; init; }

    /// <summary>Duration of the chunk's audio.</summary>
    public long DurationMs => EndMs - StartMs;
}
