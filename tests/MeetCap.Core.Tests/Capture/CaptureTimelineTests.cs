using MeetCap.Core.Capture;
using MeetCap.Core.Diagnostics;
using Xunit;

namespace MeetCap.Core.Tests.Capture;

/// <summary>
/// The timeline is what proves docs/ARCHITECTURE.md section 8 (device timing, not
/// wall-clock) and docs/RELIABILITY.md section 7 (gaps become explicit events).
/// </summary>
public class CaptureTimelineTests
{
    private static readonly AudioFormat Mono48k = new(48_000, 1, 16, AudioSampleFormat.Pcm);

    /// <summary>480 frames at 48 kHz is exactly 10 ms.</summary>
    private const int TenMs = 480;

    [Fact]
    public void Observe_FirstPacket_DefinesTheSessionOrigin()
    {
        var timeline = new CaptureTimeline(Mono48k);

        var timing = timeline.Observe(Packet(devicePosition: 12_345, frames: TenMs, qpc: 999));

        Assert.Equal(0, timing.StartMs);
        Assert.Equal(10, timing.EndMs);
        Assert.False(timing.IsIrregular);
        Assert.True(timeline.HasOrigin);
        Assert.Equal(12_345, timeline.OriginFrames);
        Assert.Equal(999, timeline.OriginQpcTicks);
    }

    [Fact]
    public void Observe_ContiguousPackets_ProduceAContinuousTimeline()
    {
        var timeline = new CaptureTimeline(Mono48k);

        timeline.Observe(Packet(0, TenMs));
        timeline.Observe(Packet(TenMs, TenMs));
        var third = timeline.Observe(Packet(2 * TenMs, TenMs));

        Assert.Equal(20, third.StartMs);
        Assert.Equal(30, third.EndMs);
        Assert.Equal(0, third.GapMs);
        Assert.False(third.IsIrregular);
        Assert.Equal(30, timeline.LastEndMs);
    }

    [Fact]
    public void Observe_DeviceSkipsFrames_ReportsTheGapInsteadOfShiftingTime()
    {
        var timeline = new CaptureTimeline(Mono48k);

        timeline.Observe(Packet(0, TenMs));

        // The device jumped one second (48,000 frames) ahead: the next buffer belongs
        // 1010 ms into the session, and the missing second must be reported rather than
        // absorbed by shifting later timestamps.
        var timing = timeline.Observe(Packet(TenMs + Mono48k.SampleRate, TenMs));

        Assert.Equal(1_010, timing.StartMs);
        Assert.Equal(1_020, timing.EndMs);
        Assert.Equal(1_000, timing.GapMs);
        Assert.True(timing.HasGap);
        Assert.True(timing.IsIrregular);
    }

    [Fact]
    public void Observe_DevicePositionMovesBackwards_KeepsTheTimelineMonotonicAndFlagsIt()
    {
        var timeline = new CaptureTimeline(Mono48k);

        timeline.Observe(Packet(50_000, TenMs));

        // A restarted stream reports a small position again.
        var timing = timeline.Observe(Packet(0, TenMs));

        Assert.True(timing.DevicePositionAnomaly);
        Assert.Equal(10, timing.StartMs);
        Assert.Equal(20, timing.EndMs);
        Assert.Equal(0, timing.GapMs);
        Assert.Equal(20, timeline.LastEndMs);
    }

    [Fact]
    public void Observe_DeviceFlags_AreSurfaced()
    {
        var timeline = new CaptureTimeline(Mono48k);

        var timing = timeline.Observe(
            Packet(0, TenMs, flags: AudioBufferFlags.DataDiscontinuity));

        Assert.True(timing.Discontinuity);
        Assert.True(timing.IsIrregular);
    }

    [Fact]
    public void RecordDeviceLoss_PlacesTheRestartedStreamAfterTheOutage()
    {
        var timeline = new CaptureTimeline(Mono48k);

        timeline.Observe(Packet(1_000, TenMs)); // 0..10 ms
        timeline.RecordDeviceLoss(5_000);

        // The device came back and restarted its own stream position.
        var timing = timeline.Observe(Packet(0, TenMs));

        Assert.Equal(5_000, timing.GapMs);
        Assert.Equal(5_010, timing.StartMs);
        Assert.Equal(5_020, timing.EndMs);
        Assert.False(timing.DevicePositionAnomaly);
        Assert.Equal(5_020, timeline.LastEndMs);
    }

