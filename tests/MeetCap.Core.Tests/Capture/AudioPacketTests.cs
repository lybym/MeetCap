using MeetCap.Core.Capture;
using Xunit;

namespace MeetCap.Core.Tests.Capture;

public class AudioPacketTests
{
    private static readonly AudioFormat Stereo = new(48_000, 2, 16, AudioSampleFormat.Pcm);

    [Fact]
    public void Constructor_RejectsPayloadsThatAreNotFrameAligned()
    {
        Assert.Throws<ArgumentException>(
            () => new AudioPacket(AudioSource.Mic, Stereo, new byte[3], 0, null, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void FrameCount_And_Duration_FollowTheFormat()
    {
        // 480 stereo frames at 48 kHz is 10 ms and 1920 bytes.
        var packet = new AudioPacket(
            AudioSource.Mic,
            Stereo,
            new byte[480 * Stereo.BlockAlign],
            1_000,
            12_345,
            DateTimeOffset.UnixEpoch);

        Assert.Equal(480, packet.FrameCount);
        Assert.Equal(10, packet.DurationMs);
        Assert.Equal(1_000, packet.DevicePositionFrames);
        Assert.Equal(12_345, packet.QpcPositionTicks);
        Assert.True(packet.HasData);
    }

    [Fact]
    public void IsDiscontinuous_IsTrueForEitherDiscontinuityOrBadTimestamp()
    {
        var discontinuity = Packet(AudioBufferFlags.DataDiscontinuity);
        var badTimestamp = Packet(AudioBufferFlags.TimestampError);
        var silent = Packet(AudioBufferFlags.Silent);

        Assert.True(discontinuity.IsDiscontinuous);
        Assert.True(badTimestamp.IsDiscontinuous);
        Assert.False(silent.IsDiscontinuous);
    }

    [Fact]
    public void WireNames_RoundTripForEveryAudioSource()
    {
        foreach (var source in new[] { AudioSource.Mic, AudioSource.Loopback })
        {
            Assert.True(AudioSources.TryParse(source.ToWireName(), out var parsed));
            Assert.Equal(source, parsed);
        }

        Assert.False(AudioSources.TryParse("speaker", out _));
    }

    private static AudioPacket Packet(AudioBufferFlags flags)
        => new(AudioSource.Mic, Stereo, new byte[Stereo.BlockAlign], 0, null, DateTimeOffset.UnixEpoch, flags);
}
