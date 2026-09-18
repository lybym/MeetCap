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
/// <para>
/// Because a re-run can happen, every script must be re-runnable *and* must leave
/// existing data untouched. When a script has to read a column that only some
/// schemas have — the usual case for a migration whose own job is to add that
/// column — it cannot say so in SQL: SQLite cannot branch on column presence, and a
/// statement naming an absent column fails while it is being prepared. Such a script
/// uses the placeholder below instead.
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

            // IMMEDIATE, not SQLite's default deferred transaction: the script's
            // schema-conditional placeholders are resolved before the script runs, and
            // that answer is only the schema the script is about to change if no other
            // migrator can commit between the read and this transaction's own write
            // lock. Taking the write lock up front makes the read and the write one
            // serialized step, so a concurrent migrator's committed rebuild is seen
            // rather than missed. SQLite's own busy timeout (BusyTimeoutSeconds) is
            // what a second process waits on; no lock of this layer's own is involved.
            using var tx = conn.BeginTransaction(deferred: false);
            var resolved = ResolveColumnPlaceholders(conn, tx, sql);
            Execute(conn, tx, resolved);
            RecordApplied(conn, tx, version);
            tx.Commit();

            // Keep the in-memory view consistent for later iterations.
            applied.Add(version);
        }
    }

    /// <summary>The migration resources bundled with this assembly, ordered by version.</summary>
    internal IReadOnlyList<(int Version, string ResourceName)> GetMigrations() =>
        ParseMigrations(_assembly.GetManifestResourceNames());

    /// <summary>
    /// Resolves the numbered migration scripts from a set of embedded resource names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every embedded <c>*.sql</c> resource under <c>Migrations</c> must carry a parseable
    /// version, and no two may claim the same one. Both violations are rejected loudly
    /// rather than filtered away, because either one makes a migration silently not run:
    /// versions are keyed in <c>schema_migrations(version INTEGER PRIMARY KEY)</c>, so a
    /// duplicate would look already-applied, and an unnumbered script would never be
    /// considered at all. A silently missing table is exactly the schema corruption this
    /// guard exists to prevent.
    /// </para>
    /// <para>
    /// This is a pure function over resource names so the throwing paths are directly
    /// testable without shipping a deliberately broken assembly.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<(int Version, string ResourceName)> ParseMigrations(
        IEnumerable<string> resourceNames)
    {
        ArgumentNullException.ThrowIfNull(resourceNames);

        var migrations = new List<(int Version, string ResourceName)>();
        foreach (var name in resourceNames)
        {
            if (!name.EndsWith(".sql", StringComparison.Ordinal)
                || !name.Contains("Migrations", StringComparison.Ordinal))
            {
                continue;
            }

            var version = TryParseVersion(name);
            if (version is null)
            {
                throw new InvalidOperationException(
                    $"Embedded migration '{name}' has no parseable version. Migration resource names " +
                    "must contain 'Migrations.<number>' (for example '0003_asr_jobs.sql'), otherwise the " +
                    "script would never be applied and its tables would never be created.");
            }

            migrations.Add((version.Value, name));
        }

        migrations.Sort((left, right) =>
        {
            var byVersion = left.Version.CompareTo(right.Version);
            return byVersion != 0
                ? byVersion
                : string.CompareOrdinal(left.ResourceName, right.ResourceName);
        });

        var duplicate = migrations
            .GroupBy(t => t.Version)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Migration version {duplicate.Key} is claimed by more than one script: " +
                string.Join(", ", duplicate.Select(t => t.ResourceName)) +
                ". Rename one of them to the next free version so that every migration is applied.");
        }

        return migrations;
    }

    /// <summary>
    /// The schema-conditional column placeholder a migration script may use:
    /// <c>{{table.column}}</c>.
    /// </summary>
    /// <remarks>
    /// A re-runnable migration that adds a column often also has to copy that column's
    /// existing values across a table rebuild — but only on a re-run, because on the
    /// first run the column does not exist yet. SQL cannot express that choice, so the
    /// placeholder does: it expands to the column reference when the column is present
    /// and to <c>NULL</c> when it is not, which is exactly the value a column this
    /// migration is adding would have on a first run.
    /// </remarks>
    private static readonly Regex ColumnPlaceholder = new(
        @"\{\{(?<table>[A-Za-z_][A-Za-z0-9_]*)\.(?<column>[A-Za-z_][A-Za-z0-9_]*)\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Expands the <c>{{table.column}}</c> placeholders in a migration script against a
    /// caller-supplied view of the schema.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A placeholder for a column that exists expands to the column name; one for a
    /// column that does not expands to <c>NULL</c>, which is the value a column this
    /// migration is adding has on a first run.
    /// </para>
    /// <para>
    /// A placeholder whose table is absent from the schema is left exactly as written,
    /// deliberately. That keeps a comment that quotes the syntax inert, and it keeps a
    /// mistyped table name loud: the script then reaches SQLite unexpanded and fails to
    /// prepare, rather than quietly expanding to <c>NULL</c> and dropping the value the
    /// migration exists to carry.
    /// </para>
    /// <para>
    /// This is a pure function over the schema so both paths are directly testable
    /// without a database.
    /// </para>
    /// </remarks>
    internal static string ResolveColumnPlaceholders(
        string sql,
        Func<string, IReadOnlyCollection<string>?> columnsOf)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(columnsOf);

        return ColumnPlaceholder.Replace(sql, match =>
        {
            var table = match.Groups["table"].Value;
            var column = match.Groups["column"].Value;
            var columns = columnsOf(table);
            if (columns is null)
            {
                return match.Value;
            }

            // Emit the name the schema declares rather than the placeholder's own spelling,
            // so the statement SQLite prepares names the real column whichever case the
            // migration author wrote.
            return columns.FirstOrDefault(
                name => string.Equals(name, column, StringComparison.OrdinalIgnoreCase)) ?? "NULL";
        });
    }

    /// <summary>
    /// Expands a pending migration's placeholders against the live schema, inside the
    /// transaction that is about to run it.
    /// </summary>
    private static string ResolveColumnPlaceholders(
        SqliteConnection conn,
        SqliteTransaction tx,
        string sql) =>
        ResolveColumnPlaceholders(sql, table => ColumnsOf(conn, tx, table));

    /// <summary>
    /// The columns of <paramref name="table"/>, or <see langword="null"/> when the schema has
    /// no such table.
    /// </summary>
    /// <remarks>
    /// <c>pragma_table_info</c> is queried as a table-valued function rather than through
    /// <c>sqlite_master</c> on purpose: it resolves its argument the way SQLite resolves an
    /// identifier, so the lookup is case-insensitive and matches what the script itself will
    /// see. A table always has at least one column, so an empty result means "no such table".
    /// </remarks>
    private static IReadOnlyCollection<string>? ColumnsOf(
        SqliteConnection conn,
        SqliteTransaction tx,
        string table)
    {
        var columns = new List<string>();
        using (var cmd = new SqliteCommand("SELECT name FROM pragma_table_info(@table)", conn, tx))
        {
            cmd.Parameters.AddWithValue("@table", table);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(reader.GetString(0));
            }
        }

        return columns.Count > 0 ? columns : null;
    }

    /// <summary>
    /// The schema versions embedded in this build, ascending. Exposed so tests and
    /// diagnostics can assert "every embedded migration is applied exactly once"
    /// without hard-coding the current count as migrations are added.
    /// </summary>
    public IReadOnlyList<int> SupportedVersions
        => GetMigrations().Select(m => m.Version).ToList();

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
