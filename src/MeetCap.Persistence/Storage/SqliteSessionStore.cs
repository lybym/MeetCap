namespace MeetCap.Persistence.Storage;

using System.Text.Json;
using MeetCap.Core.Sessions;
using Microsoft.Data.Sqlite;

/// <summary>
/// SQLite-backed session index. The filesystem remains the durable record of the
/// session (docs/DATA_MODEL.md preamble); this table indexes it so the CLI can
/// answer "is a session active?" without walking the data root.
/// </summary>
public sealed class SqliteSessionStore : ISessionStore
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly string _dbPath;

    public SqliteSessionStore(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _dbPath = dbPath;
    }

    public void Create(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        using var conn = SqliteConnectionFactory.Open(_dbPath);
        using var cmd = new SqliteCommand(
            """
            INSERT INTO sessions (
                id, title, mode, source_type, status, started_at, stopped_at, duration_ms,
                config_snapshot, config_version, tracks, created_at, updated_at)
            VALUES (
                @id, @title, @mode, @sourceType, @status, @startedAt, @stoppedAt, @durationMs,
                @configSnapshot, @configVersion, @tracks, @createdAt, @updatedAt)
            """,
            conn);

        cmd.Parameters.AddWithValue("@id", session.Id);
        cmd.Parameters.AddWithValue("@title", session.Title);
        cmd.Parameters.AddWithValue("@mode", session.Mode);
        cmd.Parameters.AddWithValue("@sourceType", session.SourceType);
        cmd.Parameters.AddWithValue("@status", session.Status);
        cmd.Parameters.AddWithValue("@startedAt", ToText(session.StartedAt));
        cmd.Parameters.AddWithValue("@stoppedAt", ToText(session.StoppedAt));
        cmd.Parameters.AddWithValue("@durationMs", session.DurationMs);
        cmd.Parameters.AddWithValue("@configSnapshot", session.ConfigSnapshotJson);
        cmd.Parameters.AddWithValue("@configVersion", session.ConfigVersion);
        cmd.Parameters.AddWithValue("@tracks", JsonSerializer.Serialize(session.Tracks, s_json));
        cmd.Parameters.AddWithValue("@createdAt", session.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@updatedAt", session.UpdatedAt.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public Session? Get(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        using var conn = SqliteConnectionFactory.Open(_dbPath);
        if (!SqliteConnectionFactory.TableExists(conn, "sessions"))
        {
            return null;
        }

        using var cmd = new SqliteCommand("SELECT * FROM sessions WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("@id", sessionId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Map(reader) : null;
    }

    public void Update(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        using var conn = SqliteConnectionFactory.Open(_dbPath);
        using var cmd = new SqliteCommand(
            """
            UPDATE sessions SET
                title = @title,
                mode = @mode,
                source_type = @sourceType,
                status = @status,
                started_at = @startedAt,
                stopped_at = @stoppedAt,
                duration_ms = @durationMs,
                config_snapshot = @configSnapshot,
                config_version = @configVersion,
                tracks = @tracks,
                updated_at = @updatedAt
            WHERE id = @id
            """,
            conn);

        cmd.Parameters.AddWithValue("@id", session.Id);
        cmd.Parameters.AddWithValue("@title", session.Title);
        cmd.Parameters.AddWithValue("@mode", session.Mode);
        cmd.Parameters.AddWithValue("@sourceType", session.SourceType);
        cmd.Parameters.AddWithValue("@status", session.Status);
        cmd.Parameters.AddWithValue("@startedAt", ToText(session.StartedAt));
        cmd.Parameters.AddWithValue("@stoppedAt", ToText(session.StoppedAt));
        cmd.Parameters.AddWithValue("@durationMs", session.DurationMs);
        cmd.Parameters.AddWithValue("@configSnapshot", session.ConfigSnapshotJson);
        cmd.Parameters.AddWithValue("@configVersion", session.ConfigVersion);
        cmd.Parameters.AddWithValue("@tracks", JsonSerializer.Serialize(session.Tracks, s_json));
        cmd.Parameters.AddWithValue("@updatedAt", session.UpdatedAt.ToString("o"));

        if (cmd.ExecuteNonQuery() == 0)
        {
            throw new InvalidOperationException($"Session '{session.Id}' does not exist and cannot be updated.");
        }
    }

    public int CountActive()
    {
        using var conn = SqliteConnectionFactory.Open(_dbPath);
        if (!SqliteConnectionFactory.TableExists(conn, "sessions"))
        {
            return 0;
        }

        using var cmd = new SqliteCommand(
            "SELECT COUNT(*) FROM sessions WHERE status IN (@s1, @s2, @s3, @s4)",
            conn);
        cmd.Parameters.AddWithValue("@s1", SessionStatus.Created);
        cmd.Parameters.AddWithValue("@s2", SessionStatus.Recording);
        cmd.Parameters.AddWithValue("@s3", SessionStatus.Finalizing);
        cmd.Parameters.AddWithValue("@s4", SessionStatus.Processing);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static Session Map(SqliteDataReader reader)
    {
        var tracksJson = reader.GetString(reader.GetOrdinal("tracks"));
        var tracks = JsonSerializer.Deserialize<List<string>>(tracksJson, s_json) ?? new List<string>();

        return new Session
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            Title = reader.GetString(reader.GetOrdinal("title")),
            Mode = reader.GetString(reader.GetOrdinal("mode")),
            SourceType = reader.GetString(reader.GetOrdinal("source_type")),
            Status = reader.GetString(reader.GetOrdinal("status")),
            StartedAt = ReadTimestamp(reader, "started_at"),
            StoppedAt = ReadTimestamp(reader, "stopped_at"),
            DurationMs = reader.GetInt64(reader.GetOrdinal("duration_ms")),
            ConfigSnapshotJson = reader.GetString(reader.GetOrdinal("config_snapshot")),
            ConfigVersion = reader.GetInt32(reader.GetOrdinal("config_version")),
            Tracks = tracks,
            CreatedAt = ReadRequiredTimestamp(reader, "created_at"),
            UpdatedAt = ReadRequiredTimestamp(reader, "updated_at"),
        };
    }

    private static object ToText(DateTimeOffset? value) =>
        value is null ? DBNull.Value : value.Value.ToString("o");

    private static DateTimeOffset? ReadTimestamp(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return DateTimeOffset.Parse(
            reader.GetString(ordinal),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);
    }

    private static DateTimeOffset ReadRequiredTimestamp(SqliteDataReader reader, string column) =>
        ReadTimestamp(reader, column)
        ?? throw new InvalidOperationException($"Column '{column}' must not be null.");
}
