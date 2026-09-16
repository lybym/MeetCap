using MeetCap.Core.Capture;
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
