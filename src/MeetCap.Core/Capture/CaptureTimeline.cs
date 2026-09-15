namespace MeetCap.Core.Capture;

/// <summary>
/// Session-relative placement of one captured buffer, plus whatever the timeline
/// noticed about continuity while placing it.
/// </summary>
public readonly record struct PacketTiming(
    long StartMs,
    long EndMs,
    long GapMs,
    bool Discontinuity,
    bool DevicePositionAnomaly,
    bool SegmentRestart = false)
{
    /// <summary>Frames the device skipped before this buffer, in milliseconds.</summary>
    public bool HasGap => GapMs > 0;

    /// <summary>Whether anything about this buffer needs an explicit event.</summary>
    public bool IsIrregular => HasGap || Discontinuity || DevicePositionAnomaly;

    /// <summary>
    /// True for the first buffer after capture was restarted on a fresh device stream.
    /// The recording pipeline closes the previous chunk on this boundary, so audio from
    /// before an outage is durable even if the outage was shorter than a chunk.
    /// </summary>
    public bool IsNewSegment => SegmentRestart;
}

/// <summary>
/// Maps device positions to session-relative milliseconds for one track.
/// </summary>
/// <remarks>
/// <para>
/// docs/ARCHITECTURE.md section 8 requires the unified timeline to be built from
/// device timing, not wall-clock reads, and docs/RELIABILITY.md section 7 requires
/// discontinuities to become explicit gap events instead of being hidden by simply
/// shifting later timestamps.
/// </para>
/// <para>
/// The first observed buffer defines the session origin. Everything after it is
/// placed by device position, so a device that restarts its stream (position goes
/// backwards) cannot make the session timeline run backwards: it is clamped and
/// flagged instead.
/// </para>
/// </remarks>
public sealed class CaptureTimeline
{
    private readonly AudioFormat _format;
    private bool _hasOrigin;
    private long _originFrames;
    private long _nextExpectedFrames;
    private long _lastEndMs;
    private long? _originQpcTicks;
    private long _pendingGapMs;
    private bool _pendingSegmentRestart;

    public CaptureTimeline(AudioFormat format)
    {
        _format = format ?? throw new ArgumentNullException(nameof(format));
    }

    /// <summary>True once at least one buffer has been placed.</summary>
    public bool HasOrigin => _hasOrigin;

    /// <summary>Device position of the session origin, once known.</summary>
    public long OriginFrames => _originFrames;

    /// <summary>QPC position of the session origin, when the device supplied one.</summary>
    public long? OriginQpcTicks => _originQpcTicks;

    /// <summary>Exclusive end of the session timeline in milliseconds.</summary>
    public long LastEndMs => _lastEndMs;

    /// <summary>
    /// Records that capture was interrupted for <paramref name="downtimeMs"/> and
    /// that the device stream restarted afterwards.
    /// </summary>
    /// <remarks>
    /// The gap is applied when the next buffer arrives, because only then is the new
    /// stream's device position known. docs/RELIABILITY.md section 7 requires the
    /// missing time to be an explicit gap rather than something the timeline hides by
    /// shifting later timestamps.
    /// </remarks>
    public void RecordDeviceLoss(long downtimeMs)
    {
        if (downtimeMs > 0)
        {
            _pendingGapMs += downtimeMs;
        }

        // A zero downtime still marks a restart: the transport changed even if no time
        // was measurably lost.
        _pendingSegmentRestart = true;
    }

    /// <summary>Places one buffer on the session timeline.</summary>
    public PacketTiming Observe(AudioPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        var frames = packet.FrameCount;
        var durationMs = _format.FramesToMilliseconds(frames);
        var flagged = packet.IsDiscontinuous;

        if (!_hasOrigin)
        {
            _hasOrigin = true;
            _originFrames = packet.DevicePositionFrames;
            _originQpcTicks = packet.QpcPositionTicks;
            _nextExpectedFrames = packet.DevicePositionFrames + frames;
            _lastEndMs = durationMs;
            return new PacketTiming(0, durationMs, 0, flagged, false);
        }

        if (_pendingSegmentRestart)
        {
            // First buffer after a device restart: keep the session timeline
            // continuous by placing the new stream immediately after the outage.
            var gap = _pendingGapMs;
            _pendingGapMs = 0;
            _pendingSegmentRestart = false;

            var targetMs = _lastEndMs + gap;
            _originFrames = packet.DevicePositionFrames - _format.MillisecondsToFrames(targetMs);
            _nextExpectedFrames = packet.DevicePositionFrames + frames;
            _lastEndMs = targetMs + durationMs;
            return new PacketTiming(targetMs, _lastEndMs, gap, flagged, false, SegmentRestart: true);
        }

        var skippedFrames = packet.DevicePositionFrames - _nextExpectedFrames;
        var anomaly = false;
        long gapMs = 0;
        long startMs;

        if (skippedFrames >= 0)
        {
            startMs = _format.FramesToMilliseconds(packet.DevicePositionFrames - _originFrames);
            if (skippedFrames > 0)
            {
                gapMs = _format.FramesToMilliseconds(skippedFrames);
            }
        }
        else
        {
            // The stream restarted or the driver repeated a buffer. Keep the session
            // timeline monotonic and flag it rather than emitting a negative gap.
            anomaly = true;
            startMs = _lastEndMs;
        }

        if (startMs < _lastEndMs)
        {
            startMs = _lastEndMs;
        }

        var endMs = startMs + durationMs;
        _nextExpectedFrames = packet.DevicePositionFrames + frames;
        _lastEndMs = endMs;

        return new PacketTiming(startMs, endMs, gapMs, flagged, anomaly);
    }

    /// <summary>
    /// Places a device QPC reading (100-ns units) on the session timeline, for
    /// cross-checking the device-position timeline.
    /// </summary>
    public long? QpcToSessionMs(long qpcTicks)
    {
        if (_originQpcTicks is null)
        {
            return null;
        }

        return (qpcTicks - _originQpcTicks.Value) / 10_000;
    }
}
