namespace MeetCap.Persistence.Storage;

using System.Text.Json;
using MeetCap.Core.Sessions;
using Microsoft.Data.Sqlite;

/// <summary>
/// Reads and writes the <c>sessions</c> table (docs/DATA_MODEL.md section 1).
/// </summary>
/// <remarks>
/// Columns are mapped by hand rather than by an ORM: the M0 baseline deliberately
/// keeps this layer thin (docs/DEVELOPMENT.md section 3), and manual mapping keeps
/// the stored schema visible next to the SQL that produces it.
/// </remarks>
public sealed class SessionRepository
{
    private const string Columns =
        "id, title, mode, source_type, status, started_at, stopped_at, duration_ms, " +
        "config_snapshot, config_version, tracks, created_at, updated_at";

    private readonly MeetCapDatabase _database;

    internal SessionRepository(MeetCapDatabase database)
    {
        _database = database;
    }

    /// <summary>Inserts a new session row. Throws when the id already exists.</summary>
    public void Insert(SessionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        using var conn = _database.Open();
        using var cmd = new SqliteCommand(
            "INSERT INTO sessions (" + Columns + ") VALUES (" +
            "@id, @title, @mode, @source_type, @status, @started_at, @stopped_at, @duration_ms, " +
            "@config_snapshot, @config_version, @tracks, @created_at, @updated_at)",
            conn);
        Bind(cmd, record);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Inserts or replaces the mutable columns of a session row. Used by recovery,
    /// which may run against a database whose session row is already present.
    /// </summary>
    public void Upsert(SessionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        using var conn = _database.Open();
        using var cmd = new SqliteCommand(
            "INSERT INTO sessions (" + Columns + ") VALUES (" +
            "@id, @title, @mode, @source_type, @status, @started_at, @stopped_at, @duration_ms, " +
            "@config_snapshot, @config_version, @tracks, @created_at, @updated_at) " +
            "ON CONFLICT(id) DO UPDATE SET " +
            "title = excluded.title, mode = excluded.mode, source_type = excluded.source_type, " +
            "status = excluded.status, started_at = excluded.started_at, stopped_at = excluded.stopped_at, " +
            "duration_ms = excluded.duration_ms, config_snapshot = excluded.config_snapshot, " +
            "config_version = excluded.config_version, tracks = excluded.tracks, " +
            "updated_at = excluded.updated_at",
            conn);
        Bind(cmd, record);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Updates the lifecycle columns of an existing session row.</summary>
    public void UpdateLifecycle(
        string sessionId,
        string status,
        DateTimeOffset updatedAt,
        DateTimeOffset? stoppedAt,
        long durationMs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        using var conn = _database.Open();
        using var cmd = new SqliteCommand(
            "UPDATE sessions SET status = @status, stopped_at = @stopped_at, duration_ms = @duration_ms, " +
            "updated_at = @updated_at WHERE id = @id",
            conn);
        cmd.Parameters.AddWithValue("@id", sessionId);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@stopped_at", (object?)SqlTimestamp.Format(stoppedAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@duration_ms", durationMs);
        cmd.Parameters.AddWithValue("@updated_at", SqlTimestamp.Format(updatedAt));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Returns the session with <paramref name="sessionId"/>, or <c>null</c>.</summary>
    public SessionRecord? Find(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        using var conn = _database.Open();
        using var cmd = new SqliteCommand(
            "SELECT " + Columns + " FROM sessions WHERE id = @id",
            conn);
        cmd.Parameters.AddWithValue("@id", sessionId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    /// <summary>Sessions that still hold the recording surface.</summary>
    public IReadOnlyList<SessionRecord> ListActive()
        => ListByStatuses(SessionStatus.Active);

    /// <summary>
    /// Sessions that startup recovery must inspect: created, recording or finalizing
    /// but never cleanly completed (docs/RELIABILITY.md section 6).
    /// </summary>
    public IReadOnlyList<SessionRecord> ListNeedingRecovery()
        => ListByStatuses(SessionStatus.Recoverable);

    /// <summary>
    /// The session the CLI should consider "the recording in progress": the newest
    /// session that is created or recording. Finalizing sessions are excluded because
    /// they are already on their way out.
    /// </summary>
    public SessionRecord? FindActiveSession()
        => ListByStatuses(new[] { SessionStatus.Created, SessionStatus.Recording }).LastOrDefault();

    /// <summary>All session ids, newest first. Used by recovery to find orphan directories.</summary>
    public IReadOnlyList<string> ListIds()
    {
        using var conn = _database.Open();
        using var cmd = new SqliteCommand("SELECT id FROM sessions ORDER BY created_at DESC", conn);
        using var reader = cmd.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private IReadOnlyList<SessionRecord> ListByStatuses(IReadOnlyList<string> statuses)
    {
        using var conn = _database.Open();
        using var cmd = new SqliteCommand(
            "SELECT " + Columns + " FROM sessions WHERE status IN (" +
            SqlList.Placeholders(statuses.Count) + ") ORDER BY created_at",
            conn);
        SqlListParameters.Add(cmd, statuses);

        using var reader = cmd.ExecuteReader();
        var sessions = new List<SessionRecord>();
        while (reader.Read())
        {
            sessions.Add(Map(reader));
        }

        return sessions;
    }

    private static void Bind(SqliteCommand cmd, SessionRecord record)
    {
        cmd.Parameters.AddWithValue("@id", record.Id);
        cmd.Parameters.AddWithValue("@title", record.Title);
        cmd.Parameters.AddWithValue("@mode", record.Mode);
        cmd.Parameters.AddWithValue("@source_type", record.SourceType);
        cmd.Parameters.AddWithValue("@status", record.Status);
        cmd.Parameters.AddWithValue("@started_at", (object?)SqlTimestamp.Format(record.StartedAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@stopped_at", (object?)SqlTimestamp.Format(record.StoppedAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@duration_ms", record.DurationMs);
        cmd.Parameters.AddWithValue("@config_snapshot", record.ConfigSnapshot);
        cmd.Parameters.AddWithValue("@config_version", record.ConfigVersion);
        cmd.Parameters.AddWithValue("@tracks", JsonSerializer.Serialize(record.Tracks));
        cmd.Parameters.AddWithValue("@created_at", SqlTimestamp.Format(record.CreatedAt));
        cmd.Parameters.AddWithValue("@updated_at", SqlTimestamp.Format(record.UpdatedAt));
    }

    private static SessionRecord Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        Title = reader.GetString(1),
        Mode = reader.GetString(2),
        SourceType = reader.GetString(3),
        Status = reader.GetString(4),
        StartedAt = SqlTimestamp.Parse(reader.IsDBNull(5) ? null : reader.GetString(5)),
        StoppedAt = SqlTimestamp.Parse(reader.IsDBNull(6) ? null : reader.GetString(6)),
        DurationMs = reader.GetInt64(7),
        ConfigSnapshot = reader.GetString(8),
        ConfigVersion = reader.GetInt32(9),
        Tracks = DeserializeTracks(reader.GetString(10)),
        CreatedAt = SqlTimestamp.ParseRequired(reader.GetString(11)),
        UpdatedAt = SqlTimestamp.ParseRequired(reader.GetString(12)),
    };

    private static IReadOnlyList<string> DeserializeTracks(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (JsonException)
        {
            // A hand-edited or truncated value must not make the session unreadable.
            return Array.Empty<string>();
        }
    }
}
