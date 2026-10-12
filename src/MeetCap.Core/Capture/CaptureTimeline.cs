namespace MeetCap.Core.Capture;

using MeetCap.Core.Diagnostics;

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

    /// <summary>
    /// Where the missing audio starts, in session-relative milliseconds, or <c>null</c>
    /// when nothing is missing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Non-null exactly when <see cref="HasGap"/> holds: a forward step that rounds to a
    /// zero-millisecond gap is not a hole this field names, so it stays <c>null</c> there
    /// rather than describing an empty interval.
    /// </para>
    /// <para>
    /// Together with <see cref="GapEndMs"/> this is the gap's own interval — audio stopped
    /// at <see cref="GapStartMs"/> and resumed at <see cref="GapEndMs"/>. It is deliberately
    /// separate from <see cref="StartMs"/>, which is where <em>this buffer</em> begins.
    /// </para>
    /// <para>
    /// The separation matters because a gap has two independent observers. The live timeline
    /// measures it while recording; the recovery gap audit
    /// (<c>MeetCap.AudioPipeline.SessionGapAuditor</c>) re-derives it from the chunk index
    /// afterwards. Both must be able to name the same hole the same way, or the record of
    /// what was lost says one thing to a listener and another to a reader
    /// (docs/RELIABILITY.md section 7).
    /// </para>
    /// </remarks>
    public long? GapStartMs { get; init; }

    /// <summary>
    /// Where the missing audio ends and captured audio resumes, in session-relative
    /// milliseconds, or <c>null</c> when nothing is missing.
    /// </summary>
    public long? GapEndMs { get; init; }
}

/// <summary>
/// The hole a device outage left at the end of a track that never recovered, in
/// session-relative milliseconds.
/// </summary>
/// <remarks>
/// The counterpart of <see cref="PacketTiming"/>'s gap fields for the case where no further
/// buffer exists to carry them: the track is ending, so the outage is named directly rather
/// than attached to a buffer that will never arrive.
/// </remarks>
public readonly record struct TerminalGap(long GapStartMs, long GapEndMs, long GapMs);

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
/// Online tracks share a pre-capture system QPC origin; single-track callers can
/// retain the first observed buffer as origin. Everything after it is
/// placed by the track's declared <see cref="CaptureClock"/>: the device position in
    /// frames for microphone capture (<see cref="CaptureClock.DevicePosition"/>), or the
    /// system QPC timestamp for loopback (<see cref="CaptureClock.Qpc"/>). Frame positions
    /// may restart, but system QPC retains its origin across a reopened stream. Unusable
    /// restart QPC is rejected instead of inventing a replacement origin.
/// </para>
/// <para>
/// The clock is declared rather than guessed, because a stream that reports no position
/// of its own reports <em>zero</em> for every buffer, and a timeline that treats those
/// zeros as positions sees a backwards jump on every buffer — one false discontinuity per
/// buffer, and a track wrongly reported degraded (docs/ARCHITECTURE.md section 8.1).
/// </para>
/// </remarks>
public sealed class CaptureTimeline
{
    /// <summary>QPC ticks in one millisecond: the WASAPI QPC unit is 100 nanoseconds.</summary>
    internal const long TicksPerMillisecond = 10_000;

    /// <summary>QPC ticks in one second, used to convert a frame count to track time.</summary>
    internal const long TicksPerSecond = 10_000_000;

    /// <summary>
    /// How far a QPC-placed buffer may sit behind the expected continuation before the
    /// timeline flags its timing as backwards.
    /// </summary>
    /// <remarks>
    /// A device position is an exact sample count, so any negative step in it is a real
    /// event. A QPC reading is a <em>time</em>, and the same stream can deliver a buffer a
    /// fraction of a millisecond "early" once the reading is expressed in whole
    /// milliseconds, so a sub-millisecond step backwards is measurement resolution rather
    /// than broken timing. One millisecond is that resolution and nothing more: system
    /// QPC does not acquire a different origin when the capture stream is reopened.
    /// </remarks>
    internal const long QpcBackwardsToleranceTicks = TicksPerMillisecond;

    private readonly AudioFormat _format;
    private readonly CaptureClock _clock;
    private readonly long? _sessionQpcOriginTicks;
    private long _initialOffsetMs;
    private bool _hasOrigin;
    private long _originFrames;
    private long _nextExpectedFrames;
    // QPC ticks scaled by sample rate: one captured frame is exactly TicksPerSecond
    // units, so fractional ticks/milliseconds survive every monotonic continuation.
    private Int128 _nextExpectedQpcUnits;
    private long _lastEndMs;
    private long? _originQpcTicks;
    private long _pendingGapMs;
    private bool _pendingSegmentRestart;

