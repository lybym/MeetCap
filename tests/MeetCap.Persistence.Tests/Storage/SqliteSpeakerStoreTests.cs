using MeetCap.Core.Ids;
using MeetCap.Core.Speakers;
using MeetCap.Persistence.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeetCap.Persistence.Tests.Storage;

/// <summary>
/// Round-trip tests for the SQLite speaker store (docs/DATA_MODEL.md sections 8-10).
/// Reopens the database between writes so persistence across process restarts is
/// verified, not just in-memory state.
/// </summary>
public class SqliteSpeakerStoreTests
{
    private static readonly DateTimeOffset s_now = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);

    private static Speaker Speaker(string name = "Alice", bool active = true) => new()
    {
        Id = Ids.NewSpeakerId(),
        DisplayName = name,
        Aliases = ["Al"],
        Active = active,
        CreatedAt = s_now,
        UpdatedAt = s_now,
    };

    private static SpeakerEmbedding Embedding(string speakerId, float[] values) => new()
    {
        Id = Ids.NewSpeakerEmbeddingId(),
        SpeakerId = speakerId,
        ModelName = "test-model",
        ModelVersion = "1",
        Dimension = values.Length,
        Values = values,
        SourceSessionId = "ses_1",
        SourceSegmentIds = ["seg_1", "seg_2"],
        QualityScore = 0.95,
        CreatedAt = s_now,
    };

    private static SpeakerAssignment Assignment(
        string sessionId, string label, string speakerId, string name) => new()
    {
        Id = Ids.NewSpeakerAssignmentId(),
        SessionId = sessionId,
        SpeakerLabel = label,
        SpeakerId = speakerId,
        SpeakerName = name,
        Source = SpeakerAssignmentSource.Manual,
        Locked = true,
        CreatedAt = s_now,
        UpdatedAt = s_now,
    };

    [Fact]
    public void Speaker_RoundTripsAllFields()
    {
        using var ws = new TempWorkspace();
        new SqliteMigrator().Migrate(ws.DatabasePath);

        var store = new SqliteSpeakerStore(ws.DatabasePath);
        var speaker = Speaker("Alice");
        store.CreateSpeaker(speaker);

        var loaded = store.GetSpeaker(speaker.Id);
        Assert.NotNull(loaded);
        Assert.Equal("Alice", loaded!.DisplayName);
        Assert.Equal(["Al"], loaded.Aliases);
        Assert.True(loaded.Active);
        Assert.Equal(s_now, loaded.CreatedAt);
    }

    [Fact]
    public void ListSpeakers_ReturnsAllOrderedByName()
    {
        using var ws = new TempWorkspace();
        new SqliteMigrator().Migrate(ws.DatabasePath);
        var store = new SqliteSpeakerStore(ws.DatabasePath);

        store.CreateSpeaker(Speaker("Charlie"));
        store.CreateSpeaker(Speaker("Alice"));
        store.CreateSpeaker(Speaker("Bob"));

        var all = store.ListSpeakers();
        Assert.Equal(3, all.Count);
        Assert.Equal("Alice", all[0].DisplayName);
        Assert.Equal("Bob", all[1].DisplayName);
        Assert.Equal("Charlie", all[2].DisplayName);
    }

    [Fact]
    public void ListActiveSpeakers_ExcludesInactive()
    {
        using var ws = new TempWorkspace();
        new SqliteMigrator().Migrate(ws.DatabasePath);
        var store = new SqliteSpeakerStore(ws.DatabasePath);

        store.CreateSpeaker(Speaker("Alice", active: true));
        store.CreateSpeaker(Speaker("Bob", active: false));

        var active = store.ListActiveSpeakers();
        Assert.Single(active);
        Assert.Equal("Alice", active[0].DisplayName);
    }

    [Fact]
    public void Embedding_RoundTripsFloatArray()
    {
        using var ws = new TempWorkspace();
        new SqliteMigrator().Migrate(ws.DatabasePath);
        var store = new SqliteSpeakerStore(ws.DatabasePath);

        var speaker = Speaker("Alice");
        store.CreateSpeaker(speaker);

        var values = new float[] { 0.1f, -0.2f, 0.3f, 0.99f, -1f };
        store.AddEmbedding(Embedding(speaker.Id, values));

        var loaded = store.ListEmbeddings(speaker.Id);
        Assert.Single(loaded);
        Assert.Equal(values, loaded[0].Values);
        Assert.Equal(5, loaded[0].Dimension);
        Assert.Equal("test-model", loaded[0].ModelName);
        Assert.Equal("ses_1", loaded[0].SourceSessionId);
        Assert.Equal(["seg_1", "seg_2"], loaded[0].SourceSegmentIds);
        Assert.Equal(0.95, loaded[0].QualityScore);
    }

    [Fact]
    public void MultipleEmbeddingsPerSpeaker_ArePersisted()
    {
        using var ws = new TempWorkspace();
        new SqliteMigrator().Migrate(ws.DatabasePath);
        var store = new SqliteSpeakerStore(ws.DatabasePath);

        var speaker = Speaker("Alice");
        store.CreateSpeaker(speaker);

        store.AddEmbedding(Embedding(speaker.Id, [1, 0, 0, 0]));
        store.AddEmbedding(Embedding(speaker.Id, [0, 1, 0, 0]));
        store.AddEmbedding(Embedding(speaker.Id, [0, 0, 1, 0]));

        var embeddings = store.ListEmbeddings(speaker.Id);
        Assert.Equal(3, embeddings.Count);
    }

    [Fact]
    public void Assignment_UpsertIsIdempotent()
    {
        using var ws = new TempWorkspace();
        new SqliteMigrator().Migrate(ws.DatabasePath);
        var store = new SqliteSpeakerStore(ws.DatabasePath);

        var speaker = Speaker("Alice");
        store.CreateSpeaker(speaker);

        store.UpsertAssignment(Assignment("ses_1", "speaker_0", speaker.Id, "Alice"));
        store.UpsertAssignment(Assignment("ses_1", "speaker_0", speaker.Id, "Alice"));

        var assignments = store.ListAssignments("ses_1");
        Assert.Single(assignments);
    }

    [Fact]
    public void Assignment_RoundTripsAllFields()
    {
        using var ws = new TempWorkspace();
        new SqliteMigrator().Migrate(ws.DatabasePath);
        var store = new SqliteSpeakerStore(ws.DatabasePath);

        var speaker = Speaker("Alice");
        store.CreateSpeaker(speaker);

        var assignment = Assignment("ses_1", "speaker_0", speaker.Id, "Alice");
        store.UpsertAssignment(assignment);

        var loaded = store.GetAssignment("ses_1", "speaker_0");
        Assert.NotNull(loaded);
        Assert.Equal("speaker_0", loaded!.SpeakerLabel);
        Assert.Equal(speaker.Id, loaded.SpeakerId);
        Assert.Equal("Alice", loaded.SpeakerName);
        Assert.Equal(SpeakerAssignmentSource.Manual, loaded.Source);
        Assert.True(loaded.Locked);
    }

    [Fact]
    public void Assignment_GetUnknownLabel_ReturnsNull()
    {
        using var ws = new TempWorkspace();
        new SqliteMigrator().Migrate(ws.DatabasePath);
        var store = new SqliteSpeakerStore(ws.DatabasePath);

        Assert.Null(store.GetAssignment("ses_1", "speaker_99"));
    }

    [Fact]
    public void ListAllEmbeddings_ReturnsAllOrderedBySpeaker()
    {
        using var ws = new TempWorkspace();
        new SqliteMigrator().Migrate(ws.DatabasePath);
        var store = new SqliteSpeakerStore(ws.DatabasePath);

        var alice = Speaker("Alice");
        var bob = Speaker("Bob");
        store.CreateSpeaker(alice);
        store.CreateSpeaker(bob);

        store.AddEmbedding(Embedding(alice.Id, [1, 0, 0, 0]));
        store.AddEmbedding(Embedding(bob.Id, [0, 1, 0, 0]));

        var all = store.ListAllEmbeddings();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void DeletingSpeaker_CascadesToEmbeddings()
    {
        using var ws = new TempWorkspace();
        new SqliteMigrator().Migrate(ws.DatabasePath);

        var speaker = Speaker("Alice");
        using (var conn = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = ws.DatabasePath, Pooling = false }.ToString()))
        {
            conn.Open();
            var store = new SqliteSpeakerStore(ws.DatabasePath);
            store.CreateSpeaker(speaker);
            store.AddEmbedding(Embedding(speaker.Id, [1, 0, 0, 0]));

            // Foreign keys must be on for cascade to work.
            using var fk = new SqliteCommand("PRAGMA foreign_keys = ON", conn);
            fk.ExecuteNonQuery();

            using var del = new SqliteCommand("DELETE FROM speakers WHERE id = @id", conn);
            del.Parameters.AddWithValue("@id", speaker.Id);
            del.ExecuteNonQuery();

            using var count = new SqliteCommand("SELECT COUNT(*) FROM speaker_embeddings WHERE speaker_id = @id", conn);
            count.Parameters.AddWithValue("@id", speaker.Id);
            Assert.Equal(0, Convert.ToInt32(count.ExecuteScalar()));
        }
    }

    [Fact]
    public void Assignment_SourceCheckConstraint_RejectsInvalid()
    {
        using var ws = new TempWorkspace();
        new SqliteMigrator().Migrate(ws.DatabasePath);

        using var conn = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = ws.DatabasePath, Pooling = false }.ToString());
        conn.Open();

        var speaker = Speaker("Alice");
        new SqliteSpeakerStore(ws.DatabasePath).CreateSpeaker(speaker);

        Assert.ThrowsAny<SqliteException>(() =>
        {
            using var cmd = new SqliteCommand(
                "INSERT INTO speaker_assignments (id, session_id, speaker_label, speaker_id, source, locked, created_at, updated_at) " +
                "VALUES ('a1', 'ses_1', 'speaker_0', @sid, 'invalid_source', 1, @now, @now)", conn);
            cmd.Parameters.AddWithValue("@sid", speaker.Id);
            cmd.Parameters.AddWithValue("@now", "2026-09-15T00:00:00Z");
            cmd.ExecuteNonQuery();
        });
    }

    [Fact]
    public void Speaker_DisplayNameIsUnique()
    {
        using var ws = new TempWorkspace();
        new SqliteMigrator().Migrate(ws.DatabasePath);
        var store = new SqliteSpeakerStore(ws.DatabasePath);

        store.CreateSpeaker(Speaker("Alice"));
        Assert.ThrowsAny<SqliteException>(() => store.CreateSpeaker(Speaker("Alice")));
    }
}
