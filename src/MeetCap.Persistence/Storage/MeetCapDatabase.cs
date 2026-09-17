namespace MeetCap.Persistence.Storage;

using MeetCap.Core.Asr;
using MeetCap.Core.Sessions;
using MeetCap.Core.Speakers;
using Microsoft.Data.Sqlite;

/// <summary>
/// Facade over the MeetCap SQLite database (<c>meetcap.db</c> under the configured
/// data root). Bootstraps the schema and hands out the repositories that index session,
/// audio-chunk, and ASR-job state; it holds no business rules of its own.
/// </summary>
public sealed class MeetCapDatabase
{
    public MeetCapDatabase(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        DatabasePath = dbPath;
        Sessions = new SessionRepository(this);
        Chunks = new AudioChunkRepository(this);
    }

    /// <summary>Absolute path of the SQLite database file.</summary>
    public string DatabasePath { get; }

    /// <summary>Sessions index (docs/DATA_MODEL.md section 1).</summary>
    public SessionRepository Sessions { get; }

    /// <summary>Audio chunk index (docs/DATA_MODEL.md section 5).</summary>
    public AudioChunkRepository Chunks { get; }

    /// <summary>The persistent ASR job queue (docs/ARCHITECTURE.md section 12).</summary>
    public IAsrJobStore AsrJobs => new SqliteAsrJobStore(DatabasePath);

    /// <summary>
    /// The same queue seen as reporting state, so <c>meetcap status</c> can describe depth
    /// and degradation without depending on a concrete store type
    /// (<c>docs/ROADMAP.md</c> M4).
    /// </summary>
    public IAsrQueueInspector AsrQueue => new SqliteAsrJobStore(DatabasePath);

    /// <summary>
    /// The local Speaker Registry (docs/DATA_MODEL.md sections 8-10, docs/ARCHITECTURE.md
    /// section 17). Stores speakers, embeddings, and per-session assignments. Voiceprints
    /// and name mappings are sensitive local data (docs/ARCHITECTURE.md section 22).
    /// </summary>
    public ISpeakerStore Speakers => new SqliteSpeakerStore(DatabasePath);

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
    public void EnsureMigrated()
    {
        var dataRoot = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(dataRoot))
        {
            // The data root holds recordings, transcripts, and voiceprints. Mark it as
            // private local data wherever it is, so the repository .gitignore no longer has
            // to guess.
            DataRootMarker.EnsureSelfIgnoring(dataRoot);
        }

        new SqliteMigrator().Migrate(DatabasePath);
    }

    /// <summary>
    /// Count of sessions in a non-terminal state. Zero before any session exists.
    /// </summary>
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
            $"SELECT COUNT(*) FROM sessions WHERE status IN ({SqlList.Placeholders(SessionStatus.Active.Count)})",
            conn);
        SqlListParameters.Add(cmd, SessionStatus.Active);
        var result = cmd.ExecuteScalar();
        return result is long l ? (int)l : 0;
    }

    /// <summary>
    /// Opens a short-lived connection for the M1 audio-chunk and session repositories.
    /// MeetCap is a local single-user CLI, so pooling stays disabled and each command
    /// owns its connection. Foreign keys are enabled per connection so a chunk row cannot
    /// exist without its session.
    /// </summary>
    internal SqliteConnection Open()
    {
        var conn = SqliteConnectionFactory.Open(DatabasePath);

        // audio_chunks references sessions; enforce it so the index cannot drift into
        // orphan rows. SQLite defaults to off, so it must be set per connection.
        using var pragma = new SqliteCommand("PRAGMA foreign_keys = ON", conn);
        pragma.ExecuteNonQuery();

        return conn;
    }

    internal static bool TableExists(SqliteConnection conn, string name)
        => SqliteConnectionFactory.TableExists(conn, name);
}
