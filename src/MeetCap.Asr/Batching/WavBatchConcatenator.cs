namespace MeetCap.Asr.Batching;

using MeetCap.AudioPipeline;
using MeetCap.AudioPipeline.Wave;
using MeetCap.Core.Capture;
using MeetCap.Core.Sessions;

/// <summary>
/// Materializes one ASR batch file from an ordered list of durable capture chunks.
/// </summary>
/// <remarks>
/// <para>
/// A batch is a real audio artifact, not a list of paths: the provider accepts one file
/// per request, and a live batch is the natural unit of provider context
/// (<c>docs/ARCHITECTURE.md</c> section 10, <c>docs/ASR_STRATEGY.md</c> section 4).
/// </para>
/// <para>
/// The chunks of one track all carry the session's single native format, so concatenating
/// them is a header-and-payload operation, not a re-encode: no resampler, no codec, and
/// nothing that can change the samples. The output is written as a WAV whose data length
/// is patched and validated by the same <see cref="WaveChunkWriter"/> the capture spool
/// uses, so a batch file is exactly as independently readable as a capture chunk.
/// </para>
/// <para>
/// If the process dies mid-write, the batch is left as a <c>.part</c> file and no job
/// references it: the ASR job is only created after the rename, so a crash cannot leave a
/// queued job pointing at audio that is not durable
/// (<c>docs/RELIABILITY.md</c> sections 5 and 6).
/// </para>
/// </remarks>
public static class WavBatchConcatenator
{
    public const string PartSuffix = ".part";

    /// <summary>
    /// Concatenates <paramref name="sources"/> into <paramref name="destinationPath"/>,
    /// writing through <c>&lt;destinationPath&gt;.part</c> and renaming only after the
    /// header has been validated.
    /// </summary>
    /// <param name="destinationPath">Durable batch path, e.g. <c>asr/batches/mic/batch-000001.wav</c>.</param>
    /// <param name="sources">Durable chunk files, in session-timeline order.</param>
    /// <param name="format">The track format every source file was written with.</param>
    /// <returns>The data bytes in the batch, excluding the WAV header.</returns>
    /// <exception cref="InvalidOperationException">A source is missing or is not readable audio.</exception>
    public static long Concatenate(string destinationPath, IReadOnlyList<ClosedAudioChunk> sources, AudioFormat format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(format);

        if (sources.Count == 0)
        {
            throw new ArgumentException("A batch needs at least one source chunk.", nameof(sources));
        }

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var partPath = destinationPath + PartSuffix;

        // One writer for the whole batch: the capacity is only an upper bound it never
        // reaches, because a batch is closed at a fixed number of chunks rather than at a
        // byte boundary.
        long declaredCapacity = 0;
        foreach (var source in sources)
        {
            declaredCapacity += Math.Max(source.DataBytes, 1);
        }

        declaredCapacity = RoundUpToFrame(declaredCapacity, format);

        var writer = new WaveChunkWriter(partPath, destinationPath, format, declaredCapacity, sequence: 1);
        try
        {
            foreach (var source in sources)
            {
                AppendSource(writer, source);
            }

            var result = writer.Close(DateTimeOffset.UtcNow);
            return result.DataBytes;
        }
        catch
        {
            // The .part file stays on disk and is discarded by batch recovery; the durable
            // path is never created, so nothing can treat a half-written batch as audio.
            writer.Dispose();
            throw;
        }
    }

    private static void AppendSource(WaveChunkWriter writer, ClosedAudioChunk source)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(source.FilePath);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Capture chunk '{source.FilePath}' could not be inspected for a batch: {ex.Message}", ex);
        }

        if (!info.Exists)
        {
            throw new InvalidOperationException(
                $"Capture chunk '{source.FilePath}' is missing, so it cannot be included in a batch.");
        }

        if (info.Length < WavHeader.Size)
        {
            throw new InvalidOperationException(
                $"Capture chunk '{source.FilePath}' is {info.Length} bytes, shorter than a WAV header.");
        }

        using var stream = new FileStream(
            source.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);

        var header = new byte[WavHeader.Size];
        stream.ReadExactly(header);

        var parsed = WavHeader.Parse(header);
        if (!parsed.IsValid || parsed.Format is null)
        {
            throw new InvalidOperationException(
                $"Capture chunk '{source.FilePath}' is not a readable WAV file: {parsed.Error}");
        }

        if (parsed.Format.SampleRate != writer.Format.SampleRate ||
            parsed.Format.Channels != writer.Format.Channels ||
            parsed.Format.BitsPerSample != writer.Format.BitsPerSample ||
            parsed.Format.SampleFormat != writer.Format.SampleFormat)
        {
            throw new InvalidOperationException(
                $"Capture chunk '{source.FilePath}' is '{parsed.Format}' but the batch is being written as " +
                $"'{writer.Format}'. Mixing formats in one batch would change how the audio is decoded.");
        }

        var buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (writer.Append(buffer.AsSpan(0, read)) != read)
            {
                throw new InvalidOperationException(
                    $"Batch capacity was exhausted while appending '{source.FilePath}'. " +
                    "The batch window held more audio than its own chunks declared.");
            }
        }
    }

    private static long RoundUpToFrame(long bytes, AudioFormat format)
    {
        var blockAlign = format.BlockAlign;
        var remainder = bytes % blockAlign;
        return remainder == 0 ? bytes : bytes + (blockAlign - remainder);
    }
}
