namespace MeetCap.WindowsAudio;

using MeetCap.Core.Capture;
using MeetCap.Core.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

/// <summary>
/// Translates NAudio wave/buffer descriptions into MeetCap's own capture types and
/// rejects anything MeetCap cannot faithfully record.
/// </summary>
/// <remarks>
/// Keeping the translation in one place is what lets the rest of the application treat
/// NAudio as an implementation detail (docs/DEVELOPMENT.md section 4).
/// </remarks>
internal static class NAudioWaveFormatAdapter
{
    public static AudioFormat ToAudioFormat(WaveFormat waveFormat, CaptureDeviceInfo device)
    {
        var sampleFormat = ResolveSampleFormat(waveFormat, device);

        try
        {
            return new AudioFormat(
                waveFormat.SampleRate,
                waveFormat.Channels,
                waveFormat.BitsPerSample,
                sampleFormat);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new DeviceUnavailableException(
                $"Capture device '{device.DisplayName}' reports an unusable format " +
                $"({waveFormat.SampleRate} Hz, {waveFormat.Channels} ch, {waveFormat.BitsPerSample}-bit, " +
                $"{waveFormat.Encoding}): {ex.Message}",
                ex);
        }
    }

    private static AudioSampleFormat ResolveSampleFormat(WaveFormat waveFormat, CaptureDeviceInfo device)
    {
        switch (waveFormat.Encoding)
        {
            case WaveFormatEncoding.Pcm:
                return AudioSampleFormat.Pcm;

            case WaveFormatEncoding.IeeeFloat:
                return AudioSampleFormat.IeeeFloat;

            case WaveFormatEncoding.Extensible when waveFormat is WaveFormatExtensible extensible:
                // A WAVE_FORMAT_EXTENSIBLE device reports the real encoding only in its
                // sub-format GUID, so the base Encoding value cannot be trusted here.
                if (extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT)
                {
                    return AudioSampleFormat.IeeeFloat;
                }

                if (extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_PCM)
                {
                    return AudioSampleFormat.Pcm;
                }

                throw new DeviceUnavailableException(
                    $"Capture device '{device.DisplayName}' uses the unsupported extensible sub-format " +
                    $"'{extensible.SubFormat}'. MeetCap records PCM or IEEE float and never converts on the " +
                    "capture path.");

            default:
                throw new DeviceUnavailableException(
                    $"Capture device '{device.DisplayName}' uses the unsupported encoding " +
                    $"'{waveFormat.Encoding}'. MeetCap records PCM or IEEE float.");
        }
    }

    public static AudioBufferFlags ToBufferFlags(AudioClientBufferFlags flags)
    {
        var result = AudioBufferFlags.None;

        if (flags.HasFlag(AudioClientBufferFlags.DataDiscontinuity))
        {
            result |= AudioBufferFlags.DataDiscontinuity;
        }

        if (flags.HasFlag(AudioClientBufferFlags.Silent))
        {
            result |= AudioBufferFlags.Silent;
        }

        if (flags.HasFlag(AudioClientBufferFlags.TimestampError))
        {
            result |= AudioBufferFlags.TimestampError;
        }

        return result;
    }
}
