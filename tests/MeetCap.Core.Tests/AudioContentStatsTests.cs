using MeetCap.Core.Capture;
using Xunit;

namespace MeetCap.Core.Tests;

/// <summary>
/// Issue #38's evidence counters: what a track's samples contained, and whether every one of
/// them was digital zero.
/// </summary>
/// <remarks>
/// These are the numbers the session document records and the silent-process-loopback verdict
/// is decided from, so each sample width is pinned explicitly: a wrong offset or a wrong zero
/// point would silently turn real audio into "silence" and hide exactly the failure this
/// exists to expose.
/// </remarks>
public class AudioContentStatsTests
{
    private static AudioFormat Format(int bits, AudioSampleFormat sampleFormat, int channels = 1)
        => new(48_000, channels, bits, sampleFormat);

    private static AudioContentStats Count(AudioFormat format, byte[] payload)
    {
        var accumulator = new AudioContentAccumulator();
        accumulator.Add(payload, format);
        return accumulator.Snapshot();
    }

    private static byte[] Float32(params float[] samples)
    {
        var payload = new byte[samples.Length * 4];
        for (var i = 0; i < samples.Length; i++)
        {
            BitConverter.TryWriteBytes(payload.AsSpan(i * 4), samples[i]);
        }

        return payload;
    }

    private static byte[] Pcm16(params short[] samples)
    {
        var payload = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            BitConverter.TryWriteBytes(payload.AsSpan(i * 2), samples[i]);
        }

