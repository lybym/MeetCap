namespace MeetCap.Cli.Commands;

using MeetCap.AudioPipeline;
using MeetCap.Core.Asr;
using MeetCap.Core.Capture;
using MeetCap.Core.Configuration;
using MeetCap.Core.Diagnostics;
using MeetCap.Core.Secrets;
using MeetCap.Core.Sessions;
using MeetCap.Persistence.Storage;
using Microsoft.Extensions.Logging;

/// <summary>
/// Implements <c>meetcap status</c>: configuration, data root and database state, plus
/// the M1 startup scan that detects a session which was not cleanly stopped.
/// </summary>
/// <remarks>
/// Running the scan here is deliberate: docs/RELIABILITY.md section 6 requires
/// incomplete sessions and chunks to be found on startup, and every CLI invocation is a
/// startup. The scan is safe to run from <c>status</c> because it only touches sessions
/// that are recoverable and whose liveness marker is free, so it can never rewrite a
/// recording that is still in progress or repair the same chunk twice
/// (docs/ARCHITECTURE.md section 9.1). It also runs before the state is reported, so the
/// report describes the state after recovery rather than before it.
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

        WriteAsrQueue(context, database, dbInitialized, dataRoot, load.Configuration);

        if (report is null)
        {
            return Task.FromResult(0);
        }

        context.Out.WriteLine($"recovery: {report.Describe()}");

        foreach (var session in report.Sessions)
        {
            context.Out.WriteLine($"  {session.SessionId}: {session.Status} — {session.Detail}");
            context.Out.WriteLine($"      directory: {session.SessionDirectory}");

            // A repaired session that still has a hole in its timeline must say so here,
            // not only in the event log: docs/RELIABILITY.md section 6 forbids presenting
            // such a session as recovered.
            if (session.Audit.HasGap)
            {
                context.Out.WriteLine($"      timeline:  {session.Audit.Describe()}");
                foreach (var gap in session.Audit.DescribeGaps())
                {
                    context.Out.WriteLine($"      gap:       {gap}");
                }
            }
        }

        foreach (var problem in report.Problems)
        {
            context.Error.WriteLine($"warning: {problem}");
        }

        context.LoggerFactory
            .CreateLogger(CliContext.LoggerCategory)
            .LogInformation(
                "status: dataRoot={DataRoot} dbInitialized={DbInit} activeSessions={Active} recovered={Recovered} " +
                "gapsRemain={GapsRemain}",
                dataRoot,
                dbInitialized,
                activeSessions,
                report.RecoveredSessions,
                report.RecoveryIncomplete);

        return Task.FromResult(0);
    }

    /// <summary>
    /// Reports the ASR queue depth and whether transcription is behind
    /// (<c>docs/ROADMAP.md</c> M4). A backlog is described, never treated as a recording
    /// failure: a lost network only leaves jobs <c>pending</c>/<c>retry_wait</c> while the
    /// audio stays safe (<c>docs/RELIABILITY.md</c> section 9).
    /// </summary>
    private static void WriteAsrQueue(
        CliContext context,
        MeetCapDatabase database,
        bool dbInitialized,
        string dataRoot,
        MeetCapConfiguration configuration)
    {
        if (!configuration.Asr.Enabled)
        {
            context.Out.WriteLine("asr: disabled (asr.enabled = false)");
            return;
        }

        if (!dbInitialized)
        {
            context.Out.WriteLine("asr: no queue yet (the database has not been created)");
            return;
        }

        AsrQueueStatus queue;
        try
        {
            queue = database.AsrQueue.Inspect();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            context.Error.WriteLine($"warning: the ASR queue could not be read: {ex.Message}");
            return;
        }

        context.Out.WriteLine($"asr: file ASR, batch window {configuration.Asr.FileBatchSeconds}s");
        context.Out.WriteLine($"asr queue: {queue.Describe()}");

        if (queue.IsDegraded)
        {
            context.Out.WriteLine(
                "asr state: behind (transcription is queued or failed; recording and audio artifacts are unaffected)");
        }

        WriteOrphanedBatches(context, database, dataRoot, configuration);

        foreach (var session in database.Sessions.ListActive())
        {
            var sessionQueue = database.AsrQueue.InspectSession(session.Id);
            if (sessionQueue.Outstanding == 0 && sessionQueue.Failed == 0)
            {
                continue;
            }

            context.Out.WriteLine($"  {session.Id}: {sessionQueue.Describe()}");
        }
    }

    /// <summary>
    /// Reports batch artifacts that have no job row, which is the one crash window
    /// <c>asr_jobs</c> cannot describe (<c>docs/ARCHITECTURE.md</c> section 10.1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this, an operator whose process died exactly between the batch WAV becoming
    /// durable and its job row being written sees a clean-looking queue and has no way to learn
    /// that a recording's audio is sitting untranscribed. <c>meetcap asr resume</c> repairs it;
    /// this says it exists.
    /// </para>
    /// <para>
    /// <c>status</c> keeps exiting 0 in every case — it describes state, and a command whose exit
    /// code is always 0 never has to be interpreted (<c>docs/ARCHITECTURE.md</c> section 9.2).
    /// </para>
    /// </remarks>
    private static void WriteOrphanedBatches(
        CliContext context,
        MeetCapDatabase database,
        string dataRoot,
        MeetCapConfiguration configuration)
    {
        if (!configuration.Asr.Enabled)
        {
            return;
        }

        foreach (var sessionId in SessionArtifactPaths.EnumerateSessionsWithBatchArtifacts(dataRoot))
        {
            var paths = new SessionArtifactPaths(dataRoot, sessionId);
            var known = database.AsrJobs
                .ListBySession(sessionId)
                .Select(job => job.InputArtifact)
                .ToHashSet(StringComparer.Ordinal);

            var orphaned = paths.BatchArtifacts().Where(artifact => !known.Contains(artifact)).ToArray();
            if (orphaned.Length == 0)
            {
                continue;
            }

            context.Out.WriteLine(
                $"asr orphaned: {sessionId} has {orphaned.Length} batch(es) with no job row " +
                $"({string.Join(", ", orphaned)}); run 'meetcap asr resume' to queue them.");
        }
    }

    /// <summary>
    /// Runs the startup scan. Recovery problems are reported as warnings rather than
    /// failing <c>status</c>, because the command's job is to describe state.
    /// </summary>
    /// <remarks>
    /// <c>status</c> therefore exits 0 even when the scan leaves a known gap, and that is a
    /// decision rather than an omission: it exits 0 in every case, so its exit code never has
    /// to be interpreted, and it stays usable in a check that must describe a broken data root
    /// instead of failing on it. The gap is still reported in its output and in the session's
    /// own artifacts, and the command that acts on a gap — <c>meetcap session repair</c> — is
    /// the one that exits non-zero while recovery is incomplete
    /// (<c>docs/DEVELOPMENT.md</c> section 8, <c>docs/RELIABILITY.md</c> section 6).
    /// </remarks>
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
