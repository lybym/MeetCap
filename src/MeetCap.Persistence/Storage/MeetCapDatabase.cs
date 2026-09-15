namespace MeetCap.Persistence.Storage;

using MeetCap.Core.Sessions;
using Microsoft.Data.Sqlite;

/// <summary>
/// Facade over the MeetCap SQLite database (<c>meetcap.db</c> under the configured
/// data root). Owns migration bootstrap and hands out the repositories; it holds no
/// business rules of its own.
/// </summary>
public sealed class MeetCapDatabase
{
    private readonly string _dbPath;

    public MeetCapDatabase(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _dbPath = dbPath;
        Sessions = new SessionRepository(this);
        Chunks = new AudioChunkRepository(this);
    }

    /// <summary>Absolute path of the database file.</summary>
    public string DbPath => _dbPath;

    /// <summary>Sessions index (docs/DATA_MODEL.md section 1).</summary>
    public SessionRepository Sessions { get; }

    /// <summary>Audio chunk index (docs/DATA_MODEL.md section 5).</summary>
    public AudioChunkRepository Chunks { get; }

    /// <summary>True when the database file exists and has been migrated at least once.</summary>
    public bool IsInitialized()
    {
        if (!File.Exists(_dbPath))
        {
            return false;
        }

        try
        {
            using var conn = Open();
            return TableExists(conn, "schema_migrations");
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>Creates the database and applies all pending migrations.</summary>
    public void EnsureMigrated() => new SqliteMigrator().Migrate(_dbPath);

    /// <summary>
    /// Count of sessions in a non-terminal state. Zero before any session exists.
    /// </summary>
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
            $"SELECT COUNT(*) FROM sessions WHERE status IN ({SqlList.Placeholders(SessionStatus.Active.Count)})",
            conn);
        SqlListParameters.Add(cmd, SessionStatus.Active);
        var result = cmd.ExecuteScalar();
        return result is long l ? (int)l : 0;
    }

    /// <summary>
    /// Opens a short-lived connection. MeetCap is a local single-user CLI, so
    /// pooling stays disabled and each command owns its connection.
    /// </summary>
    internal SqliteConnection Open()
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 30,
        }.ToString();

        var conn = new SqliteConnection(cs);
        conn.Open();

        // audio_chunks references sessions; enforce it so the index cannot drift into
        // orphan rows. SQLite defaults to off, so it must be set per connection.
        using var pragma = new SqliteCommand("PRAGMA foreign_keys = ON", conn);
        pragma.ExecuteNonQuery();

        return conn;
    }

    internal static bool TableExists(SqliteConnection conn, string name)
    {
        using var cmd = new SqliteCommand(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name",
            conn);
        cmd.Parameters.AddWithValue("@name", name);
        var result = cmd.ExecuteScalar();
        return result is long l && l > 0;
    }
}
