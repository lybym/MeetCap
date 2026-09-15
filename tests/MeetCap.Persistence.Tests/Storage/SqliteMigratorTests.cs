using Microsoft.Data.Sqlite;
using MeetCap.Persistence.Storage;
using Xunit;

namespace MeetCap.Persistence.Tests.Storage;

public class SqliteMigratorTests
{
    private static string NewDb()
        => Path.Combine(Path.GetTempPath(), "meetcap-test-" + Guid.NewGuid().ToString("N"), "meetcap.db");

    private static void Cleanup(string db)
    {
        SqliteConnection.ClearAllPools();
        var dir = Path.GetDirectoryName(db);
        if (dir is null || !Directory.Exists(dir))
        {
            return;
        }

        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
            Thread.Sleep(100);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
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

    private static int Count(SqliteConnection c, string sql)
    {
        using var cmd = new SqliteCommand(sql, c);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void InsertSession(SqliteConnection c, string mode, string id = "ses_test")
    {
        using var cmd = new SqliteCommand(
            "INSERT INTO sessions (id, title, mode, source_type, status, created_at, updated_at) " +
            "VALUES (@id, @title, @mode, @st, @status, @ca, @ua)", c);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@title", "Test");
        cmd.Parameters.AddWithValue("@mode", mode);
        cmd.Parameters.AddWithValue("@st", "live");
        cmd.Parameters.AddWithValue("@status", "COMPLETED");
        cmd.Parameters.AddWithValue("@ca", "2026-09-15T00:00:00Z");
        cmd.Parameters.AddWithValue("@ua", "2026-09-15T00:00:00Z");
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Migrate_CreatesSessionsMigrationsAndAudioChunkTables()
    {
        var db = NewDb();
        try
        {
            new SqliteMigrator().Migrate(db);
            using var c = Open(db);
            Assert.True(TableExists(c, "sessions"));
            Assert.True(TableExists(c, "schema_migrations"));
            Assert.True(TableExists(c, "audio_chunks"));
        }
        finally
        {
            Cleanup(db);
        }
    }

    [Fact]
    public void Migrate_RecordsEveryEmbeddedMigrationVersion()
    {
        var db = NewDb();
        try
        {
            var migrator = new SqliteMigrator();
            migrator.Migrate(db);
            using var c = Open(db);

            // The migration count is derived from the embedded resources rather than
            // hard-coded, so adding a migration cannot silently leave this test behind.
            Assert.Equal(migrator.SupportedVersions.Count, Count(c, "SELECT COUNT(*) FROM schema_migrations"));
            Assert.Equal(
                migrator.SupportedVersions.Max(),
                Count(c, "SELECT MAX(version) FROM schema_migrations"));
            Assert.Contains(1, migrator.SupportedVersions);
            Assert.Contains(2, migrator.SupportedVersions);
        }
        finally
        {
            Cleanup(db);
        }
    }

    [Fact]
    public void Migrate_IsIdempotent()
    {
        var db = NewDb();
        try
        {
            var migrator = new SqliteMigrator();
            migrator.Migrate(db);
            migrator.Migrate(db); // must not throw or duplicate
            using var c = Open(db);
            Assert.Equal(migrator.SupportedVersions.Count, Count(c, "SELECT COUNT(*) FROM schema_migrations"));
        }
        finally
        {
            Cleanup(db);
        }
    }

    [Fact]
    public void Migrate_ModeCheckConstraint_RejectsInvalidMode()
    {
        var db = NewDb();
        try
        {
            new SqliteMigrator().Migrate(db);
            using var c = Open(db);
            Assert.ThrowsAny<SqliteException>(() => InsertSession(c, mode: "hybrid"));
            InsertSession(c, mode: "offline");
            Assert.Equal(1, Count(c, "SELECT COUNT(*) FROM sessions"));
        }
        finally
        {
            Cleanup(db);
        }
    }

    [Fact]
    public void Migrate_ConcurrentFirstRun_AppliesEveryVersionExactlyOnce()
    {
        var db = NewDb();
        try
        {
            const int migrators = 4;
            using var start = new Barrier(migrators);
            var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
            var threads = new Thread[migrators];

            for (var i = 0; i < migrators; i++)
            {
                threads[i] = new Thread(() =>
                {
                    try
                    {
                        // Several migrators race to migrate the same clean database. The
                        // migration layer holds no lock of its own, so this exercises the
                        // documented guarantee: concurrent callers finish and each version
                        // is recorded once, because the version row is insert-or-ignore.
                        start.SignalAndWait();
                        new SqliteMigrator().Migrate(db);
                    }
                    catch (Exception ex)
                    {
                        failures.Enqueue(ex);
                    }
                });
                threads[i].Start();
            }

            foreach (var thread in threads)
            {
                Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "concurrent migration did not finish in time");
            }

            Assert.Empty(failures);
            using var c = Open(db);
            Assert.True(TableExists(c, "sessions"));
            Assert.True(TableExists(c, "audio_chunks"));
            Assert.Equal(
                new SqliteMigrator().SupportedVersions.Count,
                Count(c, "SELECT COUNT(*) FROM schema_migrations"));
            Assert.Equal(
                new SqliteMigrator().SupportedVersions.Max(),
                Count(c, "SELECT MAX(version) FROM schema_migrations"));
        }
        finally
        {
            Cleanup(db);
        }
    }

    [Fact]
    public void Migrate_RepeatedRun_IsIdempotentAndLeavesNoExtraArtifacts()
    {
        var db = NewDb();
        try
        {
            var migrator = new SqliteMigrator();
            migrator.Migrate(db);

            // A second run stands in for a later process start: it must observe the
            // already-applied version instead of failing on the primary key.
            new SqliteMigrator().Migrate(db);

            using var c = Open(db);
            Assert.Equal(
                new SqliteMigrator().SupportedVersions.Count,
                Count(c, "SELECT COUNT(*) FROM schema_migrations"));
        }
        finally
        {
            Cleanup(db);
        }
    }

    [Fact]
    public void Migrate_AudioChunks_RejectsUnknownSourceAndStatus()
    {
        var db = NewDb();
        try
        {
            new SqliteMigrator().Migrate(db);
            using var c = Open(db);
            InsertSession(c, mode: "offline", id: "ses_chunks");

            Assert.ThrowsAny<SqliteException>(() => InsertChunk(c, "ses_chunks", source: "speaker"));
            Assert.ThrowsAny<SqliteException>(() => InsertChunk(c, "ses_chunks", status: "unknown"));

            InsertChunk(c, "ses_chunks", source: "mic", status: "closed");
            Assert.Equal(1, Count(c, "SELECT COUNT(*) FROM audio_chunks"));
        }
        finally
        {
            Cleanup(db);
        }
    }

    [Fact]
    public void Migrate_AudioChunks_RequireAnExistingSession()
    {
        var db = NewDb();
        try
        {
            new SqliteMigrator().Migrate(db);
            using var c = Open(db);

            // foreign_keys is enabled per connection by MeetCapDatabase; this asserts the
            // declared reference is real and not decorative.
            using var pragma = new SqliteCommand("PRAGMA foreign_keys = ON", c);
            pragma.ExecuteNonQuery();

            Assert.ThrowsAny<SqliteException>(() => InsertChunk(c, "ses_missing", source: "mic"));
        }
        finally
        {
            Cleanup(db);
        }
    }

    private static void InsertChunk(
        SqliteConnection c,
        string sessionId,
        string source = "mic",
        string status = "closed")
    {
        using var cmd = new SqliteCommand(
            "INSERT INTO audio_chunks (id, session_id, source, sequence, path, start_ms, end_ms, " +
            "sample_rate, channels, bits_per_sample, sample_format, byte_length, status, created_at) " +
            "VALUES (@id, @sid, @source, @seq, @path, 0, 60000, 48000, 1, 16, 'pcm', 100, @status, @at)", c);
        cmd.Parameters.AddWithValue("@id", "chk_" + Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@source", source);
        cmd.Parameters.AddWithValue("@seq", 1);
        cmd.Parameters.AddWithValue("@path", "audio/mic/000001.wav");
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@at", "2026-09-15T00:00:00Z");
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void MeetCapDatabase_Lifecycle_IsInitializedAndZeroActive()
    {
        var db = NewDb();
        try
        {
            var database = new MeetCapDatabase(db);
            Assert.False(database.IsInitialized());
            database.EnsureMigrated();
            Assert.True(database.IsInitialized());
            Assert.Equal(0, database.CountActiveSessions());
        }
        finally
        {
            Cleanup(db);
        }
    }

    [Fact]
    public void MeetCapDatabase_CountActiveSessions_ReflectedInStatus()
    {
        var db = NewDb();
        try
        {
            var database = new MeetCapDatabase(db);
            database.EnsureMigrated();
            using var c = Open(db);
            InsertSession(c, mode: "offline", id: "ses_active");
            using var upd = new SqliteCommand(
                "UPDATE sessions SET status = 'RECORDING' WHERE id = 'ses_active'", c);
            upd.ExecuteNonQuery();
            Assert.Equal(1, database.CountActiveSessions());
        }
        finally
        {
            Cleanup(db);
        }
    }
}
