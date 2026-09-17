namespace MeetCap.Persistence.Storage;

using System.Globalization;
using System.Text.Json;
using MeetCap.Core.Speakers;
using Microsoft.Data.Sqlite;

/// <summary>
/// SQLite-backed local Speaker Registry (<c>docs/DATA_MODEL.md</c> sections 8-10).
/// Stores speakers, multiple embeddings per person, and per-session assignments.
/// Voiceprints and name mappings are sensitive local data and never leave this
/// database by default (<c>docs/ARCHITECTURE.md</c> section 22).
/// </summary>
public sealed class SqliteSpeakerStore : ISpeakerStore
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = false,
    };

    private readonly string _dbPath;

    public SqliteSpeakerStore(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _dbPath = dbPath;
    }

    // --- Speakers ---

    public void CreateSpeaker(Speaker speaker)
    {
        ArgumentNullException.ThrowIfNull(speaker);
        using var conn = OpenWithForeignKeys();
        using var cmd = new SqliteCommand(
            """
            INSERT INTO speakers (id, display_name, aliases, active, created_at, updated_at)
            VALUES (@id, @name, @aliases, @active, @created, @updated)
            """,
            conn);

        BindSpeaker(cmd, speaker);
        cmd.ExecuteNonQuery();
    }

    public Speaker? GetSpeaker(string speakerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speakerId);
        if (!SpeakersTableExists()) return null;

        using var conn = SqliteConnectionFactory.Open(_dbPath);
        using var cmd = new SqliteCommand(
            "SELECT id, display_name, aliases, active, created_at, updated_at FROM speakers WHERE id = @id",
            conn);
        cmd.Parameters.AddWithValue("@id", speakerId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? MapSpeaker(reader) : null;
    }

    public IReadOnlyList<Speaker> ListSpeakers()
    {
        if (!SpeakersTableExists()) return [];
        return QuerySpeakers("WHERE 1=1");
    }

    public IReadOnlyList<Speaker> ListActiveSpeakers()
    {
        if (!SpeakersTableExists()) return [];
        return QuerySpeakers("WHERE active = 1");
    }

    public void UpdateSpeaker(Speaker speaker)
    {
        ArgumentNullException.ThrowIfNull(speaker);
        using var conn = OpenWithForeignKeys();
        using var cmd = new SqliteCommand(
            """
            UPDATE speakers SET
                display_name = @name,
                aliases = @aliases,
                active = @active,
                updated_at = @updated
            WHERE id = @id
            """,
            conn);

        BindSpeaker(cmd, speaker);
        if (cmd.ExecuteNonQuery() == 0)
        {
            throw new InvalidOperationException($"Speaker '{speaker.Id}' does not exist and cannot be updated.");
        }
    }

    // --- Embeddings ---

    public void AddEmbedding(SpeakerEmbedding embedding)
    {
        ArgumentNullException.ThrowIfNull(embedding);

        using var conn = OpenWithForeignKeys();
        using var cmd = new SqliteCommand(
            """
            INSERT INTO speaker_embeddings (
                id, speaker_id, model_name, model_version, dimension, embedding_blob,
                source_session_id, source_segment_ids, quality_score, created_at)
            VALUES (
                @id, @speakerId, @modelName, @modelVersion, @dimension, @blob,
                @sourceSessionId, @sourceSegmentIds, @qualityScore, @createdAt)
            """,
            conn);

        cmd.Parameters.AddWithValue("@id", embedding.Id);
        cmd.Parameters.AddWithValue("@speakerId", embedding.SpeakerId);
        cmd.Parameters.AddWithValue("@modelName", embedding.ModelName);
        cmd.Parameters.AddWithValue("@modelVersion", embedding.ModelVersion);
        cmd.Parameters.AddWithValue("@dimension", embedding.Dimension);
        cmd.Parameters.AddWithValue("@blob", EmbeddingToBlob(embedding.Values));
        cmd.Parameters.AddWithValue("@sourceSessionId", Text(embedding.SourceSessionId));
        cmd.Parameters.AddWithValue("@sourceSegmentIds", JsonSerializer.Serialize(embedding.SourceSegmentIds, s_json));
        cmd.Parameters.AddWithValue("@qualityScore", embedding.QualityScore is { } q ? q : DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt", embedding.CreatedAt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<SpeakerEmbedding> ListEmbeddings(string speakerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(speakerId);
        if (!EmbeddingsTableExists()) return [];

        using var conn = SqliteConnectionFactory.Open(_dbPath);
        using var cmd = new SqliteCommand(
            EmbeddingSelect + "WHERE speaker_id = @sid ORDER BY created_at, id",
            conn);
        cmd.Parameters.AddWithValue("@sid", speakerId);
        return ReadEmbeddings(cmd);
    }

    public IReadOnlyList<SpeakerEmbedding> ListAllEmbeddings()
    {
        if (!EmbeddingsTableExists()) return [];

        using var conn = SqliteConnectionFactory.Open(_dbPath);
        using var cmd = new SqliteCommand(EmbeddingSelect + "ORDER BY speaker_id, created_at, id", conn);
        return ReadEmbeddings(cmd);
    }

    // --- Assignments ---

    public void UpsertAssignment(SpeakerAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        using var conn = OpenWithForeignKeys();
        using var cmd = new SqliteCommand(
            """
            INSERT INTO speaker_assignments (
                id, session_id, speaker_label, speaker_id, speaker_name, confidence,
                source, locked, created_at, updated_at)
            VALUES (
                @id, @sessionId, @label, @speakerId, @speakerName, @confidence,
                @source, @locked, @created, @updated)
            ON CONFLICT(session_id, speaker_label) DO UPDATE SET
                speaker_id = excluded.speaker_id,
                speaker_name = excluded.speaker_name,
                confidence = excluded.confidence,
                source = excluded.source,
                locked = excluded.locked,
                updated_at = excluded.updated_at
            """,
            conn);

        BindAssignment(cmd, assignment);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<SpeakerAssignment> ListAssignments(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!AssignmentsTableExists()) return [];

        using var conn = SqliteConnectionFactory.Open(_dbPath);
        using var cmd = new SqliteCommand(
            AssignmentSelect + "WHERE session_id = @sid ORDER BY speaker_label",
            conn);
        cmd.Parameters.AddWithValue("@sid", sessionId);
        return ReadAssignments(cmd);
    }

    public SpeakerAssignment? GetAssignment(string sessionId, string speakerLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(speakerLabel);
        if (!AssignmentsTableExists()) return null;

        using var conn = SqliteConnectionFactory.Open(_dbPath);
        using var cmd = new SqliteCommand(
            AssignmentSelect + "WHERE session_id = @sid AND speaker_label = @label",
            conn);
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@label", speakerLabel);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? MapAssignment(reader) : null;
    }

    // --- Helpers ---

    private const string EmbeddingSelect =
        "SELECT id, speaker_id, model_name, model_version, dimension, embedding_blob, " +
        "source_session_id, source_segment_ids, quality_score, created_at FROM speaker_embeddings ";

    private const string AssignmentSelect =
        "SELECT id, session_id, speaker_label, speaker_id, speaker_name, confidence, " +
        "source, locked, created_at, updated_at FROM speaker_assignments ";

    private SqliteConnection OpenWithForeignKeys()
    {
        var conn = SqliteConnectionFactory.Open(_dbPath);
        using var pragma = new SqliteCommand("PRAGMA foreign_keys = ON", conn);
        pragma.ExecuteNonQuery();
        return conn;
    }

    private bool SpeakersTableExists()
    {
        using var conn = SqliteConnectionFactory.Open(_dbPath);
        return SqliteConnectionFactory.TableExists(conn, "speakers");
    }

    private bool EmbeddingsTableExists()
    {
        using var conn = SqliteConnectionFactory.Open(_dbPath);
        return SqliteConnectionFactory.TableExists(conn, "speaker_embeddings");
    }

    private bool AssignmentsTableExists()
    {
        using var conn = SqliteConnectionFactory.Open(_dbPath);
        return SqliteConnectionFactory.TableExists(conn, "speaker_assignments");
    }

    private IReadOnlyList<Speaker> QuerySpeakers(string whereClause)
    {
        using var conn = SqliteConnectionFactory.Open(_dbPath);
        using var cmd = new SqliteCommand(
            "SELECT id, display_name, aliases, active, created_at, updated_at FROM speakers " +
            whereClause + " ORDER BY display_name",
            conn);
        using var reader = cmd.ExecuteReader();
        var list = new List<Speaker>();
        while (reader.Read())
        {
            list.Add(MapSpeaker(reader));
        }

        return list;
    }

    private IReadOnlyList<SpeakerEmbedding> ReadEmbeddings(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var list = new List<SpeakerEmbedding>();
        while (reader.Read())
        {
            list.Add(MapEmbedding(reader));
        }

        return list;
    }

    private IReadOnlyList<SpeakerAssignment> ReadAssignments(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var list = new List<SpeakerAssignment>();
        while (reader.Read())
        {
            list.Add(MapAssignment(reader));
        }

        return list;
    }

    private static void BindSpeaker(SqliteCommand cmd, Speaker speaker)
    {
        cmd.Parameters.AddWithValue("@id", speaker.Id);
        cmd.Parameters.AddWithValue("@name", speaker.DisplayName);
        cmd.Parameters.AddWithValue("@aliases", JsonSerializer.Serialize(speaker.Aliases, s_json));
        cmd.Parameters.AddWithValue("@active", speaker.Active ? 1 : 0);
        cmd.Parameters.AddWithValue("@created", speaker.CreatedAt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@updated", speaker.UpdatedAt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
    }

    private static void BindAssignment(SqliteCommand cmd, SpeakerAssignment assignment)
    {
        cmd.Parameters.AddWithValue("@id", assignment.Id);
        cmd.Parameters.AddWithValue("@sessionId", assignment.SessionId);
        cmd.Parameters.AddWithValue("@label", assignment.SpeakerLabel);
        cmd.Parameters.AddWithValue("@speakerId", Text(assignment.SpeakerId));
        cmd.Parameters.AddWithValue("@speakerName", Text(assignment.SpeakerName));
        cmd.Parameters.AddWithValue("@confidence", assignment.Confidence is { } c ? c : DBNull.Value);
        cmd.Parameters.AddWithValue("@source", SpeakerAssignmentSources.ToWire(assignment.Source));
        cmd.Parameters.AddWithValue("@locked", assignment.Locked ? 1 : 0);
        cmd.Parameters.AddWithValue("@created", assignment.CreatedAt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@updated", assignment.UpdatedAt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
    }

    private static Speaker MapSpeaker(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("id")),
        DisplayName = reader.GetString(reader.GetOrdinal("display_name")),
        Aliases = ParseStringArray(reader.GetString(reader.GetOrdinal("aliases"))),
        Active = reader.GetInt32(reader.GetOrdinal("active")) != 0,
        CreatedAt = ReadTimestamp(reader, "created_at"),
        UpdatedAt = ReadTimestamp(reader, "updated_at"),
    };

    private static SpeakerEmbedding MapEmbedding(SqliteDataReader reader)
    {
        var blob = (byte[])reader["embedding_blob"];
        return new SpeakerEmbedding
        {
            Id = reader.GetString(reader.GetOrdinal("id")),
            SpeakerId = reader.GetString(reader.GetOrdinal("speaker_id")),
            ModelName = reader.GetString(reader.GetOrdinal("model_name")),
            ModelVersion = reader.GetString(reader.GetOrdinal("model_version")),
            Dimension = reader.GetInt32(reader.GetOrdinal("dimension")),
            Values = BlobToEmbedding(blob),
            SourceSessionId = ReadText(reader, "source_session_id"),
            SourceSegmentIds = ParseStringArray(ReadText(reader, "source_segment_ids") ?? "[]"),
            QualityScore = ReadNullableDouble(reader, "quality_score"),
            CreatedAt = ReadTimestamp(reader, "created_at"),
        };
    }

    private static SpeakerAssignment MapAssignment(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("id")),
        SessionId = reader.GetString(reader.GetOrdinal("session_id")),
        SpeakerLabel = reader.GetString(reader.GetOrdinal("speaker_label")),
        SpeakerId = ReadText(reader, "speaker_id"),
        SpeakerName = ReadText(reader, "speaker_name"),
        Confidence = ReadNullableDouble(reader, "confidence"),
        Source = SpeakerAssignmentSources.Parse(reader.GetString(reader.GetOrdinal("source"))),
        Locked = reader.GetInt32(reader.GetOrdinal("locked")) != 0,
        CreatedAt = ReadTimestamp(reader, "created_at"),
        UpdatedAt = ReadTimestamp(reader, "updated_at"),
    };

    private static byte[] EmbeddingToBlob(float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] BlobToEmbedding(byte[] blob)
    {
        var values = new float[blob.Length / sizeof(float)];
        Buffer.BlockCopy(blob, 0, values, 0, blob.Length);
        return values;
    }

    private static string[] ParseStringArray(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<string[]>(json, s_json) ?? [];
    }

    private static string? ReadText(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static double? ReadNullableDouble(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    }

    private static DateTimeOffset ReadTimestamp(SqliteDataReader reader, string column)
    {
        var text = ReadText(reader, column)
            ?? throw new InvalidOperationException($"Column '{column}' must not be null.");
        return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static object Text(string? value) => value is null ? DBNull.Value : value;
}
