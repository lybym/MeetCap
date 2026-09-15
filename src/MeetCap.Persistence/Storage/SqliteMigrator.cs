namespace MeetCap.Persistence.Storage;

using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

/// <summary>
/// Applies numbered, embedded SQL migration scripts to a SQLite database.
/// Migrations are idempotent at the table level (IF NOT EXISTS) and tracked in
/// a <c>schema_migrations</c> table. Only foundational tables required by the
/// active milestone are created; later milestones add their own migrations.
/// </summary>
/// <remarks>
/// Two processes can start the CLI at the same time against the same data root.
/// Each migration runs in its own transaction so DDL and its version row commit
/// together, and the whole migration sequence additionally runs under a
/// file-system lock (<c>&lt;database&gt;.migration.lock</c>) so concurrent
/// first-run migrations serialize instead of racing the <c>schema_migrations</c>
/// primary key. A caller that loses the race re-reads the applied set after
/// acquiring the lock and simply observes the winner's work as already applied.
/// </remarks>
public sealed class SqliteMigrator
{
    /// <summary>How long a migration waits for a competing migrator before failing.</summary>
    internal const int BusyTimeoutSeconds = 30;

    private const int LockRetryDelayMilliseconds = 10;

    private readonly Assembly _assembly = typeof(SqliteMigrator).Assembly;

    /// <summary>Creates a migrator for the database at <paramref name="dbPath"/>.</summary>
    public SqliteMigrator(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        DbPath = dbPath;
    }

    /// <summary>The database file this migrator was created for.</summary>
    public string DbPath { get; }

    /// <summary>
    /// Creates the database (if absent) and applies all pending migrations.
    /// Safe to call concurrently from multiple processes.
    /// </summary>
    public void Migrate(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        // The lock file lives beside the database, so the directory must exist before
        // the lock can be created (a clean data directory is the normal first run).
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var migrationLock = AcquireMigrationLock(dbPath);

        using var conn = new SqliteConnection(BuildConnectionString(dbPath));
        conn.Open();

        // A competing migrator may have applied migrations while this caller waited
        // for the lock, so the applied set is read only once the lock is held.
        EnsureMigrationsTable(conn);
        var applied = GetAppliedVersions(conn);
        foreach (var (version, resourceName) in GetMigrations())
        {
            if (applied.Contains(version))
            {
                continue;
            }

            var sql = ReadResource(resourceName);
            using var tx = conn.BeginTransaction();
            Execute(conn, tx, sql);
            RecordApplied(conn, tx, version);
            tx.Commit();

            // Keep the in-memory view consistent for later iterations.
            applied.Add(version);
        }
    }

    /// <summary>The migration resources bundled with this assembly, ordered by version.</summary>
    internal IReadOnlyList<(int Version, string ResourceName)> GetMigrations()
    {
        var names = _assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.Ordinal) && n.Contains("Migrations", StringComparison.Ordinal))
            .Select(n => (Version: TryParseVersion(n), ResourceName: n))
            .Where(t => t.Version.HasValue)
            .Select(t => (t.Version!.Value, t.ResourceName))
            .OrderBy(t => t.Value)
            .ToList();
        return names;
    }

    private static string BuildConnectionString(string dbPath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Local-first CLI: short-lived per-command access. Disabling pooling keeps
            // the database file reliably releasable (inspectable local artifacts).
            Pooling = false,
            DefaultTimeout = BusyTimeoutSeconds,
        }.ToString();

    /// <summary>
    /// Acquires the cross-process migration lock, waiting up to
    /// <see cref="BusyTimeoutSeconds"/> for a competing migrator to finish. The
    /// returned stream owns the lock for the duration of the migration sequence.
    /// </summary>
    internal static FileStream AcquireMigrationLock(string dbPath)
    {
        var lockPath = dbPath + ".migration.lock";
        var stopwatch = Stopwatch.StartNew();
        var delay = LockRetryDelayMilliseconds;

        while (true)
        {
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException) when (stopwatch.Elapsed < TimeSpan.FromSeconds(BusyTimeoutSeconds))
            {
                // Another migrator holds the lock; back off and retry until the budget
                // is exhausted, then let the failure surface with an actionable error.
                Thread.Sleep(delay);
                delay = Math.Min(delay * 2, 250);
            }
        }
    }

    private static int? TryParseVersion(string resourceName)
    {
        var match = Regex.Match(resourceName, @"Migrations\.(\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out var v) ? v : null;
    }

    private static void EnsureMigrationsTable(SqliteConnection conn)
    {
        const string sql =
            "CREATE TABLE IF NOT EXISTS schema_migrations (" +
            "version INTEGER PRIMARY KEY, " +
            "applied_at TEXT NOT NULL)";
        using var cmd = new SqliteCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }

    private static HashSet<int> GetAppliedVersions(SqliteConnection conn)
    {
        var applied = new HashSet<int>();
        using var cmd = new SqliteCommand("SELECT version FROM schema_migrations", conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            applied.Add(reader.GetInt32(0));
        }

        return applied;
    }

    private string ReadResource(string resourceName)
    {
        using var stream = _assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded migration resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void Execute(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using var cmd = new SqliteCommand(sql, conn, tx);
        cmd.ExecuteNonQuery();
    }

    private static void RecordApplied(SqliteConnection conn, SqliteTransaction tx, int version)
    {
        // Insert-or-ignore keeps the version row idempotent even if the lock is ever
        // bypassed (for example by an older migrator): the schema work is
        // IF NOT EXISTS, so the first recorded row remains authoritative.
        using var cmd = new SqliteCommand(
            "INSERT OR IGNORE INTO schema_migrations (version, applied_at) VALUES (@version, @at)",
            conn,
            tx);
        cmd.Parameters.AddWithValue("@version", version);
        cmd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }
}