    public CaptureTimeline(AudioFormat format, CaptureClock clock = CaptureClock.DevicePosition,
        long? sessionQpcOriginTicks = null)
    {
        _format = format ?? throw new ArgumentNullException(nameof(format));
        _clock = clock;
        _sessionQpcOriginTicks = sessionQpcOriginTicks;
    }

    /// <summary>True once at least one buffer has been placed.</summary>
    public bool HasOrigin => _hasOrigin;

    /// <summary>
    /// Which device timing this timeline places buffers by
    /// (docs/ARCHITECTURE.md section 8.1).
    /// </summary>
    public CaptureClock Clock => _clock;

    /// <summary>Device position of the session origin, once known.</summary>
    public long OriginFrames => _originFrames;

    /// <summary>QPC position of the session origin, when the device supplied one.</summary>
    public long? OriginQpcTicks => _originQpcTicks;

    /// <summary>Exclusive end of the session timeline in milliseconds.</summary>
    public long LastEndMs => _lastEndMs;

    /// <summary>
    /// Total audio time this timeline knows is missing, in milliseconds.
    /// </summary>
    /// <remarks>
    /// Every gap is counted here exactly once, at the moment it is placed on the
    /// timeline. Recording-level gap accounting reads this property instead of
    /// re-deriving it, so a discontinuity cannot be counted twice (once from the
    /// device position and once from the measured outage) and cannot be silently
    /// smoothed away (docs/RELIABILITY.md section 7).
    /// </remarks>
    public long GapTotalMs { get; private set; }

    /// <summary>How many discontinuities produced <see cref="GapTotalMs"/>.</summary>
    public int GapCount { get; private set; }

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

    /// <summary>
    /// Records the outage a track measured while trying to recover, for the case where the
    /// track ends without the device ever coming back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="RecordDeviceLoss"/> deliberately defers its gap to the next buffer, because
    /// only then is the restarted stream's position known. A track that never recovers has no
    /// next buffer, so the deferral would drop the outage entirely and the session would report
    /// <c>gap_count: 0</c> for a stretch of its own timeline it knows holds no audio — which is
    /// exactly what issue #34 observed. This closes that hole by applying the measured outage
    /// immediately.
    /// </para>
    /// <para>
    /// Only the measured outage is accounted for: it is what the track actually observed while
    /// it was retrying, so the reported number is a floor on the missing audio rather than an
    /// estimate of a recovery that never happened.
    /// </para>
    /// </remarks>
    /// <returns>
    /// The named hole, or <c>null</c> when there is nothing to report: a track that never
    /// placed a buffer has no session span for audio to be missing <em>from</em>, and a
    /// sub-millisecond outage is not a hole any gap surface in this codebase can express (see
    /// <see cref="GapIntervalStart"/>).
    /// </returns>
    public TerminalGap? RecordTerminalDeviceLoss(long downtimeMs)
    {
        // The track is ending, so a deferred restart can never be applied. Clearing it keeps a
        // later call from reporting an outage that was already accounted for here.
        _pendingGapMs = 0;
        _pendingSegmentRestart = false;

        if (!_hasOrigin || downtimeMs <= 0)
        {
            return null;
        }

        var gapStartMs = _lastEndMs;
        _lastEndMs += downtimeMs;
        GapTotalMs += downtimeMs;
        GapCount++;

        return new TerminalGap(gapStartMs, _lastEndMs, downtimeMs);
    }

    /// <summary>Places one buffer on the session timeline.</summary>
    /// <exception cref="CaptureFailedException">
    /// The buffer cannot be placed by the track's declared clock at all — a
    /// <see cref="CaptureClock.Qpc"/> track whose stream reported no QPC timestamp. The
    /// timeline refuses to invent a position from a wall-clock read
    /// (docs/ARCHITECTURE.md section 8), so the caller must fail the track loudly rather
    /// than write audio at a fabricated time (docs/RELIABILITY.md section 7).
    /// </exception>
    public PacketTiming Observe(AudioPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        var frames = packet.FrameCount;
        var durationMs = _format.FramesToMilliseconds(frames);
        var flagged = packet.IsDiscontinuous;

        if (!_hasOrigin)
        {
            return ObserveOrigin(packet, frames, durationMs, flagged);
        }

        if (_pendingSegmentRestart)
        {
            return ObserveRestartedStream(packet, frames, durationMs, flagged);
        }

        return _clock == CaptureClock.Qpc
            ? PlaceByQpc(packet, flagged, restarted: false)
            : Place(durationMs, flagged, PlaceByDevicePosition(packet, frames));
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

        return (qpcTicks - _originQpcTicks.Value) / TicksPerMillisecond;
    }

    /// <summary>Where one buffer was placed, before the shared monotonic clamp.</summary>
    private readonly record struct Placement(long StartMs, long GapMs, bool Anomaly, long? GapStartMs);

