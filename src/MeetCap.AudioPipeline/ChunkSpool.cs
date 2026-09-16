namespace MeetCap.AudioPipeline;

using MeetCap.Core.Capture;
using MeetCap.Core.Sessions;
using MeetCap.Core.Time;
using MeetCap.Persistence.Storage;
using MeetCap.AudioPipeline.Wave;

/// <summary>
/// Owns the <c>.part</c> lifecycle of one track: it opens chunks, splits oversized
/// packets at exact chunk boundaries, closes each chunk durably and keeps the chunk
/// index and event log in step.
/// </summary>
/// <remarks>
/// <para>
/// Single-threaded by design: only the recording consumer touches it, which is what
/// lets the capture callback stay free of file and database work
/// (docs/ARCHITECTURE.md section 7).
/// </para>
/// <para>
/// Chunks are cut by <em>frame capacity</em>, not by wall-clock time, so a chunk
/// boundary is exactly <c>chunk_seconds</c> of audio even when the device delivers
/// uneven buffers, and the boundary is reproducible in tests.
/// </para>
/// </remarks>
public sealed class ChunkSpool : IDisposable
{
    private readonly SessionPaths _paths;
    private readonly AudioSource _source;
    private readonly AudioFormat _format;
    private readonly long _capacityBytes;
    private readonly MeetCapDatabase _database;
    private readonly ISessionEventSink _events;
    private readonly IClock _clock;

    private WaveChunkWriter? _writer;
    private int _sequence;
    private long _currentStartMs;
    private long _currentEndMs;
    private long _currentDevicePositionFrames;
    private long? _currentQpcTicks;
    private bool _disposed;

