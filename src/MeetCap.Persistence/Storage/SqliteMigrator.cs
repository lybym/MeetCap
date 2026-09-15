namespace MeetCap.Persistence.Storage;

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
/// This is deliberately the "equivalently thin migration layer" allowed by
/// docs/ARCHITECTURE.md section 2: one embedded SQL script per version, applied in
/// its own transaction so the DDL and its version row commit together. It
/// introduces no coordination protocol, no persistent bookkeeping artifact, and
/// no timeout semantics — docs/DEVELOPMENT.md section 3 requires an explicitly
/// authorized reliability requirement before adding machinery of that kind.
/// <para>
/// Concurrency safety therefore comes only from the database contract itself:
/// migration SQL is guarded by <c>IF NOT EXISTS</c> and the version row is
/// recorded with <c>INSERT OR IGNORE</c>, so a version that another process
/// recorded first is treated as an already-applied migration rather than a
/// primary-key failure. SQLite's own write lock ordering is left to SQLite.
/// </para>
/// </remarks>
public sealed class SqliteMigrator
{
    /// <summary>
    /// How long a migration waits on SQLite's own write lock before failing.
    /// </summary>
    internal const int BusyTimeoutSeconds = 30;

    private readonly Assembly _assembly = typeof(SqliteMigrator).Assembly;

    /// <summary>
    /// Creates the database (if absent) and applies all pending migrations.
    /// </summary>
    public void Migrate(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var conn = new SqliteConnection(BuildConnectionString(dbPath));
        conn.Open();

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
    /// <remarks>
    /// Versions are keyed in <c>schema_migrations(version INTEGER PRIMARY KEY)</c>, so
    /// two scripts claiming the same number would make the second one silently look
    /// already-applied and its tables would never be created. That is a silent schema
    /// corruption, so a duplicate version is rejected loudly instead.
    /// </remarks>
    internal IReadOnlyList<(int Version, string ResourceName)> GetMigrations()
    {
        var names = _assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.Ordinal) && n.Contains("Migrations", StringComparison.Ordinal))
            .Select(n => (Version: TryParseVersion(n), ResourceName: n))
            .Where(t => t.Version.HasValue)
            .Select(t => (t.Version!.Value, t.ResourceName))
            .OrderBy(t => t.Value, Comparer<int>.Default)
            .ThenBy(t => t.ResourceName, StringComparer.Ordinal)
            .ToList();

        var duplicate = names
            .GroupBy(t => t.Value)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Migration version {duplicate.Key} is claimed by more than one script: " +
                string.Join(", ", duplicate.Select(t => t.ResourceName)) +
                ". Rename one of them to the next free version so that every migration is applied.");
        }

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
            // Bounded wait on SQLite's own lock, so a second process migrating the
            // same clean database queues behind the first instead of failing fast.
            DefaultTimeout = BusyTimeoutSeconds,
        }.ToString();

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
        // INSERT OR IGNORE, not INSERT: if another process recorded this version
        // after our applied-set read, the migration is still complete and the
        // already-recorded row is authoritative. This is what keeps the thin
        // migration layer free of any cross-process lock protocol.
        using var cmd = new SqliteCommand(
            "INSERT OR IGNORE INTO schema_migrations (version, applied_at) VALUES (@version, @at)",
            conn,
            tx);
        cmd.Parameters.AddWithValue("@version", version);
        cmd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }
}
