using MeetCap.Core.Capture;
using Xunit;

namespace MeetCap.Core.Tests.Capture;

public class AudioFormatTests
{
    [Fact]
    public void BlockAlign_And_AverageBytesPerSecond_FollowTheFormat()
    {
        var format = new AudioFormat(48_000, 2, 32, AudioSampleFormat.IeeeFloat);

        Assert.Equal(8, format.BlockAlign);
        Assert.Equal(384_000, format.AverageBytesPerSecond);
        Assert.Equal(AudioSampleFormatNames.IeeeFloat, format.SampleFormatName);
    }

    [Theory]
    [InlineData(48_000, 96_000)]
    [InlineData(44_100, 88_200)]
    public void FramesToMilliseconds_IsExactForTheStandardRates(int sampleRate, long frames)
    {
        var format = new AudioFormat(sampleRate, 1, 16, AudioSampleFormat.Pcm);

        Assert.Equal(2_000, format.FramesToMilliseconds(frames));
        Assert.Equal(frames, format.MillisecondsToFrames(2_000));
    }

    [Fact]
    public void FramesToBytes_And_BytesToFrames_RoundTrip()
    {
        var format = new AudioFormat(16_000, 1, 16, AudioSampleFormat.Pcm);

        Assert.Equal(32_000, format.FramesToBytes(16_000));
        Assert.Equal(16_000, format.BytesToFrames(32_000));
    }

    [Theory]
    [InlineData(0, 2, 16)]
    [InlineData(48_000, 0, 16)]
    [InlineData(48_000, 2, 12)]
    public void Constructor_RejectsUnusableFormats(int sampleRate, int channels, int bitsPerSample)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AudioFormat(sampleRate, channels, bitsPerSample, AudioSampleFormat.Pcm));
    }

    [Fact]
    public void Constructor_RejectsFloatFormatsThatAreNot32Or64Bit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AudioFormat(48_000, 1, 16, AudioSampleFormat.IeeeFloat));
    }

    [Fact]
    public void Records_WithTheSameFormat_AreEqual()
    {
        Assert.Equal(
            new AudioFormat(48_000, 2, 32, AudioSampleFormat.IeeeFloat),
            new AudioFormat(48_000, 2, 32, AudioSampleFormat.IeeeFloat));
    }
}
