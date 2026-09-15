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
public sealed class SqliteMigrator
{
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

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Local-first CLI: short-lived per-command access. Disabling pooling keeps
            // the database file reliably releasable (inspectable local artifacts).
            Pooling = false,
        }.ToString();

        using var conn = new SqliteConnection(cs);
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
        using var cmd = new SqliteCommand(
            "INSERT INTO schema_migrations (version, applied_at) VALUES (@version, @at)",
            conn,
            tx);
        cmd.Parameters.AddWithValue("@version", version);
        cmd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }
}