    private PacketTiming ObserveOrigin(AudioPacket packet, int frames, long durationMs, bool flagged)
    {
        // A QPC track is validated before any state is claimed, so a stream that cannot be
        // placed at all leaves no origin behind: an origin would make every later call look
        // like a placed track (docs/RELIABILITY.md section 7).
        var qpcTicks = _clock == CaptureClock.Qpc || _sessionQpcOriginTicks is not null
            ? RequireQpcTicks(packet) : packet.QpcPositionTicks;
        var offsetTicks = _sessionQpcOriginTicks is { } origin ? qpcTicks!.Value - origin : 0;
        if (offsetTicks < 0)
            throw new CaptureFailedException("The first audio timestamp predates the shared session QPC origin; " +
                "its timing cannot be aligned. Reconnect the endpoint or use valid WASAPI timestamps.");
        _initialOffsetMs = offsetTicks / TicksPerMillisecond;

        // A device that failed before any audio was captured leaves no prior span to
        // lose. Its first successful packet defines the origin, so do not carry that
        // startup retry into the second packet as an invented restart/gap.
        _pendingGapMs = 0;
        _pendingSegmentRestart = false;
        _hasOrigin = true;
        _originFrames = packet.DevicePositionFrames;
        _originQpcTicks = _sessionQpcOriginTicks ?? qpcTicks;
        _nextExpectedFrames = packet.DevicePositionFrames + frames;
        _lastEndMs = _initialOffsetMs + durationMs;

        if (_clock == CaptureClock.Qpc)
        {
            _nextExpectedQpcUnits = (Int128)offsetTicks * _format.SampleRate + FramesToQpcUnits(frames);
            _lastEndMs = QpcUnitsToMilliseconds(_nextExpectedQpcUnits);
        }

        return new PacketTiming(_initialOffsetMs, _lastEndMs, 0, flagged, false);
    }

    private PacketTiming ObserveRestartedStream(
        AudioPacket packet,
        int frames,
        long durationMs,
        bool flagged)
    {
        // First buffer after a device restart: place frame-clock sources after the
        // observed outage, and QPC sources at their first frame's system timestamp.
        var gap = _pendingGapMs;
        _pendingGapMs = 0;
        _pendingSegmentRestart = false;

        if (_clock == CaptureClock.Qpc)
            return PlaceByQpc(packet, flagged, restarted: true);

        // Audio stopped where the last placed buffer ended, and resumes where the new
        // stream is placed. Captured before _lastEndMs moves.
        var gapStartMs = _lastEndMs;
        var targetMs = gapStartMs + gap;
        var endMs = targetMs + durationMs;
        _lastEndMs = endMs;

        _originFrames = packet.DevicePositionFrames - _format.MillisecondsToFrames(targetMs - _initialOffsetMs);
        _nextExpectedFrames = packet.DevicePositionFrames + frames;

        var gapStart = GapIntervalStart(gap, gapStartMs);

        if (gap > 0)
        {
            GapTotalMs += gap;
            GapCount++;
        }

        return new PacketTiming(targetMs, endMs, gap, flagged, false, SegmentRestart: true)
        {
            GapStartMs = gapStart,
            GapEndMs = gapStart is null ? null : targetMs,
        };
    }

    private Placement PlaceByDevicePosition(AudioPacket packet, int frames)
    {
        var skippedFrames = packet.DevicePositionFrames - _nextExpectedFrames;
        _nextExpectedFrames = packet.DevicePositionFrames + frames;

        if (skippedFrames >= 0)
        {
            var startMs = _initialOffsetMs + _format.FramesToMilliseconds(packet.DevicePositionFrames - _originFrames);
            return skippedFrames > 0
                // The device skipped this span: audio is missing from where the last placed
                // buffer ended up to where this one begins.
                ? new Placement(startMs, _format.FramesToMilliseconds(skippedFrames), false, _lastEndMs)
                : new Placement(startMs, 0, false, null);
        }

        // The stream restarted or the driver repeated a buffer. Keep the session
        // timeline monotonic and flag it rather than emitting a negative gap.
        return new Placement(_lastEndMs, 0, true, null);
    }

