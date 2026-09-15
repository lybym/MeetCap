namespace MeetCap.Persistence.Storage;

using MeetCap.Core.Asr;
using MeetCap.Core.Sessions;
using Microsoft.Data.Sqlite;

/// <summary>
/// Facade over the MeetCap SQLite database (<c>meetcap.db</c> under the configured
/// data root). Bootstraps the schema and exposes the repositories that index
/// session and ASR job state.
/// </summary>
public sealed class MeetCapDatabase
{
    public MeetCapDatabase(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        DatabasePath = dbPath;
    }

    /// <summary>Absolute path of the SQLite database file.</summary>
    public string DatabasePath { get; }

    /// <summary>Sessions indexed by <c>MeetCap.Persistence</c> (docs/DATA_MODEL.md section 1).</summary>
    public ISessionStore Sessions => new SqliteSessionStore(DatabasePath);

    /// <summary>The persistent ASR job queue (docs/ARCHITECTURE.md section 12).</summary>
    public IAsrJobStore AsrJobs => new SqliteAsrJobStore(DatabasePath);

    /// <summary>True when the database file exists and has been migrated at least once.</summary>
    public bool IsInitialized()
    {
        if (!File.Exists(DatabasePath))
        {
            return false;
        }

        try
        {
            using var conn = SqliteConnectionFactory.Open(DatabasePath);
            return SqliteConnectionFactory.TableExists(conn, "schema_migrations");
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    /// <summary>Creates the database and applies all pending migrations.</summary>
    public void EnsureMigrated() => new SqliteMigrator().Migrate(DatabasePath);

    /// <summary>Count of sessions in a non-terminal state. Zero before any session exists.</summary>
    public int CountActiveSessions()
    {
        if (!IsInitialized())
        {
            return 0;
        }

        using var conn = SqliteConnectionFactory.Open(DatabasePath);
        if (!SqliteConnectionFactory.TableExists(conn, "sessions"))
        {
            return 0;
        }

        using var cmd = new SqliteCommand(
            "SELECT COUNT(*) FROM sessions WHERE status IN (@s1, @s2, @s3, @s4)",
            conn);
        cmd.Parameters.AddWithValue("@s1", SessionStatus.Created);
        cmd.Parameters.AddWithValue("@s2", SessionStatus.Recording);
        cmd.Parameters.AddWithValue("@s3", SessionStatus.Finalizing);
        cmd.Parameters.AddWithValue("@s4", SessionStatus.Processing);
        var result = cmd.ExecuteScalar();
        return result is long l ? (int)l : 0;
    }
}
