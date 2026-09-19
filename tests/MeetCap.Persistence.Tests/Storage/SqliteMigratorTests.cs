using Microsoft.Data.Sqlite;
using MeetCap.Core.Asr;
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
    public void Migrate_RecordsEveryEmbeddedVersion()
    {
        var db = NewDb();
        try
        {
            var migrator = new SqliteMigrator();
            migrator.Migrate(db);
            using var c = Open(db);

            // The migration count is derived from the embedded resources rather than
            // hard-coded, so adding a migration cannot silently leave this test behind.
            var expected = ExpectedVersions();
            Assert.Equal(expected.Count, Count(c, "SELECT COUNT(*) FROM schema_migrations"));

            using var cmd = new SqliteCommand("SELECT version FROM schema_migrations ORDER BY version", c);
            var applied = new List<int>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                applied.Add(reader.GetInt32(0));
            }

            Assert.Equal(expected, applied);
            Assert.Contains(1, migrator.SupportedVersions);
            Assert.Contains(2, migrator.SupportedVersions);
            Assert.Contains(3, migrator.SupportedVersions);
            Assert.Contains(4, migrator.SupportedVersions);
            Assert.Contains(5, migrator.SupportedVersions);
        }
        finally
        {
            Cleanup(db);
        }
    }

    [Fact]
    public void Migrations_ClaimDistinctVersionNumbers()
    {
        // Two scripts sharing a version would make the second one look already applied,
        // so its tables would never be created at runtime. This asserts the embedded set
        // is well formed: 0001 (M0 sessions), 0002 (M1 audio_chunks), 0003 (M3 asr_jobs),
        // 0004 (M6 speakers) and 0005 (issue #26 provider_log_id) are all present and claim
        // distinct versions.
        var versions = new SqliteMigrator().GetMigrations().Select(m => m.Version).ToArray();

        Assert.Equal(versions.Length, versions.Distinct().Count());
        Assert.Contains(1, versions);
        Assert.Contains(2, versions);
        Assert.Contains(3, versions);
        Assert.Contains(4, versions);
        Assert.Contains(5, versions);
    }

    [Fact]
    public void Migrate_AddsProviderLogIdAndPreservesExistingAsrJobRows()
    {
        // 0005 rebuilds asr_jobs to add a nullable column (SQLite has no
        // ADD COLUMN IF NOT EXISTS). The rebuild must not lose rows, and the legacy `tier`
        // column must survive as a schema-compatibility field (docs/DATA_MODEL.md section 6).
        var db = NewDb();
        try
        {
            var migrator = new SqliteMigrator();
            var version = ProviderLogIdMigrationVersion();
            migrator.Migrate(db);

            using var c = Open(db);

            // Rewind to the schema and migration state a pre-#26 database has.
            using (var rewind = new SqliteCommand("ALTER TABLE asr_jobs DROP COLUMN provider_log_id", c))
            {
                rewind.ExecuteNonQuery();
            }

            using (var seed = new SqliteCommand(
                       "INSERT INTO asr_jobs (id, session_id, source, tier, provider, input_artifact, status, " +
                       "provider_request_id, created_at, updated_at) " +
                       "VALUES ('job_legacy', 'ses_1', 'import', 'idle', 'volcengine', 'audio/import/a.wav', " +
                       "'pending', 'req-1', @now, @now)",
                       c))
            {
                seed.Parameters.AddWithValue("@now", "2026-09-15T00:00:00Z");
                seed.ExecuteNonQuery();
            }

            using (var forget = new SqliteCommand("DELETE FROM schema_migrations WHERE version = @v", c))
            {
                forget.Parameters.AddWithValue("@v", version);
                forget.ExecuteNonQuery();
            }

            // The upgrade.
            migrator.Migrate(db);

            Assert.True(ColumnExists(c, "asr_jobs", "provider_log_id"));
            Assert.True(ColumnExists(c, "asr_jobs", "tier"));

            using (var read = new SqliteCommand(
                       "SELECT source, tier, provider, provider_request_id, provider_log_id " +
                       "FROM asr_jobs WHERE id = 'job_legacy'",
                       c))
            using (var reader = read.ExecuteReader())
            {
                Assert.True(reader.Read());
                Assert.Equal("import", reader.GetString(0));
                Assert.Equal("idle", reader.GetString(1));
                Assert.Equal("volcengine", reader.GetString(2));
                Assert.Equal("req-1", reader.GetString(3));
                Assert.True(reader.IsDBNull(4));
            }

            // `tier` is no longer a routing input, so the domain type reports the one
            // supported value regardless of what a pre-#26 row stored (docs/DATA_MODEL.md
            // section 6).
            Assert.Equal("standard", new SqliteAsrJobStore(db).Get("job_legacy")!.Tier);

            // Everything migration 0006 added must still be readable through the store after
            // 0005 replays. This is the regression the CI failure exposed: the 0005 rebuild
            // recreated `asr_jobs` from its own older shape, DROP TABLE removed the columns a
            // later migration had added, and the very next `Get` failed with
            // "no such column: audio_transport".
            var legacy = new SqliteAsrJobStore(db).Get("job_legacy")!;
            Assert.Equal("inline", legacy.AudioTransport);
            Assert.Null(legacy.TosBucket);
            Assert.Null(legacy.TosObjectKey);
            Assert.False(legacy.TosCleanupPending);
        }
        finally
        {
            Cleanup(db);
        }
    }

    [Fact]
    public void Migrate_ReRunOfBothAsrJobMigrations_PreservesEveryColumnAndValue()
    {
        // 0005 and 0006 both rebuild `asr_jobs`, and either can be the one that runs a second
        // time. Whichever replays, DROP TABLE removes columns it never heard of, so each rebuild
        // has to declare the whole post-#26 schema *and* copy every later column through
        // SqliteMigrator's schema-conditional placeholder. Replaying both is the strictest form
        // of that check: the table has to survive a full round trip with its data intact.
        var db = NewDb();
        try
        {
            var migrator = new SqliteMigrator();
            migrator.Migrate(db);

            using var c = Open(db);
            using (var seed = new SqliteCommand(
                       "INSERT INTO asr_jobs (id, session_id, source, tier, provider, input_artifact, " +
                       "status, provider_request_id, created_at, updated_at, provider_log_id, " +
                       "audio_transport, tos_bucket, tos_object_key, tos_cleanup_pending) " +
                       "VALUES ('job_tos', 'ses_1', 'import', 'standard', 'volcengine', " +
                       "'audio/import/a.wav', 'succeeded', 'req-tos', @now, @now, 'LOGID-REAL', " +
                       "'tos', 'meetcap-asr', 'meetcap-asr/ab/2026/09/19/job_tos.wav', 1)",
                       c))
            {
                seed.Parameters.AddWithValue("@now", "2026-09-15T00:00:00Z");
                seed.ExecuteNonQuery();
            }

            ForgetAsrJobMigration(c, "0005_asr_job_provider_log_id.sql");
            migrator.Migrate(db); // replay of 0005 with 0006's columns present
            Assert.True(ColumnExists(c, "asr_jobs", "audio_transport"));
            Assert.True(ColumnExists(c, "asr_jobs", "tos_cleanup_pending"));

            ForgetAsrJobMigration(c, "0006_tos_asr_transport.sql");
            migrator.Migrate(db); // replay of 0006 with 0005 already committed
            Assert.True(ColumnExists(c, "asr_jobs", "provider_log_id"));

            // No half-built table may survive either rebuild, and every migration is still
            // recorded exactly once.
            Assert.False(TableExists(c, "asr_jobs_new"));
            Assert.Equal(ExpectedVersions().Count, Count(c, "SELECT COUNT(*) FROM schema_migrations"));

            using (var read = new SqliteCommand(
                       "SELECT provider_log_id, audio_transport, tos_bucket, tos_object_key, " +
                       "tos_cleanup_pending, status FROM asr_jobs WHERE id = 'job_tos'",
                       c))
            using (var reader = read.ExecuteReader())
            {
                Assert.True(reader.Read());
                Assert.Equal("LOGID-REAL", reader.GetString(0));
                Assert.Equal("tos", reader.GetString(1));
                Assert.Equal("meetcap-asr", reader.GetString(2));
                Assert.Equal("meetcap-asr/ab/2026/09/19/job_tos.wav", reader.GetString(3));
                // The cleanup debt is durable state, not a transient hint: a replay must not
                // quietly mark an object as released that is still in the bucket.
                Assert.Equal(1, reader.GetInt32(4));
                Assert.Equal("succeeded", reader.GetString(5));
            }

            // The domain store is the real reader, so it is what proves the columns are usable.
            var stored = new SqliteAsrJobStore(db).Get("job_tos")!;
            Assert.Equal("tos", stored.AudioTransport);
            Assert.Equal("meetcap-asr", stored.TosBucket);
            Assert.Equal("meetcap-asr/ab/2026/09/19/job_tos.wav", stored.TosObjectKey);
            Assert.True(stored.TosCleanupPending);
        }
        finally
        {
            Cleanup(db);
        }
    }

    [Fact]
    public void Migrate_UpgradeFromAPreIssue29Database_AddsTheTransportColumnsWithInlineDefaults()
    {
        // The other half of the upgrade story: a data root created before issue #29 has no
        // transport columns at all. Forward migration must add them, and every existing job must
        // read back as an inline job with no TOS identity, so nothing is retroactively claimed to
        // have been staged.
        var db = NewDb();
        try
        {
            var migrator = new SqliteMigrator();
            migrator.Migrate(db);

            using (var c = Open(db))
            {
                using (var insert = new SqliteCommand(
                           "INSERT INTO asr_jobs (id, session_id, source, tier, provider, input_artifact, " +
                           "status, provider_request_id, created_at, updated_at) " +
                           "VALUES ('job_pre29', 'ses_1', 'import', 'standard', 'volcengine', " +
                           "'audio/import/a.wav', 'pending', 'req-pre', @now, @now)",
                           c))
                {
                    insert.Parameters.AddWithValue("@now", "2026-09-15T00:00:00Z");
                    insert.ExecuteNonQuery();
                }

                // Rewind to the pre-#29 schema: drop 0006's columns and forget its version, which
                // is exactly the state a data root migrated before this issue is in.
                foreach (var column in new[] { "audio_transport", "tos_bucket", "tos_object_key", "tos_cleanup_pending" })
                {
                    using var drop = new SqliteCommand($"ALTER TABLE asr_jobs DROP COLUMN {column}", c);
                    drop.ExecuteNonQuery();
                }

                ForgetAsrJobMigration(c, "0006_tos_asr_transport.sql");
            }

            migrator.Migrate(db);

            using (var c = Open(db))
            {
                Assert.True(ColumnExists(c, "asr_jobs", "audio_transport"));
                Assert.True(ColumnExists(c, "asr_jobs", "tos_bucket"));
                Assert.True(ColumnExists(c, "asr_jobs", "tos_object_key"));
                Assert.True(ColumnExists(c, "asr_jobs", "tos_cleanup_pending"));
                Assert.True(ColumnExists(c, "asr_jobs", "provider_log_id"));

                // The pre-existing row survived the upgrade with its own values untouched.
                Assert.Equal("req-pre", ReadString(c, "SELECT provider_request_id FROM asr_jobs WHERE id = 'job_pre29'"));
                Assert.Equal("inline", ReadString(c, "SELECT audio_transport FROM asr_jobs WHERE id = 'job_pre29'"));
                Assert.True(ReadIsNull(c, "SELECT tos_bucket FROM asr_jobs WHERE id = 'job_pre29'"));
            }

            var upgraded = new SqliteAsrJobStore(db).Get("job_pre29")!;
            Assert.Equal("inline", upgraded.AudioTransport);
            Assert.Null(upgraded.TosBucket);
            Assert.Null(upgraded.TosObjectKey);
            Assert.False(upgraded.TosCleanupPending);
            Assert.Equal("req-pre", upgraded.ProviderRequestId);
        }
        finally
        {
            Cleanup(db);
        }
    }

    [Fact]
    public void Migrate_AudioTransportRejectsAnUnknownTransportValue()
    {
        // The column is what a reader trusts to decide whether a remote copy exists, so the
        // CHECK constraint has to reject a value the domain type cannot interpret.
        var db = NewDb();
        try
        {
            new SqliteMigrator().Migrate(db);
            using var c = Open(db);

            using var insert = new SqliteCommand(
                "INSERT INTO asr_jobs (id, session_id, source, tier, provider, input_artifact, status, " +
                "provider_request_id, audio_transport, created_at, updated_at) " +
                "VALUES ('job_bad', 'ses_1', 'import', 'standard', 'volcengine', 'audio/import/a.wav', " +
                "'pending', 'req', 's3', @now, @now)",
                c);
            insert.Parameters.AddWithValue("@now", "2026-09-15T00:00:00Z");
            Assert.ThrowsAny<SqliteException>(() => insert.ExecuteNonQuery());
        }
        finally
        {
            Cleanup(db);
        }
    }

    [Fact]
    public void ListCleanupPending_ReturnsOnlyTerminalJobsThatStillOweARelease()
    {
        // Cleanup debt is deliberately not "outstanding work": a succeeded job that still owns a
        // staged object must not make a session look unfinished, but it must still be found by
        // the cleanup pass (docs/DATA_MODEL.md section 6.2).
        var db = NewDb();
        try
        {
            new SqliteMigrator().Migrate(db);
            var store = new SqliteAsrJobStore(db);
            using var c = Open(db);

            InsertTransportJob(c, "job_succeeded_owed", status: "succeeded", pending: 1, createdMinute: 1);
            InsertTransportJob(c, "job_failed_owed", status: "failed", pending: 1, createdMinute: 2);
            InsertTransportJob(c, "job_succeeded_settled", status: "succeeded", pending: 0, createdMinute: 3);
            InsertTransportJob(c, "job_running_owed", status: "polling", pending: 1, createdMinute: 4);
            InsertTransportJob(c, "job_other_session", status: "succeeded", pending: 1, createdMinute: 5, sessionId: "ses_2");

            var all = store.ListCleanupPending(10).Select(j => j.Id).ToArray();
            Assert.Equal(new[] { "job_succeeded_owed", "job_failed_owed", "job_other_session" }, all);

            var scoped = store.ListCleanupPending(10, "ses_1").Select(j => j.Id).ToArray();
            Assert.Equal(new[] { "job_succeeded_owed", "job_failed_owed" }, scoped);

            Assert.Empty(store.ListCleanupPending(0));

            // The limit is honoured oldest-first, so a bounded cleanup pass finishes the oldest
            // debt rather than an arbitrary subset.
            Assert.Single(store.ListCleanupPending(1), j => j.Id == "job_succeeded_owed");
        }
        finally
        {
            Cleanup(db);
        }
    }

    [Fact]
    public void AudioTransportIdentity_RoundTripsThroughTheStoreWithoutTheSignedUrl()
    {
        // AC: bucket + object key are durable for crash recovery, and the presigned URL is never
        // durable state. The row is the only place a restart can learn the stable identity from,
        // so the read-back is asserted in full.
        var db = NewDb();
        try
        {
            new SqliteMigrator().Migrate(db);
            var store = new SqliteAsrJobStore(db);

            var job = new AsrJob
            {
                Id = "job_tos",
                SessionId = "ses_1",
                Source = "import",
                Provider = "volcengine",
                InputArtifact = "audio/import/normalized.wav",
                ProviderRequestId = "req-tos",
                Status = AsrJobStatus.Submitted,
                AudioTransport = "tos",
                TosBucket = "meetcap-asr",
                TosObjectKey = "meetcap-asr/deadbeefdeadbeef/2026/09/19/job_tos.wav",
                TosCleanupPending = true,
                CreatedAt = DateTimeOffset.Parse("2026-09-15T00:00:00Z"),
                UpdatedAt = DateTimeOffset.Parse("2026-09-15T00:00:00Z"),
            };

            store.Create(job);
            var read = store.Get("job_tos")!;

            Assert.Equal("tos", read.AudioTransport);
            Assert.Equal("meetcap-asr", read.TosBucket);
            Assert.Equal("meetcap-asr/deadbeefdeadbeef/2026/09/19/job_tos.wav", read.TosObjectKey);
            Assert.True(read.TosCleanupPending);

            // A released object clears only the debt: the identity stays for the audit trail.
            store.Update(AsrJobTransitions.MarkAudioReleased(read, DateTimeOffset.Parse("2026-09-15T01:00:00Z")));
            var released = store.Get("job_tos")!;
            Assert.False(released.TosCleanupPending);
            Assert.Equal("meetcap-asr/deadbeefdeadbeef/2026/09/19/job_tos.wav", released.TosObjectKey);

            // Nothing durable carries a signed URL: the only columns are the stable identity.
            using var c = Open(db);
            var ddl = ReadString(c, "SELECT sql FROM sqlite_master WHERE name = 'asr_jobs'")!;
            Assert.DoesNotContain("signature", ddl, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("presigned", ddl, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("url", ddl, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(db);
        }
    }

    private static void InsertTransportJob(
        SqliteConnection c,
        string id,
        string status,
        int pending,
        int createdMinute = 0,
        string sessionId = "ses_1")
    {
        using var cmd = new SqliteCommand(
            "INSERT INTO asr_jobs (id, session_id, source, tier, provider, input_artifact, status, " +
            "provider_request_id, audio_transport, tos_bucket, tos_object_key, tos_cleanup_pending, " +
            "created_at, updated_at) " +
            "VALUES (@id, @sid, 'import', 'standard', 'volcengine', 'audio/import/a.wav', @status, " +
            "@req, 'tos', 'meetcap-asr', @key, @pending, @now, @now)",
            c);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@req", "req-" + id);
        cmd.Parameters.AddWithValue("@key", "meetcap-asr/ab/2026/09/19/" + id + ".wav");
        cmd.Parameters.AddWithValue("@pending", pending);
        // Distinct, ordered timestamps so the oldest-first ordering is asserted rather than
        // accidentally satisfied by whatever order the rows happen to be inserted in.
        cmd.Parameters.AddWithValue("@now", $"2026-09-15T00:{createdMinute:D2}:00Z");
        cmd.ExecuteNonQuery();
    }

    /// <summary>Forgets one migration's version row, which is how a replay is reached.</summary>
    private static void ForgetAsrJobMigration(SqliteConnection c, string resourceSuffix)
    {
        var version = new SqliteMigrator().GetMigrations()
            .Single(m => m.ResourceName.EndsWith(resourceSuffix, StringComparison.Ordinal))
            .Version;

        using var forget = new SqliteCommand("DELETE FROM schema_migrations WHERE version = @v", c);
        forget.Parameters.AddWithValue("@v", version);
        forget.ExecuteNonQuery();
    }

    [Fact]
    public void Migrate_ReRunOfProviderLogIdMigration_PreservesTheStoredProviderLogId()
    {
        // The migrator relies on every script being re-runnable, because a second process can
        // read schema_migrations before this version is recorded. A bare ALTER TABLE would fail
        // with "duplicate column name" here; the rebuild has to be a no-op instead.
        //
        // "A no-op" includes the rows: between the first run's commit and this replay's
        // DROP TABLE the rest of MeetCap can write real X-Tt-Logid values into
        // provider_log_id, and the rebuild used to reset every one of them to NULL. Asserting
        // only "does not throw" plus column existence is what let that loss ship (review
        // finding P1 on PR #28), so this test writes values, replays, and re-reads them —
        // per row, so a single value smeared across the table would fail too.
        var db = NewDb();
        try
        {
            var migrator = new SqliteMigrator();
            migrator.Migrate(db);

            using var c = Open(db);

            // Both rows exist only after the first run, which is exactly the window the
            // replay is able to destroy.
            InsertAsrJob(c, "job_without_log_id");
            InsertAsrJob(c, "job_with_log_id", providerLogId: "LOGID-REAL");

            ForgetProviderLogIdMigration(c);
            migrator.Migrate(db); // the replay

            Assert.True(ColumnExists(c, "asr_jobs", "provider_log_id"));
            Assert.True(TableExists(c, "asr_jobs"));
            Assert.False(TableExists(c, "asr_jobs_new"));
            Assert.True(ReadIsNull(c, "SELECT provider_log_id FROM asr_jobs WHERE id = 'job_without_log_id'"));
            Assert.Equal(
                "LOGID-REAL",
                ReadString(c, "SELECT provider_log_id FROM asr_jobs WHERE id = 'job_with_log_id'"));
        }
        finally
        {
            Cleanup(db);
        }
    }

    [Fact]
    public void ResolveColumnPlaceholders_ExpandsToTheColumnWhenPresentAndToNullWhenAbsent()
    {
        // SQLite cannot branch on column presence, and naming an absent column fails while the
        // statement is prepared, so a re-runnable migration cannot express "carry this column
        // if it is already there" in SQL. The placeholder is that branch: a re-run expands it
        // to the stored column, a first run to NULL.
        var schema = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["asr_jobs"] = new[] { "id", "provider_log_id" },
            ["asr_jobs_old"] = new[] { "id" },
        };
        IReadOnlyCollection<string>? ColumnsOf(string table) =>
            schema.TryGetValue(table, out var columns) ? columns : null;

        Assert.Equal(
            "SELECT id, provider_log_id FROM asr_jobs",
            SqliteMigrator.ResolveColumnPlaceholders(
                "SELECT id, {{asr_jobs.provider_log_id}} FROM asr_jobs",
                ColumnsOf));

        Assert.Equal(
            "SELECT id, NULL FROM asr_jobs_old",
            SqliteMigrator.ResolveColumnPlaceholders(
                "SELECT id, {{asr_jobs_old.provider_log_id}} FROM asr_jobs_old",
                ColumnsOf));

        // SQLite identifier resolution is case-insensitive, so the table lookup is too.
        Assert.Equal(
            "SELECT id, provider_log_id FROM asr_jobs",
            SqliteMigrator.ResolveColumnPlaceholders(
                "SELECT id, {{ASR_JOBS.PROVIDER_LOG_ID}} FROM asr_jobs",
                ColumnsOf));
    }

    [Fact]
    public void ResolveColumnPlaceholders_LeavesAnUnknownTableUntouchedSoAMistakeStaysLoud()
    {
        // Two things must not happen here. A comment that quotes the syntax has to stay inert,
        // and a mistyped table must reach SQLite unexpanded — expanding it to NULL would drop
        // the value the migration exists to carry, silently.
        const string sql = "-- {{asr_jobz.provider_log_id}} is resolved by SqliteMigrator\n" +
                           "SELECT id, {{asr_jobz.provider_log_id}} FROM asr_jobs";

        Assert.Equal(sql, SqliteMigrator.ResolveColumnPlaceholders(sql, _ => null));
    }

    [Fact]
    public void ResolveColumnPlaceholders_AMistypedColumnOnAnExistingTableBecomesNull()
    {
        // The asymmetry docs/DATA_MODEL.md section 14 and the migrator remarks now state: a
        // placeholder for a column the schema does not have expands to NULL, because that is
        // also the correct first-run value of the column the migration itself is adding. A
        // mistyped *column* on an existing table is therefore indistinguishable from that
        // legitimate case and is NOT loud — only a mistyped table is. Spelling the column
        // correctly is what the script author has to get right.
        IReadOnlyCollection<string>? ColumnsOf(string table) =>
            string.Equals(table, "asr_jobs", StringComparison.OrdinalIgnoreCase) ? new[] { "id" } : null;

        Assert.Equal(
            "SELECT id, NULL FROM asr_jobs",
            SqliteMigrator.ResolveColumnPlaceholders(
                "SELECT id, {{asr_jobs.provider_log_idd}} FROM asr_jobs",
                ColumnsOf));
    }

    [Fact]
    public void UnexpandedPlaceholder_ReachesSqliteAndFailsLoudlyInsteadOfCopyingNull()
    {
        // This is the link the pass-through assertion above cannot reach: what makes a mistyped
        // table loud is that the placeholder reaches SQLite exactly as written and the statement
        // then fails. Asserted against a real database, because that failure — rather than a
        // silent NULL — is what stops a mistake in a migration from dropping the value the
        // migration exists to carry.
        var db = NewDb();
        try
        {
            new SqliteMigrator().Migrate(db);
            using var c = Open(db);

            var failure = Assert.Throws<SqliteException>(() =>
            {
                using var cmd = new SqliteCommand("SELECT {{asr_jobz.provider_log_id}} FROM asr_jobs", c);
                cmd.ExecuteNonQuery();
            });

            // The placeholder itself is what SQLite rejects, which is why the statement cannot
            // quietly degrade into a NULL copy.
            Assert.Contains("{", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(db);
        }
    }

    private static void InsertAsrJob(SqliteConnection c, string id, string? providerLogId = null)
    {
        using (var cmd = new SqliteCommand(
                   "INSERT INTO asr_jobs (id, session_id, source, tier, provider, input_artifact, status, " +
                   "provider_request_id, created_at, updated_at) " +
                   "VALUES (@id, 'ses_1', 'import', 'standard', 'volcengine', 'audio/import/a.wav', " +
                   "'pending', @req, @now, @now)",
                   c))
        {
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@req", "req-" + id);
            cmd.Parameters.AddWithValue("@now", "2026-09-15T00:00:00Z");
            cmd.ExecuteNonQuery();
        }

        if (providerLogId is not null)
        {
            using var set = new SqliteCommand(
                "UPDATE asr_jobs SET provider_log_id = @logId WHERE id = @id", c);
            set.Parameters.AddWithValue("@logId", providerLogId);
            set.Parameters.AddWithValue("@id", id);
            set.ExecuteNonQuery();
        }
    }

    private static void ForgetProviderLogIdMigration(SqliteConnection c)
    {
        using var forget = new SqliteCommand("DELETE FROM schema_migrations WHERE version = @v", c);
        forget.Parameters.AddWithValue("@v", ProviderLogIdMigrationVersion());
        forget.ExecuteNonQuery();
    }

    private static string? ReadString(SqliteConnection c, string sql)
    {
        using var cmd = new SqliteCommand(sql, c);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    private static bool ReadIsNull(SqliteConnection c, string sql)
    {
        using var cmd = new SqliteCommand(sql, c);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull;
    }

    private static int ProviderLogIdMigrationVersion() =>
        new SqliteMigrator().GetMigrations()
            .Single(m => m.ResourceName.EndsWith("0005_asr_job_provider_log_id.sql", StringComparison.Ordinal))
            .Version;

    private static bool ColumnExists(SqliteConnection c, string table, string column)
    {
        using var cmd = new SqliteCommand($"PRAGMA table_info({table})", c);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void ParseMigrations_OrdersByVersionAndIgnoresNonMigrationResources()
    {
        var migrations = SqliteMigrator.ParseMigrations(new[]
        {
            "MeetCap.Persistence.Storage.StorageMarker",
            "MeetCap.Persistence.Migrations.0003_asr_jobs.sql",
            "MeetCap.Persistence.Migrations.0001_sessions.sql",
            "MeetCap.Persistence.Migrations.readme.md",
        });

        Assert.Equal(new[] { 1, 3 }, migrations.Select(m => m.Version));
        Assert.Equal(
            new[]
            {
                "MeetCap.Persistence.Migrations.0001_sessions.sql",
                "MeetCap.Persistence.Migrations.0003_asr_jobs.sql",
            },
            migrations.Select(m => m.ResourceName));
    }

    [Fact]
    public void ParseMigrations_ThrowsForAnUnnumberedMigrationInsteadOfDroppingIt()
    {
        // The silent-drop hole: a migration whose name carries no version used to be filtered
        // out of the list entirely, so it was never applied and no error was raised.
        var ex = Assert.Throws<InvalidOperationException>(() => SqliteMigrator.ParseMigrations(new[]
        {
            "MeetCap.Persistence.Migrations.0001_sessions.sql",
            "MeetCap.Persistence.Migrations.asr_jobs.sql",
        }));

        Assert.Contains("MeetCap.Persistence.Migrations.asr_jobs.sql", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no parseable version", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseMigrations_ThrowsForANameWhoseVersionSegmentHasNoLeadingDigits()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => SqliteMigrator.ParseMigrations(new[]
        {
            "MeetCap.Persistence.Migrations.v0004_asr_jobs.sql",
        }));

        Assert.Contains("v0004_asr_jobs.sql", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseMigrations_ThrowsForTwoScriptsClaimingOneVersion()
    {
        // Drives the guard's throwing path directly, which the shipped embedded set cannot do.
        var ex = Assert.Throws<InvalidOperationException>(() => SqliteMigrator.ParseMigrations(new[]
        {
            "MeetCap.Persistence.Migrations.0002_audio_chunks.sql",
            "MeetCap.Persistence.Migrations.0002_asr_jobs.sql",
        }));

        Assert.Contains("Migration version 2 is claimed by more than one script", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0002_audio_chunks.sql", ex.Message, StringComparison.Ordinal);
        Assert.Contains("0002_asr_jobs.sql", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetMigrations_NeverDropsAnEmbeddedSqlScript()
    {
        // Belt and braces: every embedded *.sql resource under Migrations must appear in the
        // applied set. If this ever fails, a migration is silently not running.
        var assembly = typeof(SqliteMigrator).Assembly;
        var embedded = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.Ordinal)
                && n.Contains("Migrations", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        var resolved = new SqliteMigrator().GetMigrations()
            .Select(m => m.ResourceName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(embedded, resolved);
    }

    [Fact]
    public void Migrate_CreatesAsrJobsTableWithStatusCheckConstraint()
    {
        var db = NewDb();
        try
        {
            new SqliteMigrator().Migrate(db);
            using var c = Open(db);
            Assert.True(TableExists(c, "asr_jobs"));

            using var cmd = new SqliteCommand(
                "INSERT INTO asr_jobs (id, session_id, source, tier, provider, input_artifact, status, " +
                "provider_request_id, created_at, updated_at) " +
                "VALUES ('job_1', 'ses_1', 'import', 'standard', 'volcengine', 'audio/import/a.wav', " +
                "'not_a_status', 'req', @now, @now)", c);
            cmd.Parameters.AddWithValue("@now", "2026-09-15T00:00:00Z");
            Assert.ThrowsAny<SqliteException>(() => cmd.ExecuteNonQuery());
        }
        finally
        {
            Cleanup(db);
        }
    }

    private static IReadOnlyList<int> ExpectedVersions() =>
        new SqliteMigrator().GetMigrations().Select(m => m.Version).OrderBy(v => v).ToArray();

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
            Assert.Equal(ExpectedVersions().Count, Count(c, "SELECT COUNT(*) FROM schema_migrations"));
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
            Assert.True(TableExists(c, "asr_jobs"));
            Assert.Equal(ExpectedVersions().Count, Count(c, "SELECT COUNT(*) FROM schema_migrations"));
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
            // already-applied versions instead of failing on the primary key.
            new SqliteMigrator().Migrate(db);

            using var c = Open(db);
            Assert.Equal(ExpectedVersions().Count, Count(c, "SELECT COUNT(*) FROM schema_migrations"));
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