    [Fact]
    public void RecordDeviceLoss_OnlyAffectsTheNextPacket()
    {
        var timeline = new CaptureTimeline(Mono48k);

        timeline.Observe(Packet(0, TenMs));
        timeline.RecordDeviceLoss(1_000);
        timeline.Observe(Packet(0, TenMs));

        var after = timeline.Observe(Packet(TenMs, TenMs));

        Assert.Equal(0, after.GapMs);
        Assert.Equal(1_010 + 10, after.StartMs);
    }

    [Fact]
    public void RecordDeviceLoss_WithZeroDowntime_StillMarksTheSegmentRestart()
    {
        var timeline = new CaptureTimeline(Mono48k);

        timeline.Observe(Packet(0, TenMs));
        timeline.RecordDeviceLoss(0);
        var timing = timeline.Observe(Packet(TenMs, TenMs));

        Assert.Equal(0, timing.GapMs);
        Assert.True(timing.IsNewSegment);
    }

    [Fact]
    public void QpcToSessionMs_UsesTheFirstPacketAsTheAnchor()
    {
        var timeline = new CaptureTimeline(Mono48k);

        timeline.Observe(Packet(0, TenMs, qpc: 1_000_000));

        // QPC arrives in 100-nanosecond units, so 10,000 ticks is 1 ms.
        Assert.Equal(0, timeline.QpcToSessionMs(1_000_000));
        Assert.Equal(1, timeline.QpcToSessionMs(1_010_000));
        Assert.Null(new CaptureTimeline(Mono48k).QpcToSessionMs(1));
    }

    // ------------------------------------------------------------------ gap accounting
    //
    // docs/RELIABILITY.md section 7 requires a discontinuity to be explicit. The session
    // records how much audio the timeline knows is missing, so that number has to be
    // counted once per discontinuity and never invented for continuous audio.

    [Fact]
    public void GapTotalMs_StartsAtZeroForAContinuousTimeline()
    {
        var timeline = new CaptureTimeline(Mono48k);

        timeline.Observe(Packet(0, TenMs));
        timeline.Observe(Packet(TenMs, TenMs));

        Assert.Equal(0, timeline.GapTotalMs);
        Assert.Equal(0, timeline.GapCount);
    }

    [Fact]
    public void GapTotalMs_AccumulatesEveryDeviceSkipExactlyOnce()
    {
        var timeline = new CaptureTimeline(Mono48k);

        timeline.Observe(Packet(0, TenMs));
        timeline.Observe(Packet(TenMs + Mono48k.SampleRate, TenMs)); // 1 s skipped
        timeline.Observe(Packet(2 * TenMs + 3 * Mono48k.SampleRate, TenMs)); // 2 s more

        Assert.Equal(3_000, timeline.GapTotalMs);
        Assert.Equal(2, timeline.GapCount);
    }

    [Fact]
    public void GapTotalMs_CountsAMeasuredDeviceOutageOnce()
    {
        var timeline = new CaptureTimeline(Mono48k);

        timeline.Observe(Packet(0, TenMs));
        timeline.RecordDeviceLoss(5_000);
        timeline.Observe(Packet(0, TenMs));

        // The measured outage is the gap. The device's own restart position must not be
        // added on top of it, or the session would report twice the audio it lost.
        Assert.Equal(5_000, timeline.GapTotalMs);
        Assert.Equal(1, timeline.GapCount);
    }

    [Fact]
    public void GapTotalMs_IgnoresABackwardsDevicePosition()
    {
        var timeline = new CaptureTimeline(Mono48k);

        timeline.Observe(Packet(50_000, TenMs));
        var timing = timeline.Observe(Packet(0, TenMs));

        // A backwards jump is a restarted stream, not lost audio.
        Assert.True(timing.DevicePositionAnomaly);
        Assert.Equal(0, timeline.GapTotalMs);
        Assert.Equal(0, timeline.GapCount);
    }

    // ------------------------------------------------------------- gap identity
    //
    // A gap has two independent observers: the live timeline, and the recovery gap audit that
    // re-derives the same hole from the chunk index. Both have to be able to name it the same
    // way, which is why the gap carries its own interval instead of only the position of the
    // buffer that follows it (docs/RELIABILITY.md section 7).

