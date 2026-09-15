namespace MeetCap.Core.Capture;

/// <summary>
/// A single captured buffer handed across the capture boundary, carrying the timing
/// metadata the device exposed for it.
/// </summary>
/// <remarks>
/// <para>
/// This is the MeetCap-owned packet type required by Issue #3: device position and
/// QPC position are preserved, but no vendored audio type appears here
/// (docs/ARCHITECTURE.md section 8).
/// </para>
/// <para>
/// The payload is expected to be a private copy taken on the capture thread. The
/// capture boundary must not hand a vendor-owned span across threads.
/// </para>
/// </remarks>
public sealed class AudioPacket
{
    public AudioPacket(
        AudioSource source,
        AudioFormat format,
        ReadOnlyMemory<byte> data,
        long devicePositionFrames,
        long? qpcPositionTicks,
        DateTimeOffset capturedAtUtc,
        AudioBufferFlags flags = AudioBufferFlags.None)
    {
        ArgumentNullException.ThrowIfNull(format);

        if (data.Length % format.BlockAlign != 0)
        {
            throw new ArgumentException(
                $"Packet payload of {data.Length} bytes is not frame-aligned for {format} " +
                $"(block align {format.BlockAlign}).",
                nameof(data));
        }

        Source = source;
        Format = format;
        Data = data;
        DevicePositionFrames = devicePositionFrames;
        QpcPositionTicks = qpcPositionTicks;
        CapturedAtUtc = capturedAtUtc;
        Flags = flags;
    }

    public AudioSource Source { get; }

    public AudioFormat Format { get; }

    /// <summary>The captured bytes, exactly as the device delivered them.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>
    /// Position of the first frame in this buffer, in the device's own stream.
    /// This is the primary session-timeline source (docs/ARCHITECTURE.md section 8).
    /// </summary>
    public long DevicePositionFrames { get; }

    /// <summary>
    /// Device QPC timestamp in 100-nanosecond units, when the device supplied one.
    /// Retained for cross-checking and diagnostics.
    /// </summary>
    public long? QpcPositionTicks { get; }

    /// <summary>When the boundary observed this buffer.</summary>
    public DateTimeOffset CapturedAtUtc { get; }

    public AudioBufferFlags Flags { get; }

    public bool HasData => !Data.IsEmpty;

    public int FrameCount => Data.Length / Format.BlockAlign;

    /// <summary>Duration of this buffer in milliseconds.</summary>
    public long DurationMs => Format.FramesToMilliseconds(FrameCount);

    /// <summary>Whether the device flagged a gap or an untrustworthy timestamp.</summary>
    public bool IsDiscontinuous
        => Flags.HasFlag(AudioBufferFlags.DataDiscontinuity) || Flags.HasFlag(AudioBufferFlags.TimestampError);
}
