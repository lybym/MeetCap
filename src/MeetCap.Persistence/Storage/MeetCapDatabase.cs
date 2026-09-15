namespace MeetCap.Persistence.Storage;

using Microsoft.Data.Sqlite;

/// <summary>
/// Facade over the MeetCap SQLite database (<c>meetcap.db</c> under the configured
/// data root). M0 only needs to bootstrap schema and answer "is a session active?";
/// richer repositories arrive with later milestones.
/// </summary>
public sealed class MeetCapDatabase
{
    private readonly string _dbPath;

    public MeetCapDatabase(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _dbPath = dbPath;
    }

    /// <summary>
    /// True when the database file exists and every migration embedded in this
    /// version of MeetCap has been committed.
    /// </summary>
    public bool IsInitialized()
    {
        if (!File.Exists(_dbPath))
        {
            return false;
        }

        try
        {
            using var conn = Open();
            if (!TableExists(conn, "schema_migrations"))
            {
                return false;
            }

            var requiredVersions = new SqliteMigrator()
                .GetMigrations()
                .Select(migration => migration.Version);
            var appliedVersions = GetAppliedMigrationVersions(conn);
            return requiredVersions.All(appliedVersions.Contains);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>Creates the database and applies all pending migrations.</summary>
    public void EnsureMigrated() => new SqliteMigrator().Migrate(_dbPath);

    /// <summary>Count of sessions in a non-terminal state. Zero before any session exists.</summary>
    public int CountActiveSessions()
    {
        if (!IsInitialized())
        {
            return 0;
        }

        using var conn = Open();
        if (!TableExists(conn, "sessions"))
        {
            return 0;
        }

        using var cmd = new SqliteCommand(
            "SELECT COUNT(*) FROM sessions WHERE status IN (@s1, @s2, @s3, @s4)",
            conn);
        cmd.Parameters.AddWithValue("@s1", "CREATED");
        cmd.Parameters.AddWithValue("@s2", "RECORDING");
        cmd.Parameters.AddWithValue("@s3", "FINALIZING");
        cmd.Parameters.AddWithValue("@s4", "PROCESSING");
        var result = cmd.ExecuteScalar();
        return result is long l ? (int)l : 0;
    }

    private SqliteConnection Open()
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = _dbPath, Pooling = false }.ToString();
        var conn = new SqliteConnection(cs);
        conn.Open();
        return conn;
    }

    private static bool TableExists(SqliteConnection conn, string name)
    {
        using var cmd = new SqliteCommand(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name",
            conn);
        cmd.Parameters.AddWithValue("@name", name);
        var result = cmd.ExecuteScalar();
        return result is long l && l > 0;
    }

    private static HashSet<int> GetAppliedMigrationVersions(SqliteConnection conn)
    {
        var versions = new HashSet<int>();
        using var cmd = new SqliteCommand("SELECT version FROM schema_migrations", conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            versions.Add(reader.GetInt32(0));
        }

        return versions;
    }
}
