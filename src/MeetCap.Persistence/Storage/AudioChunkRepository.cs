namespace MeetCap.Persistence.Storage;

using MeetCap.Core.Capture;
using MeetCap.Core.Sessions;
using Microsoft.Data.Sqlite;

/// <summary>
/// Reads and writes the <c>audio_chunks</c> index (docs/DATA_MODEL.md section 5).
/// The audio bytes themselves stay on the filesystem; only their index lives here.
/// </summary>
public sealed class AudioChunkRepository
{
    private const string Columns =
        "id, session_id, source, sequence, path, start_ms, end_ms, sample_rate, channels, " +
        "bits_per_sample, sample_format, byte_length, status, device_position_frames, " +
        "qpc_position_ticks, sha256, created_at, closed_at";

    private readonly MeetCapDatabase _database;

    internal AudioChunkRepository(MeetCapDatabase database)
    {
        _database = database;
    }

    /// <summary>
    /// Inserts a chunk row, or refreshes the mutable columns when the row already
    /// exists. A chunk is indexed as <c>open</c> when its <c>.part</c> file is
    /// created and updated to <c>closed</c>/<c>recovered</c> when it becomes durable.
    /// </summary>
    public void Upsert(AudioChunkRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        using var conn = _database.Open();
        using var cmd = new SqliteCommand(
            "INSERT INTO audio_chunks (" + Columns + ") VALUES (" +
            "@id, @session_id, @source, @sequence, @path, @start_ms, @end_ms, @sample_rate, @channels, " +
            "@bits_per_sample, @sample_format, @byte_length, @status, @device_position_frames, " +
            "@qpc_position_ticks, @sha256, @created_at, @closed_at) " +
            "ON CONFLICT(id) DO UPDATE SET " +
            "path = excluded.path, start_ms = excluded.start_ms, end_ms = excluded.end_ms, " +
            "sample_rate = excluded.sample_rate, channels = excluded.channels, " +
            "bits_per_sample = excluded.bits_per_sample, sample_format = excluded.sample_format, " +
            "byte_length = excluded.byte_length, status = excluded.status, " +
            "device_position_frames = excluded.device_position_frames, " +
            "qpc_position_ticks = excluded.qpc_position_ticks, sha256 = excluded.sha256, " +
            "closed_at = excluded.closed_at",
            conn);
        Bind(cmd, record);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Finds one chunk of a track by its 1-based sequence number.</summary>
    public AudioChunkRecord? Find(string sessionId, AudioSource source, int sequence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        using var conn = _database.Open();
        using var cmd = new SqliteCommand(
            "SELECT " + Columns + " FROM audio_chunks WHERE session_id = @session_id AND source = @source " +
            "AND sequence = @sequence",
            conn);
        cmd.Parameters.AddWithValue("@session_id", sessionId);
        cmd.Parameters.AddWithValue("@source", source.ToWireName());
        cmd.Parameters.AddWithValue("@sequence", sequence);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    /// <summary>All indexed chunks of a session, ordered by track then sequence.</summary>
    public IReadOnlyList<AudioChunkRecord> ListForSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        using var conn = _database.Open();
        using var cmd = new SqliteCommand(
            "SELECT " + Columns + " FROM audio_chunks WHERE session_id = @session_id " +
            "ORDER BY source, sequence",
            conn);
        cmd.Parameters.AddWithValue("@session_id", sessionId);
        using var reader = cmd.ExecuteReader();
        var chunks = new List<AudioChunkRecord>();
        while (reader.Read())
        {
            chunks.Add(Map(reader));
        }

        return chunks;
    }

    /// <summary>Chunks of one session in a given lifecycle state.</summary>
    public IReadOnlyList<AudioChunkRecord> ListByStatus(string sessionId, string status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        using var conn = _database.Open();
        using var cmd = new SqliteCommand(
            "SELECT " + Columns + " FROM audio_chunks WHERE session_id = @session_id AND status = @status " +
            "ORDER BY source, sequence",
            conn);
        cmd.Parameters.AddWithValue("@session_id", sessionId);
        cmd.Parameters.AddWithValue("@status", status);
        using var reader = cmd.ExecuteReader();
        var chunks = new List<AudioChunkRecord>();
        while (reader.Read())
        {
            chunks.Add(Map(reader));
        }

        return chunks;
    }

    /// <summary>Number of indexed chunks of a session.</summary>
    public int CountForSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        using var conn = _database.Open();
        using var cmd = new SqliteCommand(
            "SELECT COUNT(*) FROM audio_chunks WHERE session_id = @session_id",
            conn);
        cmd.Parameters.AddWithValue("@session_id", sessionId);
        var result = cmd.ExecuteScalar();
        return result is long l ? (int)l : 0;
    }