    private PacketTiming PlaceByQpc(AudioPacket packet, bool flagged, bool restarted)
    {
        var positionTicks = RequireQpcTicks(packet) - _originQpcTicks!.Value;
        var positionUnits = (Int128)positionTicks * _format.SampleRate;
        var skippedUnits = positionUnits - _nextExpectedQpcUnits;
        var toleranceUnits = (Int128)QpcBackwardsToleranceTicks * _format.SampleRate;
        if (restarted && skippedUnits < -toleranceUnits)
            throw new CaptureFailedException("The restarted capture stream supplied a QPC timestamp " +
                "that moved backwards. Its system timing is unusable; reconnect the endpoint " +
                "or use an audio device with valid WASAPI timestamps.");

        // Keep the high-water protection for early timestamps, but advance by exact
        // frame units. Rounding each buffer's ticks or duration would accumulate error.
        var startUnits = positionUnits > _nextExpectedQpcUnits ? positionUnits : _nextExpectedQpcUnits;
        var startMs = QpcUnitsToMilliseconds(startUnits);
        var previousEndMs = _lastEndMs;
        var gap = skippedUnits >= (Int128)TicksPerMillisecond * _format.SampleRate
            ? startMs - previousEndMs : 0;
        _nextExpectedQpcUnits = startUnits + FramesToQpcUnits(packet.FrameCount);
        _lastEndMs = QpcUnitsToMilliseconds(_nextExpectedQpcUnits);
        if (gap > 0)
        {
            GapTotalMs += gap;
            GapCount++;
        }
        return new PacketTiming(startMs, _lastEndMs, gap, flagged,
            !restarted && skippedUnits < -toleranceUnits, SegmentRestart: restarted)
        {
            GapStartMs = gap > 0 ? previousEndMs : null,
            GapEndMs = gap > 0 ? startMs : null,
        };
    }

    private PacketTiming Place(long durationMs, bool flagged, Placement placement)
    {
        var startMs = placement.StartMs;
        // A backwards source position can leave its original anchor behind the
        // monotonic timeline. A later positive skip is still missing audio: clamp
        // after that measured gap, rather than collapsing its interval to zero.
        var earliestStartMs = _lastEndMs + placement.GapMs;
        if (startMs < earliestStartMs)
        {
            startMs = earliestStartMs;
        }

        var endMs = startMs + durationMs;
        _lastEndMs = endMs;

        // The interval is named only when the gap is a whole millisecond of missing audio, so a
        // step that rounds to zero never leaves a zero-length hole behind
        // (docs/DATA_MODEL.md section 4.1).
        var gapStart = GapIntervalStart(placement.GapMs, placement.GapStartMs);

        if (placement.GapMs > 0)
        {
            GapTotalMs += placement.GapMs;
            GapCount++;
        }

        return new PacketTiming(startMs, endMs, placement.GapMs, flagged, placement.Anomaly)
        {
            GapStartMs = gapStart,
            GapEndMs = gapStart is null ? null : startMs,
        };
    }

    /// <summary>
    /// The gap interval's start, or <c>null</c> when there is no reportable gap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A forward step smaller than a millisecond rounds to a zero-millisecond gap. Such a step
    /// is real — the stream skipped that fraction of its own clock — but it is not reportable:
    /// the session timeline, every other gap surface and the recovery gap audit are all
    /// expressed in whole milliseconds, so a zero-millisecond gap is not a hole any of them can
    /// name. Reporting one would leave <see cref="PacketTiming.GapStartMs"/> non-null while
    /// <see cref="PacketTiming.GapMs"/> is 0, contradicting the documented invariant that the
    /// interval is null when nothing is missing, and handing a future consumer that keys off
    /// <see cref="PacketTiming.GapStartMs"/> a zero-length gap to read
    /// (docs/DATA_MODEL.md section 4.1).
    /// </para>
    /// <para>
    /// Integer QPC readings can differ from exact frame time by fractional ticks. QPC placement
    /// retains the rational frame continuation and reports only whole-ms gaps, so that source
    /// quantisation cannot introduce an empty gap interval.
    /// </para>
    /// </remarks>
    private static long? GapIntervalStart(long gapMs, long? gapStartMs)
        => gapMs > 0 ? gapStartMs : null;

    /// <summary>
    /// Exact frame duration in QPC ticks scaled by sample rate.
    /// </summary>
    /// <remarks>
    /// 512 frames at 48 kHz is 106666.67 ticks. Keeping its rational numerator rather than
    /// rounding each buffer retains both the frame duration and its fractional tick remainder.
    /// </remarks>
    private static Int128 FramesToQpcUnits(long frames) => (Int128)frames * TicksPerSecond;

    private long QpcUnitsToMilliseconds(Int128 units)
        => (long)(units / ((long)_format.SampleRate * TicksPerMillisecond));

    private static long RequireQpcTicks(AudioPacket packet)
    {
        if (packet.QpcPositionTicks is { } ticks && ticks >= 0
            && !packet.Flags.HasFlag(AudioBufferFlags.TimestampError)) return ticks;
        throw new CaptureFailedException(
            "The capture stream supplied no usable QPC timestamp (missing, negative or TimestampError), " +
            "so its audio cannot be placed on the session timeline. Reconnect the endpoint or use " +
            "an audio device with valid WASAPI timestamps; offline microphone capture is also available.");
    }
}