    public ChunkSpool(
        SessionPaths paths,
        AudioSource source,
        AudioFormat format,
        int chunkSeconds,
        MeetCapDatabase database,
        ISessionEventSink events,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(clock);

        if (chunkSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSeconds), chunkSeconds, "Chunk seconds must be positive.");
        }

        _paths = paths;
        _source = source;
        _format = format;
        _database = database;
        _events = events;
        _clock = clock;

        // Frame-aligned so a chunk never splits a sample frame.
        _capacityBytes = format.FramesToBytes(format.MillisecondsToFrames(chunkSeconds * 1000L));
    }

    /// <summary>Number of chunks that have been closed durably.</summary>
    public int ClosedChunkCount { get; private set; }

    /// <summary>Total audio bytes in closed chunks.</summary>
    public long ClosedDataBytes { get; private set; }

    /// <summary>Sequence number of the most recently opened chunk.</summary>
    public int Sequence => _sequence;

    /// <summary>
    /// The most recently closed durable chunk, described for downstream consumers, or
    /// <c>null</c> when no chunk has been closed yet.
    /// </summary>
    /// <remarks>
    /// Chunk rotation happens inside <see cref="Append"/>, so "which chunk was just
    /// closed" cannot be derived by the caller from the result of its own
    /// <see cref="CloseCurrentChunk"/> call. The spool therefore remembers it, and the
    /// recording session announces it through
    /// <c>RecordingSession.ChunkClosed</c>.
    /// </remarks>
    public ClosedAudioChunk? LastClosed { get; private set; }

    /// <summary>Exclusive session-relative end of the audio written so far.</summary>
    public long WrittenEndMs => _currentEndMs;

    /// <summary>Whether a chunk is currently open.</summary>
    public bool HasOpenChunk => _writer is not null;

    /// <summary>Data capacity of one chunk in bytes.</summary>
    public long ChunkCapacityBytes => _capacityBytes;

    /// <summary>
    /// Appends a packet, rotating chunks at the capacity boundary. The packet is
    /// split when it straddles the boundary so chunk boundaries stay exact.
    /// </summary>
    public void Append(AudioPacket packet, PacketTiming timing)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var data = packet.Data;
        if (data.IsEmpty)
        {
            return;
        }

        var offset = 0;
        while (offset < data.Length)
        {
            OpenChunkIfNeeded(packet, timing, offset);

            var written = _writer!.Append(data.Span[offset..]);
            if (written <= 0)
            {
                // Capacity reached between iterations; close and continue in the next chunk.
                CloseCurrentChunk();
                continue;
            }

            offset += written;
            var framesConsumed = _format.BytesToFrames(offset);
            _currentEndMs = timing.StartMs + _format.FramesToMilliseconds(framesConsumed);

            if (_writer.IsFull)
            {
                CloseCurrentChunk();
            }
        }
    }

    /// <summary>
    /// Flushes the open chunk towards the operating system. Called from the
    /// configured flush interval, never from the capture callback.
    /// </summary>
    public void Flush() => _writer?.Flush();

    /// <summary>
    /// Finalizes the open chunk, if any, and records it as durable. Returns
    /// <c>null</c> when no chunk was open.
    /// </summary>
    public ChunkCloseResult? CloseCurrentChunk()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var writer = _writer;
        if (writer is null)
        {
            return null;
        }

        _writer = null;
        var chunkId = AudioChunkRecord.BuildId(_paths.SessionId, _source, writer.Sequence);

        // Close() patches the header, flushes to disk, validates and only then renames.
        // If validation fails it throws with the .part file still on disk.
        var result = writer.Close(_clock.UtcNow);

        UpsertChunk(chunkId, writer.Sequence, result.DataBytes, ChunkStates.Closed, result.ClosedAt);

        ClosedChunkCount++;
        ClosedDataBytes += result.DataBytes;

        // Recorded after the rename, so LastClosed only ever describes durable audio.
        LastClosed = new ClosedAudioChunk
        {
            SessionId = _paths.SessionId,
            Source = _source.ToWireName(),
            Sequence = writer.Sequence,
            FilePath = result.FinalPath,
            RelativePath = _paths.RelativeChunkPath(_source, writer.Sequence),
            StartMs = _currentStartMs,
            EndMs = _currentEndMs,
            DataBytes = result.DataBytes,
            Format = _format,
        };

        _events.Write(new SessionEvent(SessionEventNames.ChunkClosed, _currentEndMs)
        {
            Source = _source.ToWireName(),
            Chunk = Path.GetFileName(result.FinalPath),
            StartMs = _currentStartMs,
            EndMs = _currentEndMs,
        });

        return result;
    }

    private void OpenChunkIfNeeded(AudioPacket packet, PacketTiming timing, int offset)
    {
        if (_writer is not null)
        {
            return;
        }

        var sequence = ++_sequence;
        var framesIntoPacket = _format.BytesToFrames(offset);
        _currentStartMs = timing.StartMs + _format.FramesToMilliseconds(framesIntoPacket);
        _currentEndMs = _currentStartMs;
        _currentDevicePositionFrames = packet.DevicePositionFrames + framesIntoPacket;
        _currentQpcTicks = packet.QpcPositionTicks;

        _writer = new WaveChunkWriter(
            _paths.ChunkPartPath(_source, sequence),
            _paths.ChunkFinalPath(_source, sequence),
            _format,
            _capacityBytes,
            sequence);

        // Index the in-flight chunk immediately: docs/RELIABILITY.md section 6 requires
        // an unclean shutdown to leave an identifiable active chunk behind.
        UpsertChunk(
            AudioChunkRecord.BuildId(_paths.SessionId, _source, sequence),
            sequence,
            dataBytes: 0,
            ChunkStates.Open,
            closedAt: null);

        _events.Write(new SessionEvent(SessionEventNames.ChunkOpened, _currentStartMs)
        {
            Source = _source.ToWireName(),
            Chunk = SessionPaths.ChunkFileName(sequence),
            StartMs = _currentStartMs,
            DevicePositionFrames = _currentDevicePositionFrames,
            QpcPositionTicks = _currentQpcTicks,
        });
    }

    private void UpsertChunk(string chunkId, int sequence, long dataBytes, string status, DateTimeOffset? closedAt)
    {
        _database.Chunks.Upsert(new AudioChunkRecord
        {
            Id = chunkId,
            SessionId = _paths.SessionId,
            Source = _source,
            Sequence = sequence,
            RelativePath = _paths.RelativeChunkPath(_source, sequence),
            StartMs = _currentStartMs,
            EndMs = _currentEndMs,
            Format = _format,
            ByteLength = dataBytes,
            Status = status,
            DevicePositionFrames = _currentDevicePositionFrames,
            QpcPositionTicks = _currentQpcTicks,
            CreatedAt = _clock.UtcNow,
            ClosedAt = closedAt,
        });
    }

    /// <summary>
    /// Releases the open chunk without finalizing it. The <c>.part</c> file remains
    /// for startup recovery, which is the same outcome as a process kill.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writer?.Dispose();
        _writer = null;
    }
}
