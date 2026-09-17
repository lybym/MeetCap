using MeetCap.Persistence.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeetCap.Persistence.Tests.Storage;

/// <summary>
/// Tests the 0004_speakers migration (M6, issue #8). Verifies the speakers,
/// speaker_embeddings, and speaker_assignments tables are created with the correct
/// constraints (docs/DATA_MODEL.md sections 8-10, 14).
/// </summary>
public class SpeakerMigrationTests
{
    private static string NewDb()
        => Path.Combine(Path.GetTempPath(), "meetcap-migration-test-" + Guid.NewGuid().ToString("N"), "meetcap.db");

    private static void Cleanup(string db)
    {
        SqliteConnection.ClearAllPools();
        var dir = Path.GetDirectoryName(db);
        if (dir is not null && Directory.Exists(dir))
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static SqliteConnection Open(string db)
    {
        var conn = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString());
        conn.Open();
        return conn;
    }

    private static bool TableExists(SqliteConnection c, string name)
    {
        using var cmd = new SqliteCommand(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name", c);
        cmd.Parameters.AddWithValue("@name", name);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    [Fact]
    public void Migrate_CreatesSpeakerTables()
    {
        var db = NewDb();
        try
        {
            new SqliteMigrator().Migrate(db);
            using var c = Open(db);
            Assert.True(TableExists(c, "speakers"));
            Assert.True(TableExists(c, "speaker_embeddings"));
            Assert.True(TableExists(c, "speaker_assignments"));
        }
        finally
        {
            Cleanup(db);
        }
    }

    [Fact]
    public void Migrate_Version4_IsApplied()
    {
        var db = NewDb();
        try
        {
            new SqliteMigrator().Migrate(db);
            using var c = Open(db);

            using var cmd = new SqliteCommand(
                "SELECT COUNT(*) FROM schema_migrations WHERE version = 4", c);
            Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));
        }
        finally
        {
            Cleanup(db);
        }
    }

    [Fact]
    public void Migrate_SpeakerEmbeddings_ForeignKeyEnforced()
    {
        var db = NewDb();
        try
        {
            new SqliteMigrator().Migrate(db);
            using var c = Open(db);
            using var fk = new SqliteCommand("PRAGMA foreign_keys = ON", c);
            fk.ExecuteNonQuery();

            // An embedding for a non-existent speaker should be rejected.
            Assert.ThrowsAny<SqliteException>(() =>
            {
                using var cmd = new SqliteCommand(
                    "INSERT INTO speaker_embeddings (id, speaker_id, model_name, model_version, " +
                    "dimension, embedding_blob, created_at) " +
                    "VALUES ('emb_1', 'person_missing', 'm', '1', 4, X'00000000', @now)", c);
                cmd.Parameters.AddWithValue("@now", "2026-09-15T00:00:00Z");
                cmd.ExecuteNonQuery();
            });
        }
        finally
        {
            Cleanup(db);
        }
    }

    [Fact]
    public void Migrate_Speakers_ActiveCheckConstraint()
    {
        var db = NewDb();
        try
        {
            new SqliteMigrator().Migrate(db);
            using var c = Open(db);

            // active = 2 should be rejected (only 0 or 1 allowed).
            Assert.ThrowsAny<SqliteException>(() =>
            {
                using var cmd = new SqliteCommand(
                    "INSERT INTO speakers (id, display_name, active, created_at, updated_at) " +
                    "VALUES ('person_1', 'Test', 2, @now, @now)", c);
                cmd.Parameters.AddWithValue("@now", "2026-09-15T00:00:00Z");
                cmd.ExecuteNonQuery();
            });
        }
        finally
        {
            Cleanup(db);
        }
    }

    [Fact]
    public void Migrate_Assignments_UniqueSessionLabel()
    {
        var db = NewDb();
        try
        {
            new SqliteMigrator().Migrate(db);
            using var c = Open(db);

            // Insert a speaker so the FK works.
            using var sp = new SqliteCommand(
                "INSERT INTO speakers (id, display_name, active, created_at, updated_at) " +
                "VALUES ('person_1', 'Alice', 1, @now, @now)", c);
            sp.Parameters.AddWithValue("@now", "2026-09-15T00:00:00Z");
            sp.ExecuteNonQuery();

            // First assignment for (ses_1, speaker_0) succeeds.
            using var a1 = new SqliteCommand(
                "INSERT INTO speaker_assignments (id, session_id, speaker_label, speaker_id, source, locked, created_at, updated_at) " +
                "VALUES ('a1', 'ses_1', 'speaker_0', 'person_1', 'manual', 1, @now, @now)", c);
            a1.Parameters.AddWithValue("@now", "2026-09-15T00:00:00Z");
            a1.ExecuteNonQuery();

            // Second assignment for the same (ses_1, speaker_0) should be rejected by UNIQUE.
            Assert.ThrowsAny<SqliteException>(() =>
            {
                using var a2 = new SqliteCommand(
                    "INSERT INTO speaker_assignments (id, session_id, speaker_label, speaker_id, source, locked, created_at, updated_at) " +
                    "VALUES ('a2', 'ses_1', 'speaker_0', 'person_1', 'manual', 1, @now, @now)", c);
                a2.Parameters.AddWithValue("@now", "2026-09-15T00:00:00Z");
                a2.ExecuteNonQuery();
            });
        }
        finally
        {
            Cleanup(db);
        }
    }

    [Fact]
    public void SupportedVersions_Includes4()
    {
        var versions = new SqliteMigrator().SupportedVersions;
        Assert.Contains(4, versions);
    }
}