    [Fact]
    public void GapInterval_NamesTheMissingStretchSeparatelyFromTheBufferPosition()
    {
        var timeline = new CaptureTimeline(Mono48k);

        timeline.Observe(Packet(0, TenMs)); // placed at 0..10 ms

        // The device skipped one second: audio is missing from 10 ms to 1010 ms, and this
        // buffer begins where the audio resumes.
        var timing = timeline.Observe(Packet(TenMs + Mono48k.SampleRate, TenMs));

        Assert.Equal(1_010, timing.StartMs);
        Assert.Equal(10, timing.GapStartMs);
        Assert.Equal(1_010, timing.GapEndMs);
    }

    [Fact]
    public void GapInterval_SpansAMeasuredDeviceOutage()
    {
        var timeline = new CaptureTimeline(Mono48k);

        timeline.Observe(Packet(0, TenMs)); // placed at 0..10 ms
        timeline.RecordDeviceLoss(5_000);
        var timing = timeline.Observe(Packet(0, TenMs));

        // Audio stopped at 10 ms and resumes at 5010 ms: the outage is the gap, and it is not
        // conflated with where the buffer itself sits.
        Assert.Equal(10, timing.GapStartMs);
        Assert.Equal(5_010, timing.GapEndMs);
        Assert.Equal(5_010, timing.StartMs);
    }

    [Fact]
    public void GapInterval_IsAbsentWhenNothingIsMissing()
    {
        var timeline = new CaptureTimeline(Mono48k);

        timeline.Observe(Packet(0, TenMs));
        var contiguous = timeline.Observe(Packet(TenMs, TenMs));

        Assert.Null(contiguous.GapStartMs);
        Assert.Null(contiguous.GapEndMs);

        // A backwards jump is flagged but is not a gap either.
        var anomaly = timeline.Observe(Packet(0, TenMs));
        Assert.True(anomaly.DevicePositionAnomaly);
        Assert.Null(anomaly.GapStartMs);
        Assert.Null(anomaly.GapEndMs);
    }

    // ------------------------------------------------------------------- capture clock
    //
    // Issue #33: on a real Windows machine the baseline sources report a device position
    // and the process-loopback source reports none — every one of its buffers carries
    // device_position_frames = 0 while only the QPC timestamp advances. The clock a track
    // is placed by is therefore declared by the capture source rather than inferred from
    // the buffer values (docs/ARCHITECTURE.md section 8.1). These tests are the regression
    // test for that handling: they place the same packet stream twice, once by device
    // position (which is what produced one false discontinuity per buffer) and once by QPC.

    [Fact]
    public void DevicePositionClock_AllZeroPositions_FlagsEveryBufferAfterTheFirst()
    {
        // The defect's mechanism, kept explicit: a stream with no position of its own must
        // never be placed by device position, because its zeros read as a backwards jump on
        // every buffer (issue #33, docs/ARCHITECTURE.md section 8.1).
        var timeline = new CaptureTimeline(Mono48k);

        var first = timeline.Observe(ProcessLoopbackPacket(0));
        Assert.False(first.DevicePositionAnomaly);

        for (var i = 1; i < 20; i++)
        {
            Assert.True(timeline.Observe(ProcessLoopbackPacket(i * TenMs)).DevicePositionAnomaly);
        }

        // A backwards position is a restarted stream, not lost audio, so the false
        // anomalies never inflated the gap accounting either.
        Assert.Equal(0, timeline.GapTotalMs);
        Assert.Equal(0, timeline.GapCount);
    }

    [Fact]
    public void QpcClock_ProcessLoopbackBuffers_ProduceAContinuousTimeline()
    {
        var timeline = new CaptureTimeline(Mono48k, CaptureClock.Qpc);

        for (var i = 0; i < 20; i++)
        {
            var timing = timeline.Observe(ProcessLoopbackPacket(i * TenMs));

            Assert.False(timing.DevicePositionAnomaly);
            Assert.False(timing.Discontinuity);
            Assert.False(timing.IsIrregular);
            Assert.Equal(0, timing.GapMs);
            Assert.Equal(i * 10, timing.StartMs);
            Assert.Equal((i + 1) * 10, timing.EndMs);
        }

        Assert.Equal(CaptureClock.Qpc, timeline.Clock);
        Assert.Equal(200, timeline.LastEndMs);
        Assert.Equal(0, timeline.GapTotalMs);
        Assert.Equal(0, timeline.GapCount);
    }

