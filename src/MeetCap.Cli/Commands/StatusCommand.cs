namespace MeetCap.Cli.Commands;

using MeetCap.AudioPipeline;
using MeetCap.Core.Capture;
using MeetCap.Core.Configuration;
using MeetCap.Core.Diagnostics;
using MeetCap.Core.Secrets;
using MeetCap.Persistence.Storage;
using Microsoft.Extensions.Logging;

/// <summary>
/// Implements <c>meetcap status</c>: configuration, data root and database state, plus
/// the M1 startup scan that detects a session which was not cleanly stopped.
/// </summary>
/// <remarks>
/// Running the scan here is deliberate: docs/RELIABILITY.md section 6 requires
/// incomplete sessions and chunks to be found on startup, and every CLI invocation is a
/// startup. The scan is idempotent, so running it from <c>status</c> as well as from
/// <c>start</c> cannot repair the same chunk twice. It also runs before the state is
/// reported, so the report describes the state after recovery rather than before it.
/// </remarks>
internal static class StatusCommand
{
    public static Task<int> Run(CliContext context)
    {
        var store = context.ConfigurationStore;
        var load = store.Load();
        context.Secrets.UpdateFrom(load.Configuration);

        string dataRoot;
        try
        {
            dataRoot = context.ResolveDataRoot(load.Configuration);
        }
        catch (MeetCapException ex)
        {
            context.Error.WriteLine($"meetcap status: {ex.Message}");
            return Task.FromResult(1);
        }

        var dbPath = Path.Combine(dataRoot, "meetcap.db");
        var database = new MeetCapDatabase(dbPath);

        var report = RunStartupRecovery(context, load.Configuration, dataRoot);

        var dbInitialized = database.IsInitialized();
        var activeSessions = database.CountActiveSessions();

        context.Out.WriteLine($"config: {store.ConfigFilePath} ({(store.Exists() ? "present" : "missing")})");
        context.Out.WriteLine($"data root: {dataRoot}");
        context.Out.WriteLine($"database: {dbPath} ({(dbInitialized ? "initialized" : "not initialized")})");
        context.Out.WriteLine(activeSessions > 0
            ? $"sessions: {activeSessions} active"
            : "sessions: none active");

        if (report is null)
        {
            return Task.FromResult(0);
        }

        context.Out.WriteLine($"recovery: {report.Describe()}");

        foreach (var session in report.Sessions)
        {
            context.Out.WriteLine($"  {session.SessionId}: {session.Status} — {session.Detail}");
            context.Out.WriteLine($"      directory: {session.SessionDirectory}");
        }

        foreach (var problem in report.Problems)
        {
            context.Error.WriteLine($"warning: {problem}");
        }

        context.LoggerFactory
            .CreateLogger(CliContext.LoggerCategory)
            .LogInformation(
                "status: dataRoot={DataRoot} dbInitialized={DbInit} activeSessions={Active} recovered={Recovered}",
                dataRoot,
                dbInitialized,
                activeSessions,
                report.RecoveredSessions);

        return Task.FromResult(0);
    }

    /// <summary>
    /// Runs the startup scan. Recovery problems are reported as warnings rather than
    /// failing <c>status</c>, because the command's job is to describe state.
    /// </summary>
    private static RecoveryReport? RunStartupRecovery(
        CliContext context,
        MeetCapConfiguration configuration,
        string dataRoot)
    {
        try
        {
            var settings = CaptureSettings.FromConfiguration(configuration, dataRoot);
            return context.CreateCaptureService(settings).RunStartupRecovery();
        }
        catch (Exception ex) when (ex is MeetCapException or IOException or UnauthorizedAccessException)
        {
            context.Error.WriteLine($"warning: startup recovery could not run: {ex.Message}");
            return null;
        }
    }
}
