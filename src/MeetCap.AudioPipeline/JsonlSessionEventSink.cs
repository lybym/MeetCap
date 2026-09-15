namespace MeetCap.AudioPipeline;

using System.Buffers;
using System.Text.Json;
using MeetCap.Core.Sessions;

/// <summary>
/// Appends session events as JSONL (docs/DATA_MODEL.md section 4). One event per
/// line, UTF-8, flushed per write: the event log is the operational record of what
/// happened during a recording, so it must survive the same crashes the audio chunks
/// survive.
/// </summary>
public sealed class JsonlSessionEventSink : ISessionEventSink, IDisposable
{
    private readonly FileStream _stream;
    private readonly Lock _gate = new();
    private bool _disposed;

    public JsonlSessionEventSink(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Path = path;
        _stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            // ReadWrite sharing so `meetcap status` and tests can read the log while a
            // session is still recording.
            FileShare.ReadWrite,
            bufferSize: 4096,
            FileOptions.None);
    }

    /// <summary>The events file being appended to.</summary>
    public string Path { get; }

    public void Write(SessionEvent sessionEvent)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteTo(writer, sessionEvent);
        }

        // Events are written from the capture loop, the consumer and the housekeeping
        // loop, so the append has to be serialized to keep lines whole.
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _stream.Write(buffer.WrittenSpan);
            _stream.WriteByte((byte)'\n');
            _stream.Flush();
        }
    }

    /// <summary>
    /// Writes one event object. Public so tests can assert the exact wire shape
    /// without touching the filesystem.
    /// </summary>
    public static void WriteTo(Utf8JsonWriter writer, SessionEvent sessionEvent)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(sessionEvent);

        writer.WriteStartObject();
        writer.WriteString("event", sessionEvent.Name);
        writer.WriteNumber("at_ms", sessionEvent.AtMs);
        WriteOptionalString(writer, "source", sessionEvent.Source);
        WriteOptionalString(writer, "chunk", sessionEvent.Chunk);
        WriteOptionalNumber(writer, "start_ms", sessionEvent.StartMs);
        WriteOptionalNumber(writer, "end_ms", sessionEvent.EndMs);
        WriteOptionalNumber(writer, "gap_ms", sessionEvent.GapMs);
        WriteOptionalNumber(writer, "device_position_frames", sessionEvent.DevicePositionFrames);
        WriteOptionalNumber(writer, "qpc_position_ticks", sessionEvent.QpcPositionTicks);
        WriteOptionalNumber(writer, "free_bytes", sessionEvent.FreeBytes);
        if (sessionEvent.Count is { } count)
        {
            writer.WriteNumber("count", count);
        }

        WriteOptionalString(writer, "detail", sessionEvent.Detail);
        writer.WriteEndObject();
    }

    /// <summary>Serializes one event as a JSONL line (without the trailing newline).</summary>
    public static string Serialize(SessionEvent sessionEvent)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteTo(writer, sessionEvent);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteOptionalNumber(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is { } number)
        {
            writer.WriteNumber(name, number);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
    }
}

/// <summary>
/// Collects events in memory. Used by tests and by code paths that must not touch
/// the filesystem.
/// </summary>
public sealed class InMemorySessionEventSink : ISessionEventSink
{
    private readonly List<SessionEvent> _events = new();
    private readonly Lock _gate = new();

    public IReadOnlyList<SessionEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToList();
            }
        }
    }

    public void Write(SessionEvent sessionEvent)
    {
        ArgumentNullException.ThrowIfNull(sessionEvent);
        lock (_gate)
        {
            _events.Add(sessionEvent);
        }
    }

    /// <summary>Events with the given name, in order.</summary>
    public IReadOnlyList<SessionEvent> WithName(string name)
        => Events.Where(e => string.Equals(e.Name, name, StringComparison.Ordinal)).ToList();
}
