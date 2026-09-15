namespace MeetCap.Cli.Commands;

using MeetCap.Core.Configuration;
using MeetCap.Core.Secrets;
using MeetCap.Persistence.Storage;
using Microsoft.Extensions.Logging;

/// <summary>
/// Implements <c>meetcap status</c>. M0 reports configuration/data-root/database
/// state and confirms no session is active (no sessions exist yet).
/// </summary>
public static class StatusCommand
{
    public static int Run(IConfigurationStore store, SecretRegistry secrets, ILogger logger, string? dataRootOverride)
    {
        var load = store.Load();
        secrets.UpdateFrom(load.Configuration);

        var dataRoot = dataRootOverride
            ?? Environment.ExpandEnvironmentVariables(load.Configuration.Storage.DataRoot);
        var dbPath = Path.Combine(dataRoot, "meetcap.db");
        var database = new MeetCapDatabase(dbPath);

        var dbInitialized = database.IsInitialized();
        var activeSessions = database.CountActiveSessions();

        Console.Out.WriteLine($"config: {store.ConfigFilePath} ({(store.Exists() ? "present" : "missing")})");
        Console.Out.WriteLine($"data root: {dataRoot}");
        Console.Out.WriteLine($"database: {dbPath} ({(dbInitialized ? "initialized" : "not initialized")})");
        Console.Out.WriteLine(activeSessions > 0
            ? $"sessions: {activeSessions} active"
            : "sessions: none active");

        logger.LogInformation("status: dataRoot={DataRoot} dbInitialized={DbInit} activeSessions={Active}",
            dataRoot, dbInitialized, activeSessions);
        return 0;
    }
}
