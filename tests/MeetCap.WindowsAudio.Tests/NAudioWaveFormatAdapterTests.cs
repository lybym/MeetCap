using MeetCap.Core.Capture;
using MeetCap.Core.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Xunit;

namespace MeetCap.WindowsAudio.Tests;

/// <summary>
/// Coverage for the pure translation layer between NAudio wave/flag descriptors and
/// MeetCap-owned capture types. The rest of <c>MeetCap.WindowsAudio</c> is COM-bound and
/// needs real audio hardware (docs/M1_WINDOWS_VALIDATION.md), but this adapter decides
/// which endpoints are recordable at all and which buffer flags the pipeline sees, so it
/// is worth exercising without a device.
/// </summary>
public class NAudioWaveFormatAdapterTests
{
    private static readonly CaptureDeviceInfo Device = new("mic-1", "USB Microphone", true);

    /// <summary>
    /// A sub-format GUID that is neither PCM nor IEEE float, written out rather than
    /// taken from a NAudio constant so the test states the value it depends on.
    /// </summary>
    private static readonly Guid UnsupportedSubFormat =
        new("00000002-0000-0010-8000-00aa00389b71");

    private const int StereoMask = 3;

    [Fact]
    public void ToAudioFormat_MapsPlainPcm()
    {
        var format = NAudioWaveFormatAdapter.ToAudioFormat(
            new WaveFormat(48_000, 16, 2),
            Device);

        Assert.Equal(48_000, format.SampleRate);
        Assert.Equal(2, format.Channels);
        Assert.Equal(16, format.BitsPerSample);
        Assert.Equal(AudioSampleFormat.Pcm, format.SampleFormat);
        Assert.Equal(4, format.BlockAlign);
        Assert.Equal(192_000, format.AverageBytesPerSecond);
    }

    [Fact]
    public void ToAudioFormat_MapsPlainIeeeFloat()
    {
        var format = NAudioWaveFormatAdapter.ToAudioFormat(
            WaveFormat.CreateIeeeFloatWaveFormat(44_100, 2),
            Device);

        Assert.Equal(44_100, format.SampleRate);
        Assert.Equal(32, format.BitsPerSample);
        Assert.Equal(AudioSampleFormat.IeeeFloat, format.SampleFormat);
        Assert.Equal("ieee_float", format.SampleFormatName);
    }

    [Fact]
    public void ToAudioFormat_ReadsTheSubFormatGuidOfAnExtensiblePcmDevice()
    {
        // WAVE_FORMAT_EXTENSIBLE reports the real encoding only in the sub-format GUID,
        // so the base Encoding value cannot be trusted. USB headsets commonly use this.
        var extensible = new WaveFormatExtensible(
            48_000,
            16,
            2,
            AudioMediaSubtypes.MEDIASUBTYPE_PCM,
            16,
            StereoMask);
        Assert.Equal(WaveFormatEncoding.Extensible, extensible.Encoding);

        var format = NAudioWaveFormatAdapter.ToAudioFormat(extensible, Device);

        Assert.Equal(AudioSampleFormat.Pcm, format.SampleFormat);
        Assert.Equal(48_000, format.SampleRate);
        Assert.Equal(2, format.Channels);
    }

    [Fact]
    public void ToAudioFormat_ReadsTheSubFormatGuidOfAnExtensibleFloatDevice()
    {
        var extensible = new WaveFormatExtensible(
            48_000,
            32,
            2,
            AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT,
            32,
            StereoMask);

        var format = NAudioWaveFormatAdapter.ToAudioFormat(extensible, Device);

        Assert.Equal(AudioSampleFormat.IeeeFloat, format.SampleFormat);
        Assert.Equal(32, format.BitsPerSample);
    }

    [Fact]
    public void ToAudioFormat_RejectsAnUnsupportedExtensibleSubFormat()
    {
        var extensible = new WaveFormatExtensible(48_000, 16, 2, UnsupportedSubFormat, 16, StereoMask);

        var error = Assert.Throws<DeviceUnavailableException>(
            () => NAudioWaveFormatAdapter.ToAudioFormat(extensible, Device));

        // The failure has to name the device and the reason: this is the path a user hits
        // when their endpoint uses a format MeetCap refuses to record.
        Assert.Contains("USB Microphone", error.Message, StringComparison.Ordinal);
        Assert.Contains("unsupported extensible sub-format", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToAudioFormat_RejectsANonPcmNonFloatEncoding()
    {
        var compressed = WaveFormat.CreateCustomFormat(WaveFormatEncoding.MpegLayer3, 48_000, 1, 12_000, 1, 0);

        var error = Assert.Throws<DeviceUnavailableException>(
            () => NAudioWaveFormatAdapter.ToAudioFormat(compressed, Device));

        Assert.Contains("unsupported encoding", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToAudioFormat_ReportsAnUnusableFormatAsADeviceProblem()
    {
        // NAudio accepts this descriptor, but MeetCap cannot represent it (12 bits per
        // sample is not a shape the artifact contract records), so the device must be
        // reported as unusable with the device named rather than surfacing a raw
        // ArgumentOutOfRangeException.
        var unusable = WaveFormat.CreateCustomFormat(WaveFormatEncoding.Pcm, 48_000, 1, 72_000, 2, 12);

        var error = Assert.Throws<DeviceUnavailableException>(
            () => NAudioWaveFormatAdapter.ToAudioFormat(unusable, Device));

        Assert.Contains("unusable format", error.Message, StringComparison.Ordinal);
        Assert.Contains("USB Microphone", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ToBufferFlags_MapsEveryFlagIndividually()
    {
        Assert.Equal(AudioBufferFlags.None, NAudioWaveFormatAdapter.ToBufferFlags(AudioClientBufferFlags.None));
        Assert.Equal(
            AudioBufferFlags.DataDiscontinuity,
            NAudioWaveFormatAdapter.ToBufferFlags(AudioClientBufferFlags.DataDiscontinuity));
        Assert.Equal(AudioBufferFlags.Silent, NAudioWaveFormatAdapter.ToBufferFlags(AudioClientBufferFlags.Silent));
        Assert.Equal(
            AudioBufferFlags.TimestampError,
            NAudioWaveFormatAdapter.ToBufferFlags(AudioClientBufferFlags.TimestampError));
    }

    [Fact]
    public void ToBufferFlags_PreservesEveryBitOfACombinedValue()
    {
        // Dropping a flag here would silently disable discontinuity or timestamp
        // detection while every downstream consumer test stayed green.
        var combined = NAudioWaveFormatAdapter.ToBufferFlags(
            AudioClientBufferFlags.DataDiscontinuity |
            AudioClientBufferFlags.Silent |
            AudioClientBufferFlags.TimestampError);

        Assert.True(combined.HasFlag(AudioBufferFlags.DataDiscontinuity));
        Assert.True(combined.HasFlag(AudioBufferFlags.Silent));
        Assert.True(combined.HasFlag(AudioBufferFlags.TimestampError));
    }
}