    [Fact]
    public void QpcClock_SkipsAStretchOfItsOwnClock_ReportsTheGapInsteadOfAnAnomaly()
    {
        var timeline = new CaptureTimeline(Mono48k, CaptureClock.Qpc);

        timeline.Observe(ProcessLoopbackPacket(0)); // placed at 0..10 ms

        // The stream produced nothing for a second: its QPC time resumes at 1010 ms.
        var timing = timeline.Observe(ProcessLoopbackPacket(101 * TenMs));

        Assert.Equal(1_010, timing.StartMs);
        Assert.Equal(1_020, timing.EndMs);
        Assert.Equal(1_000, timing.GapMs);
        Assert.Equal(10, timing.GapStartMs);
        Assert.Equal(1_010, timing.GapEndMs);
        Assert.False(timing.DevicePositionAnomaly);
        Assert.Equal(1_000, timeline.GapTotalMs);
        Assert.Equal(1, timeline.GapCount);
    }

    [Fact]
    public void QpcClock_OneMissingBuffer_IsStillReportedAsAGap()
    {
        // "Actual drops, stalls, and gaps should remain observable and should not be
        // hidden" (issue #33). One buffer of missing audio is one buffer of missing time.
        var timeline = new CaptureTimeline(Mono48k, CaptureClock.Qpc);

        timeline.Observe(ProcessLoopbackPacket(0));
        var timing = timeline.Observe(ProcessLoopbackPacket(2 * TenMs));

        Assert.Equal(10, timing.GapMs);
        Assert.Equal(10, timing.GapStartMs);
        Assert.Equal(20, timing.GapEndMs);
        Assert.Equal(20, timing.StartMs);
        Assert.Equal(1, timeline.GapCount);
    }

    [Fact]
    public void QpcClock_SubMillisecondEarlyArrival_IsNotARestartedStream()
    {
        // A QPC reading is a time, so a buffer that lands within a millisecond of the
        // expected continuation is the same stream read at millisecond resolution — not
        // the restarted stream a negative device position would mean.
        var timeline = new CaptureTimeline(Mono48k, CaptureClock.Qpc);

        timeline.Observe(ProcessLoopbackPacket(0));
        var timing = timeline.Observe(ProcessLoopbackPacket(Mono48k.MillisecondsToFrames(9)));

        Assert.False(timing.DevicePositionAnomaly);
        Assert.Equal(0, timing.GapMs);
        Assert.Equal(10, timing.StartMs);
        Assert.Equal(20, timing.EndMs);
        Assert.Equal(0, timeline.GapCount);
    }

    [Fact]
    public void QpcClock_AQpcThatMovesBackwards_IsFlaggedOnceAndKeepsTheTimelineMonotonic()
    {
        var timeline = new CaptureTimeline(Mono48k, CaptureClock.Qpc);

        // A restarted stream reports a position from its own new origin.
        timeline.Observe(ProcessLoopbackPacket(10 * TenMs));

        var timing = timeline.Observe(ProcessLoopbackPacket(0));

        Assert.True(timing.DevicePositionAnomaly);
        Assert.Equal(10, timing.StartMs);
        Assert.Equal(20, timing.EndMs);
        Assert.Equal(0, timing.GapMs);
        Assert.Equal(0, timeline.GapTotalMs);
        Assert.Equal(20, timeline.LastEndMs);
    }