    /// <summary>Total indexed bytes of a session's durable audio.</summary>
    public long TotalByteLengthForSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        using var conn = _database.Open();
        using var cmd = new SqliteCommand(
            "SELECT COALESCE(SUM(byte_length), 0) FROM audio_chunks WHERE session_id = @session_id",
            conn);
        cmd.Parameters.AddWithValue("@session_id", sessionId);
        var result = cmd.ExecuteScalar();
        return result is long l ? l : 0;
    }

    private static void Bind(SqliteCommand cmd, AudioChunkRecord record)
    {
        cmd.Parameters.AddWithValue("@id", record.Id);
        cmd.Parameters.AddWithValue("@session_id", record.SessionId);
        cmd.Parameters.AddWithValue("@source", record.Source.ToWireName());
        cmd.Parameters.AddWithValue("@sequence", record.Sequence);
        cmd.Parameters.AddWithValue("@path", record.RelativePath);
        cmd.Parameters.AddWithValue("@start_ms", record.StartMs);
        cmd.Parameters.AddWithValue("@end_ms", record.EndMs);
        cmd.Parameters.AddWithValue("@sample_rate", record.Format.SampleRate);
        cmd.Parameters.AddWithValue("@channels", record.Format.Channels);
        cmd.Parameters.AddWithValue("@bits_per_sample", record.Format.BitsPerSample);
        cmd.Parameters.AddWithValue("@sample_format", record.Format.SampleFormatName);
        cmd.Parameters.AddWithValue("@byte_length", record.ByteLength);
        cmd.Parameters.AddWithValue("@status", record.Status);
        cmd.Parameters.AddWithValue("@device_position_frames", (object?)record.DevicePositionFrames ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@qpc_position_ticks", (object?)record.QpcPositionTicks ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sha256", DBNull.Value);
        cmd.Parameters.AddWithValue("@created_at", SqlTimestamp.Format(record.CreatedAt));
        cmd.Parameters.AddWithValue("@closed_at", (object?)SqlTimestamp.Format(record.ClosedAt) ?? DBNull.Value);
    }

    private static AudioChunkRecord Map(SqliteDataReader reader)
    {
        var sampleFormatName = reader.GetString(10);
        if (!AudioSampleFormatNames.TryParse(sampleFormatName, out var sampleFormat))
        {
            throw new InvalidOperationException(
                $"Stored audio_chunks.sample_format '{sampleFormatName}' is not a known sample format.");
        }

        var sourceName = reader.GetString(2);
        if (!AudioSources.TryParse(sourceName, out var source))
        {
            throw new InvalidOperationException(
                $"Stored audio_chunks.source '{sourceName}' is not a known audio source.");
        }

        return new AudioChunkRecord
        {
            Id = reader.GetString(0),
            SessionId = reader.GetString(1),
            Source = source,
            Sequence = reader.GetInt32(3),
            RelativePath = reader.GetString(4),
            StartMs = reader.GetInt64(5),
            EndMs = reader.GetInt64(6),
            Format = new AudioFormat(
                reader.GetInt32(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                sampleFormat),
            ByteLength = reader.GetInt64(11),
            Status = reader.GetString(12),
            DevicePositionFrames = reader.IsDBNull(13) ? null : reader.GetInt64(13),
            QpcPositionTicks = reader.IsDBNull(14) ? null : reader.GetInt64(14),
            CreatedAt = SqlTimestamp.ParseRequired(reader.GetString(16)),
            ClosedAt = SqlTimestamp.Parse(reader.IsDBNull(17) ? null : reader.GetString(17)),
        };
    }
}
