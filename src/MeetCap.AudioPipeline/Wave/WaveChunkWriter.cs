namespace MeetCap.AudioPipeline.Wave;

using MeetCap.Core.Capture;
using MeetCap.Core.Diagnostics;

/// <summary>
/// Writes one recoverable capture chunk using the <c>.part</c> lifecycle from
/// docs/ARCHITECTURE.md section 9 and docs/RELIABILITY.md section 5.
/// </summary>
/// <remarks>
/// <para>
/// The file is created with a complete, valid header describing zero data, then PCM
/// is appended. Because the header is complete from the first byte, a process kill
/// at any point leaves a file whose data length can be recovered from its size alone
/// — which is what makes the startup repair path safe.
/// </para>
/// <para>
/// A chunk is only renamed to its durable name after the header has been patched,
/// flushed to disk and re-validated. Previously closed chunks are never touched
/// again, so a later crash cannot corrupt them.
/// </para>
/// </remarks>
public sealed class WaveChunkWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly string _finalPath;
    private readonly long _capacityBytes;
    private bool _closed;

    /// <param name="partPath">The in-progress path, <c>audio/&lt;source&gt;/000001.wav.part</c>.</param>
    /// <param name="finalPath">The durable path, <c>audio/&lt;source&gt;/000001.wav</c>.</param>
    /// <param name="format">The device's native format for this track.</param>
    /// <param name="capacityBytes">
    /// Frame-aligned data capacity, typically <c>chunk_seconds * sample_rate * block_align</c>.
    /// </param>
    /// <param name="sequence">1-based chunk number within the track.</param>
    public WaveChunkWriter(
        string partPath,
        string finalPath,
        AudioFormat format,
        long capacityBytes,
        int sequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);
        ArgumentNullException.ThrowIfNull(format);

        if (capacityBytes <= 0 || capacityBytes % format.BlockAlign != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacityBytes),
                capacityBytes,
                $"Chunk capacity must be a positive multiple of the block align ({format.BlockAlign} bytes).");
        }

        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), sequence, "Chunk sequence starts at 1.");
        }

        var directory = Path.GetDirectoryName(partPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Format = format;
        PartPath = partPath;
        _finalPath = finalPath;
        _capacityBytes = capacityBytes;
        Sequence = sequence;

        _stream = new FileStream(
            partPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);

        _stream.Write(WavHeader.Build(format, 0));
        _stream.Flush();
    }

    /// <summary>The in-progress <c>.part</c> path.</summary>
    public string PartPath { get; }

    /// <summary>The durable <c>.wav</c> path this chunk is renamed to when closed.</summary>
    public string FinalPath => _finalPath;

    /// <summary>1-based chunk number within the track.</summary>
    public int Sequence { get; }

    public AudioFormat Format { get; }

    /// <summary>Audio bytes appended so far, excluding the header.</summary>
    public long DataBytesWritten { get; private set; }

    /// <summary>True once the chunk holds as much audio as it may.</summary>
    public bool IsFull => DataBytesWritten >= _capacityBytes;

    /// <summary>True once any audio has been appended.</summary>
    public bool HasData => DataBytesWritten > 0;

    /// <summary>True once <see cref="Close"/> completed successfully.</summary>
    public bool IsClosed => _closed;

    /// <summary>Data capacity of this chunk in bytes.</summary>
    public long CapacityBytes => _capacityBytes;

    /// <summary>
    /// Appends as much of <paramref name="data"/> as fits and returns how many bytes
    /// were written. A short write means the caller must close this chunk and
    /// continue in the next one, which is how chunk boundaries stay exact instead of
    /// overshooting by whatever the device happened to deliver.
    /// </summary>
    public int Append(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_closed, this);

        var remaining = _capacityBytes - DataBytesWritten;
        if (remaining <= 0)
        {
            return 0;
        }

        var take = (int)Math.Min(remaining, data.Length);
        if (take <= 0)
        {
            return 0;
        }

        _stream.Write(data[..take]);
        DataBytesWritten += take;
        return take;
    }

    /// <summary>Flushes buffered audio towards the operating system.</summary>
    public void Flush() => _stream.Flush();

    /// <summary>
    /// Finalizes the chunk: patch the header, flush to disk, validate, then
    /// atomically rename out of <c>.part</c>.
    /// </summary>
    /// <exception cref="CaptureFailedException">
    /// The finalized file did not validate. The <c>.part</c> file is left on disk so
    /// startup recovery can inspect it; nothing is deleted.
    /// </exception>
    public ChunkCloseResult Close(DateTimeOffset closedAt)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        _closed = true;

        Span<byte> header = stackalloc byte[WavHeader.Size];
        _stream.Position = 0;
        _stream.ReadExactly(header);
        WavHeader.PatchDataLength(header, DataBytesWritten);
        _stream.Position = 0;
        _stream.Write(header);
        _stream.Flush(flushToDisk: true);
        _stream.Dispose();

        var validation = WaveChunkValidator.ValidateClosedFile(PartPath, Format);
        if (!validation.IsValid)
        {
            throw new CaptureFailedException(
                $"Finalized chunk '{PartPath}' failed validation ({validation.Error}). " +
                "The .part file was kept; run 'meetcap status' to let startup recovery inspect it.");
        }

        File.Move(PartPath, _finalPath);

        return new ChunkCloseResult(_finalPath, validation.DataBytes, closedAt);
    }

    /// <summary>
    /// Releases the file handle without finalizing. The <c>.part</c> file stays on
    /// disk for recovery; this is the crash-equivalent path.
    /// </summary>
    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        try
        {
            _stream.Flush();
        }
        catch (IOException)
        {
            // Nothing useful to do while abandoning a partially written chunk.
        }

        _stream.Dispose();
    }
}

/// <summary>What a successfully closed chunk produced.</summary>
public sealed record ChunkCloseResult(string FinalPath, long DataBytes, DateTimeOffset ClosedAt);
