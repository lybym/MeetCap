namespace MeetCap.Persistence.Storage;

using Microsoft.Data.Sqlite;

/// <summary>
/// Central connection construction for MeetCap's SQLite database. Pooling is
/// disabled so the database file is always releasable between short-lived CLI
/// commands, which keeps the local database inspectable.
/// </summary>
internal static class SqliteConnectionFactory
{
    public static SqliteConnection Open(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = SqliteMigrator.BusyTimeoutSeconds,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    public static bool TableExists(SqliteConnection connection, string name)
    {
        using var cmd = new SqliteCommand(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name",
            connection);
        cmd.Parameters.AddWithValue("@name", name);
        return cmd.ExecuteScalar() is long count && count > 0;
    }
}
