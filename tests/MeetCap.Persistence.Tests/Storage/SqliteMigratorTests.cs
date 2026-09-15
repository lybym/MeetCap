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
    public void Migrate_CreatesSessionsAndMigrationsTables()
    {
        var db = NewDb();
        try
        {
            new SqliteMigrator(db).Migrate(db);
            using var c = Open(db);
            Assert.True(TableExists(c, "sessions"));
            Assert.True(TableExists(c, "schema_migrations"));
        }
        finally
        {
            Cleanup(db);
        }
    }

    [Fact]
    public void Migrate_RecordsVersionOne()
    {
        var db = NewDb();
        try
        {
            new SqliteMigrator(db).Migrate(db);
            using var c = Open(db);
            Assert.Equal(1, Count(c, "SELECT COUNT(*) FROM schema_migrations"));
            Assert.Equal(1, Count(c, "SELECT version FROM schema_migrations"));
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
            var migrator = new SqliteMigrator(db);
            migrator.Migrate(db);
            migrator.Migrate(db); // must not throw or duplicate
            using var c = Open(db);
            Assert.Equal(1, Count(c, "SELECT COUNT(*) FROM schema_migrations"));
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
            new SqliteMigrator(db).Migrate(db);
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
    public void Migrate_ConcurrentFirstRun_SerializesAndRecordsEachVersionOnce()
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
                        // Every thread races to migrate the same clean database, which is
                        // what two CLI processes starting together do.
                        start.SignalAndWait();
                        new SqliteMigrator(db).Migrate(db);
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
            Assert.Equal(1, Count(c, "SELECT COUNT(*) FROM schema_migrations"));
            Assert.Equal(1, Count(c, "SELECT version FROM schema_migrations"));
        }
        finally
        {
            Cleanup(db);
        }
    }

    [Fact]
    public void Migrate_RetryAfterCompetingMigrator_IsIdempotent()
    {
        var db = NewDb();
        try
        {
            var migrator = new SqliteMigrator(db);
            migrator.Migrate(db);

            // A second migrator instance (as another process would create) must observe
            // the already-applied version instead of failing on the primary key.
            var second = new SqliteMigrator(db);
            second.Migrate(db);

            using var c = Open(db);
            Assert.Equal(1, Count(c, "SELECT COUNT(*) FROM schema_migrations"));
        }
        finally
        {
            Cleanup(db);
        }
    }

    [Fact]
    public void MigrationLock_BlocksSecondAcquireUntilReleased()
    {
        var db = NewDb();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(db)!);

            using (var first = SqliteMigrator.AcquireMigrationLock(db))
            {
                Assert.NotNull(first);

                // While the lock is held, a competing acquisition must block rather than
                // proceed, which is what prevents the schema_migrations race.
                var acquired = new ManualResetEventSlim(false);
                var competitor = new Thread(() =>
                {
                    using var second = SqliteMigrator.AcquireMigrationLock(db);
                    acquired.Set();
                });
                competitor.IsBackground = true;
                competitor.Start();

                Assert.False(acquired.Wait(TimeSpan.FromMilliseconds(250)), "lock was acquired while already held");

                acquired.Reset();
            }

            using var reacquired = SqliteMigrator.AcquireMigrationLock(db);
            Assert.NotNull(reacquired);
        }
        finally
        {
            Cleanup(db);
        }
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
