namespace MeetCap.Cli.Commands;

using MeetCap.Asr;
using MeetCap.Asr.Batching;
using MeetCap.Core.Asr;
using MeetCap.Core.Configuration;
using Microsoft.Extensions.Logging;

/// <summary>
/// Implements <c>meetcap asr resume</c>: the restart entry point for the persistent
/// ASR job queue (<c>docs/ARCHITECTURE.md</c> section 12).
/// </summary>
/// <remarks>
/// <para>
/// A process that dies while an ASR job is pending, submitted, or waiting to retry
/// leaves the job's state in SQLite. This command re-drives those jobs, which is what
/// makes "incomplete work survives process restart" an observable property rather than
/// a claim. It never submits a job that already has a provider request id unless the
/// provider reports the task as unknown.
/// </para>
/// <para>
/// It also runs the batch-recovery pass before it drives the queue, because the restart
/// entry point has to see the whole crash surface, not only the half that already has a job
/// row: a process killed between writing a batch WAV and creating its job leaves audio that
/// <c>asr_jobs</c> knows nothing about (<c>docs/ARCHITECTURE.md</c> section 10.1).
/// </para>
/// </remarks>
internal static class AsrCommand
{
    public static async Task<int> ResumeAsync(
        CliContext context,
        string? sessionId,
        int maxJobs,
        bool force,
        CancellationToken cancellationToken)
    {
        if (!CommandSupport.TryLoadConfiguration(context, out var configuration, out var dataRoot, out var failure))
        {
            return failure;
        }

        if (!AsrStack.TryCreate(
                context,
                configuration,
                dataRoot,
                requireMedia: false,
                out var stack,
                out var stackFailure,
                context.AsrHttpHandler) || stack is null)
        {
            return stackFailure;
        }

        using var ownedStack = stack;

        var recovered = RecoverOrphanedBatches(context, configuration, dataRoot, stack, sessionId);

        var results = await stack.Processor
            .RunDueAsync(maxJobs, sessionId, cancellationToken, ignoreRetrySchedule: force)
            .ConfigureAwait(false);

        if (results.Count == 0 && recovered.Count == 0)
        {
            context.Out.WriteLine(
                sessionId is null
                    ? "No ASR jobs need work."
                    : $"No ASR jobs need work for session {sessionId}.");
            return 0;
        }

        var failures = 0;
        foreach (var result in results)
        {
            context.Out.WriteLine(
                $"{result.Job.Id} {AsrJobStatuses.ToWire(result.Job.Status)} " +
                $"attempts={result.Job.AttemptCount} segments={result.SegmentCount}" +
                (result.Message is null ? string.Empty : $" :: {result.Message}"));

            if (result.Outcome is AsrJobOutcome.Failed)
            {
                failures++;
            }
        }

        Logger(context).LogInformation(
            "asr resume: jobs={Jobs} failures={Failures} recoveredBatches={Recovered}",
            results.Count,
            failures,
            recovered.Count);

        if (failures > 0)
        {
            context.Error.WriteLine($"meetcap asr resume: {failures} job(s) failed permanently.");
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Queues every finalized-but-unqueued batch under the requested session, or under every
    /// session that still holds batch artifacts when no session was named.
    /// </summary>
    /// <remarks>
    /// This is the half of the crash window that <c>asr_jobs</c> cannot describe: the batch WAV
    /// and its manifest are durable, but the process died before the job row existed. Running it
    /// here is what makes the documented restart entry point actually a restart entry point
    /// (<c>docs/ARCHITECTURE.md</c> sections 10.1 and 12).
    /// </remarks>
    private static IReadOnlyList<AsrBatch> RecoverOrphanedBatches(
        CliContext context,
        MeetCapConfiguration configuration,
        string dataRoot,
        AsrStack stack,
        string? sessionId)
    {
        var options = new AsrBatchBuilderOptions
        {
            DataRoot = dataRoot,
            ProviderName = stack.Provider.Name,
            BatchSeconds = configuration.Asr.FileBatchSeconds,
            RequestSpeakerInfo = configuration.Asr.Volcengine.RequestSpeakerInfo,
            CostPerHourCny = configuration.Asr.Volcengine.CostPerHourCny,
        };

        var sessions = sessionId is null
            ? AsrBatchBuilder.EnumerateSessionsWithBatchArtifacts(dataRoot)
            : new[] { sessionId };

        var recovered = new List<AsrBatch>();
        foreach (var candidate in sessions)
        {
            recovered.AddRange(AsrBatchBuilder.RecoverFinalizedBatches(
                dataRoot,
                candidate,
                stack.Jobs,
                options,
                events: null));
        }

        foreach (var batch in recovered)
        {
            context.Out.WriteLine(
                $"recovered batch {batch.RelativePath} for session {batch.SessionId} " +
                $"({batch.DurationMs} ms of session audio).");
        }

        return recovered;
    }

    private static ILogger Logger(CliContext context)
        => context.LoggerFactory.CreateLogger(CliContext.LoggerCategory);
}
