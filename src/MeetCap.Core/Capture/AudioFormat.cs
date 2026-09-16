namespace MeetCap.Core.Capture;

/// <summary>Sample representation of a captured audio track.</summary>
public enum AudioSampleFormat
{
    /// <summary>Linear PCM integers.</summary>
    Pcm,

    /// <summary>IEEE floating point samples (WASAPI shared-mode mix format).</summary>
    IeeeFloat,
}

/// <summary>
/// Wire/artifact names for <see cref="AudioSampleFormat"/>, stored in
/// <c>audio_chunks.sample_format</c> (docs/DATA_MODEL.md section 5).
/// </summary>
public static class AudioSampleFormatNames
{
    public const string Pcm = "pcm";
    public const string IeeeFloat = "ieee_float";

    public static string ToWireName(this AudioSampleFormat format) => format switch
    {
        AudioSampleFormat.Pcm => Pcm,
        AudioSampleFormat.IeeeFloat => IeeeFloat,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown sample format."),
    };

    public static bool TryParse(string? value, out AudioSampleFormat format)
    {
        switch (value)
        {
            case Pcm:
                format = AudioSampleFormat.Pcm;
                return true;
            case IeeeFloat:
                format = AudioSampleFormat.IeeeFloat;
                return true;
            default:
                format = default;
                return false;
        }
    }
}

/// <summary>
/// The native format of a captured track. MeetCap records the device's own format
/// rather than converting on the capture path: docs/ARCHITECTURE.md section 7
/// forbids DSP work in the capture callback, and docs/DEVELOPMENT.md section 5
/// requires the raw source to be preserved.
/// </summary>
public sealed record AudioFormat
{
    public AudioFormat(int sampleRate, int channels, int bitsPerSample, AudioSampleFormat sampleFormat)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be positive.");
        }

        if (channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channel count must be positive.");
        }

        if (bitsPerSample is not (8 or 16 or 24 or 32 or 64))
        {
            throw new ArgumentOutOfRangeException(
                nameof(bitsPerSample),
                bitsPerSample,
                "Bits per sample must be 8, 16, 24, 32 or 64.");
        }

        if (sampleFormat == AudioSampleFormat.IeeeFloat && bitsPerSample is not (32 or 64))
        {
            throw new ArgumentOutOfRangeException(
                nameof(bitsPerSample),
                bitsPerSample,
                "IEEE float samples must be 32 or 64 bits.");
        }

        SampleRate = sampleRate;
        Channels = channels;
        BitsPerSample = bitsPerSample;
        SampleFormat = sampleFormat;
    }

    public int SampleRate { get; }

    public int Channels { get; }

    public int BitsPerSample { get; }

    public AudioSampleFormat SampleFormat { get; }

    /// <summary>Bytes per sample frame across all channels.</summary>
    public int BlockAlign => Channels * (BitsPerSample / 8);

    /// <summary>Bytes per second of captured audio.</summary>
    public int AverageBytesPerSecond => SampleRate * BlockAlign;

    /// <summary>Artifact name of the sample format.</summary>
    public string SampleFormatName => SampleFormat.ToWireName();

    /// <summary>
    /// Converts a frame count to session milliseconds. Integer division is exact for
    /// the standard 44.1/48 kHz rates and keeps chunk boundaries frame-accurate.
    /// </summary>
    public long FramesToMilliseconds(long frames) => frames * 1000 / SampleRate;

    public long MillisecondsToFrames(long milliseconds) => milliseconds * SampleRate / 1000;

    public long FramesToBytes(long frames) => frames * BlockAlign;

    public long BytesToFrames(long bytes) => bytes / BlockAlign;

    public override string ToString()
        => $"{SampleRate} Hz, {Channels} ch, {BitsPerSample}-bit {SampleFormatName}";
}