    [Fact]
    public void QpcClock_RecordDeviceLoss_PlacesTheRestartedStreamAfterTheOutage()
    {
        var timeline = new CaptureTimeline(Mono48k, CaptureClock.Qpc);

        // The stream's own QPC starts far away from the session origin, so the restart can
        // only be placed by the measured outage, never by the new stream's position.
        timeline.Observe(ProcessLoopbackPacket(1_000, qpcDeltaTicks: 50_000_000));
        timeline.RecordDeviceLoss(5_000);

        var restarted = timeline.Observe(ProcessLoopbackPacket(0, qpcDeltaTicks: 900_000_000));

        Assert.True(restarted.IsNewSegment);
        Assert.Equal(5_000, restarted.GapMs);
        Assert.Equal(5_010, restarted.StartMs);
        Assert.Equal(5_020, restarted.EndMs);
        Assert.Equal(10, restarted.GapStartMs);
        Assert.Equal(5_010, restarted.GapEndMs);
        Assert.False(restarted.DevicePositionAnomaly);
        Assert.Equal(5_000, timeline.GapTotalMs);
        Assert.Equal(1, timeline.GapCount);

        // The new stream is re-anchored: the buffer after it continues contiguously, and the
        // rewritten origin is what puts the next buffer one buffer past the restart.
        var after = timeline.Observe(
            ProcessLoopbackPacket(TenMs, qpcDeltaTicks: 900_000_000));

        Assert.Equal(5_020, after.StartMs);
        Assert.Equal(5_030, after.EndMs);
        Assert.Equal(0, after.GapMs);
        Assert.False(after.DevicePositionAnomaly);

        // Audio stopped at 10 ms and resumes at 5010 ms even though the new stream's own
        // clock says something else entirely (docs/RELIABILITY.md section 7): the restart
        // buffer's own QPC reading now maps to where the outage put it.
        Assert.Equal(5_010, timeline.QpcToSessionMs(900_000_000));
        Assert.Equal(5_020, timeline.QpcToSessionMs(900_100_000));
    }

    [Fact]
    public void QpcClock_WithoutAQpcTimestamp_FailsWithAnActionableDiagnostic()
    {
        // Issue #33's fourth expectation: an unsupported process-loopback environment must
        // fail with an actionable diagnostic rather than silently producing degraded audio.
        // A track whose stream supplies no QPC cannot be placed by device timing at all, and
        // MeetCap refuses to invent one from a wall-clock read
        // (docs/ARCHITECTURE.md sections 8 and 8.1).
        var timeline = new CaptureTimeline(Mono48k, CaptureClock.Qpc);

        var exception = Assert.Throws<CaptureFailedException>(
            () => timeline.Observe(PacketWithoutAnyTiming()));

        Assert.Contains("QPC", exception.Message, StringComparison.Ordinal);
        Assert.Contains("loopback_mode = \"system\"", exception.Message, StringComparison.Ordinal);
        Assert.False(timeline.HasOrigin);

        // The same guard holds after the track has been placed for a while, so a stream
        // that stops supplying timing mid-session cannot slip through.
        var placed = new CaptureTimeline(Mono48k, CaptureClock.Qpc);
        placed.Observe(ProcessLoopbackPacket(0));

        Assert.Throws<CaptureFailedException>(
            () => placed.Observe(PacketWithoutAnyTiming()));
    }

    [Fact]
    public void QpcClock_DeviceFlags_AreStillSurfaced()
    {
        var timeline = new CaptureTimeline(Mono48k, CaptureClock.Qpc);

        var timing = timeline.Observe(
            ProcessLoopbackPacket(0, flags: AudioBufferFlags.DataDiscontinuity));

        Assert.True(timing.Discontinuity);
        Assert.True(timing.IsIrregular);
    }

    /// <summary>
    /// A process-loopback-shaped packet: the stream reports no device position of its own,
    /// so the device position is 0 on every buffer and only the QPC timestamp advances
    /// (issue #33, docs/ARCHITECTURE.md section 8.1).
    /// </summary>
    private static AudioPacket ProcessLoopbackPacket(
        long startFrame,
        int frames = TenMs,
        long qpcDeltaTicks = 0,
        AudioBufferFlags flags = AudioBufferFlags.None)
        => Packet(
            devicePosition: 0,
            frames: frames,
            // The QPC of the frame the stream starts at, shifted by an explicit delta when a
            // test needs the stream's own clock to sit somewhere else entirely.
            qpc: (startFrame * 10_000_000L / Mono48k.SampleRate) + qpcDeltaTicks,
            flags: flags);

    /// <summary>A packet that carries neither a device position nor a QPC timestamp.</summary>
    private static AudioPacket PacketWithoutAnyTiming(int frames = TenMs)
        => Packet(devicePosition: 0, frames: frames, qpc: null);

    private static AudioPacket Packet(
        long devicePosition,
        int frames,
        long? qpc = null,
        AudioBufferFlags flags = AudioBufferFlags.None)
        => new(
            AudioSource.Mic,
            Mono48k,
            new byte[frames * Mono48k.BlockAlign],
            devicePosition,
            qpc,
            DateTimeOffset.UnixEpoch,
            flags);
}
