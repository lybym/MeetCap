namespace MeetCap.Cli.Commands;

using MeetCap.Core.Configuration;
using MeetCap.Core.Secrets;
using MeetCap.Persistence.Storage;
using Microsoft.Extensions.Logging;

/// <summary>
/// Implements <c>meetcap status</c>. M0 reports configuration/data-root/database
/// state and confirms no session is active (no sessions exist yet).
/// </summary>
internal static class StatusCommand
{
    public static Task<int> Run(CliContext context)
    {
        // Shared effective-configuration path: `status` and `config show` must agree on
        // what "effective" means, including the one-shot --data-root override.
        var effective = context.LoadEffectiveConfiguration();
        var store = effective.Store;

        var dataRoot = effective.DataRoot;
        var dbPath = Path.Combine(dataRoot, "meetcap.db");
        var database = new MeetCapDatabase(dbPath);

        var dbInitialized = database.IsInitialized();
        var activeSessions = database.CountActiveSessions();

        context.Out.WriteLine($"config: {store.ConfigFilePath} ({(store.Exists() ? "present" : "missing")})");
        context.Out.WriteLine($"data root: {dataRoot}");
        context.Out.WriteLine($"database: {dbPath} ({(dbInitialized ? "initialized" : "not initialized")})");
        context.Out.WriteLine(activeSessions > 0
            ? $"sessions: {activeSessions} active"
            : "sessions: none active");

        context.LoggerFactory
            .CreateLogger(CliContext.LoggerCategory)
            .LogInformation(
                "status: dataRoot={DataRoot} dbInitialized={DbInit} activeSessions={Active}",
                dataRoot,
                dbInitialized,
                activeSessions);
        return Task.FromResult(0);
    }
}