        return payload;
    }

    private static byte[] Pcm24(params int[] samples)
    {
        var payload = new byte[samples.Length * 3];
        for (var i = 0; i < samples.Length; i++)
        {
            var value = samples[i];
            payload[i * 3] = (byte)(value & 0xFF);
            payload[(i * 3) + 1] = (byte)((value >> 8) & 0xFF);
            payload[(i * 3) + 2] = (byte)((value >> 16) & 0xFF);
        }

        return payload;
    }

    [Fact]
    public void TrackOfZeros_IsAllSilent()
    {
        var format = Format(32, AudioSampleFormat.IeeeFloat);
        var stats = Count(format, new byte[format.BlockAlign * 480]);

        Assert.True(stats.AllSilent);
        Assert.Equal(480, stats.TotalSamples);
        Assert.Equal(0, stats.NonZeroSamples);
        Assert.Equal(0d, stats.PeakAbsSample);
    }

    [Fact]
    public void Audio_CountsEveryNonZeroSampleAndTheLargestMagnitude()
    {
        var format = Format(32, AudioSampleFormat.IeeeFloat);
        var stats = Count(format, Float32(0f, 0.5f, -0.25f, 0.125f));

        Assert.False(stats.AllSilent);
        Assert.Equal(4, stats.TotalSamples);
        Assert.Equal(3, stats.NonZeroSamples);
        Assert.Equal(0.5d, stats.PeakAbsSample, precision: 6);
    }

    [Fact]
    public void NegativeFloatZero_IsSilence()
    {
        // -0.0f == 0f, so a reader of the WAV would hear nothing: the verdict must agree with
        // the audio, not with the bits.
        var format = Format(32, AudioSampleFormat.IeeeFloat);
        var payload = Float32(-0.0f, -0.0f);
        var stats = Count(format, payload);

        Assert.True(stats.AllSilent);
        Assert.Equal(0, stats.NonZeroSamples);
    }

    [Fact]
    public void NaN_IsNeitherContentNorAPeak()
    {
        var format = Format(32, AudioSampleFormat.IeeeFloat);
        var stats = Count(format, Float32(float.NaN));

        Assert.True(stats.AllSilent);
        Assert.Equal(1, stats.TotalSamples);
        Assert.Equal(0d, stats.PeakAbsSample);
    }

    [Fact]
    public void Pcm16_PeakIsNormalizedToFullScale()
    {
        var stats = Count(Format(16, AudioSampleFormat.Pcm), Pcm16(8192, -16384, 0));

        Assert.False(stats.AllSilent);
        Assert.Equal(3, stats.TotalSamples);
        Assert.Equal(2, stats.NonZeroSamples);
        Assert.Equal(0.5d, stats.PeakAbsSample, precision: 6);
    }

    [Fact]
    public void Pcm24_NegativeSampleIsSignExtendedAndCounted()
    {
        var format = Format(24, AudioSampleFormat.Pcm);
        var stats = Count(format, Pcm24(-8388608, 0));

        Assert.False(stats.AllSilent);
        Assert.Equal(2, stats.TotalSamples);
        Assert.Equal(1, stats.NonZeroSamples);
        Assert.Equal(1d, stats.PeakAbsSample, precision: 6);
    }

    [Fact]
    public void Pcm8_UsesOneTwentyEightAsTheZeroPoint()
    {
        var format = Format(8, AudioSampleFormat.Pcm);
        var stats = Count(format, new byte[] { 128, 160, 96 });

        Assert.Equal(3, stats.TotalSamples);
        Assert.Equal(2, stats.NonZeroSamples);
        Assert.Equal(0.25d, stats.PeakAbsSample, precision: 6);
    }

    [Fact]
    public void Pcm32AndPcm64_AreCountedAtTheirOwnWidths()
    {
        var pcm32 = Format(32, AudioSampleFormat.Pcm);
        var payload32 = new byte[8];
        BitConverter.TryWriteBytes(payload32.AsSpan(0), 1073741824); // half of full scale
        BitConverter.TryWriteBytes(payload32.AsSpan(4), 0);
        var stats32 = Count(pcm32, payload32);

        Assert.Equal(1, stats32.NonZeroSamples);
        Assert.Equal(0.5d, stats32.PeakAbsSample, precision: 6);

        var pcm64 = Format(64, AudioSampleFormat.Pcm);
        var payload64 = new byte[16];
        BitConverter.TryWriteBytes(payload64.AsSpan(0), 4611686018427387904L); // half of full scale
        BitConverter.TryWriteBytes(payload64.AsSpan(8), 0L);
        var stats64 = Count(pcm64, payload64);

        Assert.Equal(1, stats64.NonZeroSamples);
        Assert.Equal(0.5d, stats64.PeakAbsSample, precision: 6);
    }

    [Fact]
    public void Float64_AudioIsCounted()
    {
        var format = Format(64, AudioSampleFormat.IeeeFloat);
        var payload = new byte[16];
        BitConverter.TryWriteBytes(payload.AsSpan(0), -0.75d);
        BitConverter.TryWriteBytes(payload.AsSpan(8), 0.0d);
        var stats = Count(format, payload);

        Assert.Equal(2, stats.TotalSamples);
        Assert.Equal(1, stats.NonZeroSamples);
        Assert.Equal(0.75d, stats.PeakAbsSample, precision: 6);
    }

    [Fact]
    public void EmptyPayload_RecordsNothingAndIsNotSilent()
    {
        // "No samples at all" and "samples that were all zero" are different statements: only
        // the second is a silent track, so an empty track must not claim to be one.
        var stats = Count(Format(16, AudioSampleFormat.Pcm), Array.Empty<byte>());

        Assert.Equal(AudioContentStats.Empty, stats);
        Assert.False(stats.AllSilent);
        Assert.Equal(0, stats.TotalSamples);
    }

    [Fact]
    public void UnalignedPayload_CountsOnlyWholeFrames()
    {
        // A stray trailing byte cannot be a sample; counting it would invent audio.
        var stats = Count(Format(16, AudioSampleFormat.Pcm, channels: 2), new byte[] { 0x00, 0x20, 0x00, 0x20, 0x7F });

        Assert.Equal(2, stats.TotalSamples);
        Assert.Equal(2, stats.NonZeroSamples);
    }

    [Fact]
    public void Snapshot_IsIndependentOfLaterAudio()
    {
        var accumulator = new AudioContentAccumulator();
        var format = Format(16, AudioSampleFormat.Pcm);
        accumulator.Add(Pcm16(0, 0), format);
        var silent = accumulator.Snapshot();

        accumulator.Add(Pcm16(8192), format);
        var withAudio = accumulator.Snapshot();

        Assert.True(silent.AllSilent);
        Assert.False(withAudio.AllSilent);
        Assert.Equal(2, silent.TotalSamples);
        Assert.Equal(3, withAudio.TotalSamples);
        Assert.Equal(1, withAudio.NonZeroSamples);
    }
}