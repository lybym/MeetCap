namespace MeetCap.Core.Capture;

/// <summary>
/// What a captured track's samples actually contained, in the track's own format.
/// </summary>
/// <remarks>
/// <para>
/// MeetCap reports every capture from its buffers, so a stream that runs perfectly while
/// delivering nothing but digital zeros — issue #38's process-loopback failure — used to end
/// as a <c>healthy</c>, <c>COMPLETED</c> session with an empty track and no hint that anything
/// was wrong. These counters are the evidence that was missing: the peak absolute sample and
/// how many samples were non-zero, recorded per track in <c>session.json</c> and used to flag
/// a process-loopback track that never captured a single non-zero sample
/// (docs/RELIABILITY.md section 17).
/// </para>
/// <para>
/// <see cref="PeakAbsSample"/> is normalized so it is comparable across formats: PCM values
/// are divided by their full-scale magnitude and IEEE floats are already normalized, so a
/// full-scale tone reads near 1.0 whatever the endpoint's native format is
/// (docs/DEVELOPMENT.md section 5: the raw source is counted, never converted).
/// </para>
/// </remarks>
/// <param name="PeakAbsSample">Largest absolute sample value seen on the track, normalized to full scale.</param>
/// <param name="NonZeroSamples">Samples whose value was not zero (a negative float zero counts as zero).</param>
/// <param name="TotalSamples">Samples examined on the track, across all channels.</param>
public sealed record AudioContentStats(double PeakAbsSample, long NonZeroSamples, long TotalSamples)
{
    /// <summary>No audio was examined at all.</summary>
    public static readonly AudioContentStats Empty = new(0, 0, 0);

    /// <summary>
    /// True when the track carried samples and every one of them was digital zero: the shape
    /// of a silent capture, which is legitimate for an idle target but is also exactly how a
    /// capture backend that returns only zeros presents itself (issue #38).
    /// </summary>
    public bool AllSilent => TotalSamples > 0 && NonZeroSamples == 0;
}

/// <summary>
/// Accumulates <see cref="AudioContentStats"/> from the packets a track consumes.
/// </summary>
/// <remarks>
/// <para>
/// The scan runs on the consumer thread, on the same bytes the track writes to its chunk, so
/// the verdict is about the audio that was recorded rather than about a parallel copy of it.
/// It deliberately does not run in the capture callback: docs/ARCHITECTURE.md section 7 keeps
/// the audio thread free of work a slow consumer could turn into dropped audio, and this scan
/// is O(payload) per packet exactly like the copy already made when the callback took the
/// buffer.
/// </para>
/// <para>
/// Nothing here converts or resamples: samples are read at their native width for counting and
/// for the peak, so the statistics describe the preserved source
/// (docs/DEVELOPMENT.md section 5).
/// </para>
/// </remarks>
public sealed class AudioContentAccumulator
{
    private double _peakAbsSample;
    private long _nonZeroSamples;
    private long _totalSamples;

    /// <summary>Counts one packet's payload in its own format. Call on the consumer thread.</summary>
    public void Add(ReadOnlySpan<byte> payload, AudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        if (payload.IsEmpty)
        {
            return;
        }

        var blockAlign = format.BlockAlign;
        var usable = payload.Length - (payload.Length % blockAlign);
        if (usable <= 0)
        {
            return;
        }

        var samples = payload[..usable];

        if (format.SampleFormat == AudioSampleFormat.IeeeFloat)
        {
            if (format.BitsPerSample == 32)
            {
                AccumulateFloat32(samples);
            }
            else
            {
                AccumulateFloat64(samples);
            }

            return;
        }

        switch (format.BitsPerSample)
        {
            case 8:
                AccumulatePcm8(samples);
                break;
            case 16:
                AccumulatePcm16(samples);
                break;
            case 24:
                AccumulatePcm24(samples);
                break;
            case 32:
                AccumulatePcm32(samples);
                break;
            default:
                AccumulatePcm64(samples);
                break;
        }
    }

    /// <summary>The statistics for everything counted so far.</summary>
    public AudioContentStats Snapshot() => new(_peakAbsSample, _nonZeroSamples, _totalSamples);

    private void Count(double normalizedMagnitude, bool nonZero)
    {
        _totalSamples++;

        if (nonZero)
        {
            _nonZeroSamples++;
        }

        if (normalizedMagnitude > _peakAbsSample)
        {
            _peakAbsSample = normalizedMagnitude;
        }
    }

    private void AccumulateFloat32(ReadOnlySpan<byte> samples)
    {
        for (var offset = 0; offset + 4 <= samples.Length; offset += 4)
        {
            var value = BitConverter.ToSingle(samples[offset..]);
            if (float.IsNaN(value))
            {
                // Not audio: a NaN sample is unplaceable, so it is neither content nor a peak.
                _totalSamples++;
                continue;
            }

            // -0.0f == 0f, so a negative float zero is counted as silence, which is what a
            // reader of the WAV would hear.
            Count(Math.Abs((double)value), value != 0f);
        }
    }

    private void AccumulateFloat64(ReadOnlySpan<byte> samples)
    {
        for (var offset = 0; offset + 8 <= samples.Length; offset += 8)
        {
            var value = BitConverter.ToDouble(samples[offset..]);
            if (double.IsNaN(value))
            {
                _totalSamples++;
                continue;
            }

            Count(Math.Abs(value), value != 0d);
        }
    }

    private void AccumulatePcm8(ReadOnlySpan<byte> samples)
    {
        // 8-bit PCM is unsigned with 128 as the zero point.
        foreach (var value in samples)
        {
            Count(Math.Abs(value - 128) / 128d, value != 128);
        }
    }

    private void AccumulatePcm16(ReadOnlySpan<byte> samples)
    {
        for (var offset = 0; offset + 2 <= samples.Length; offset += 2)
        {
            var value = BitConverter.ToInt16(samples[offset..]);
            Count(Math.Abs((double)value) / 32768d, value != 0);
        }
    }

    private void AccumulatePcm24(ReadOnlySpan<byte> samples)
    {
        for (var offset = 0; offset + 3 <= samples.Length; offset += 3)
        {
            var raw = samples[offset] | (samples[offset + 1] << 8) | (samples[offset + 2] << 16);
            // Sign-extend the 24-bit value into an int.
            var value = (raw & 0x800000) != 0 ? raw | unchecked((int)0xFF000000) : raw;
            Count(Math.Abs((double)value) / 8388608d, value != 0);
        }
    }

    private void AccumulatePcm32(ReadOnlySpan<byte> samples)
    {
        for (var offset = 0; offset + 4 <= samples.Length; offset += 4)
        {
            var value = BitConverter.ToInt32(samples[offset..]);
            Count(Math.Abs((double)value) / 2147483648d, value != 0);
        }
    }

    private void AccumulatePcm64(ReadOnlySpan<byte> samples)
    {
        for (var offset = 0; offset + 8 <= samples.Length; offset += 8)
        {
            var value = BitConverter.ToInt64(samples[offset..]);
            Count(Math.Abs((double)value) / 9223372036854775808d, value != 0);
        }
    }
}