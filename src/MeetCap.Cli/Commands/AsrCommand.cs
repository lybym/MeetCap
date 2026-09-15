namespace MeetCap.Cli.Commands;

using MeetCap.Asr;
using MeetCap.Core.Asr;
using MeetCap.Core.Configuration;
using Microsoft.Extensions.Logging;

/// <summary>
/// Implements <c>meetcap asr resume</c>: the restart entry point for the persistent
/// ASR job queue (<c>docs/ARCHITECTURE.md</c> section 12).
/// </summary>
/// <remarks>
/// A process that dies while an ASR job is pending, submitted, or waiting to retry
/// leaves the job's state in SQLite. This command re-drives those jobs, which is what
/// makes "incomplete work survives process restart" an observable property rather than
/// a claim. It never submits a job that already has a provider request id unless the
/// provider reports the task as unknown.
/// </remarks>
internal static class AsrCommand
{
    public static async Task<int> ResumeAsync(
        CliContext context,
        string? sessionId,
        int maxJobs,
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
                configuration.Asr.ServiceTier,
                requireMedia: false,
                out var stack,
                out var stackFailure) || stack is null)
        {
            return stackFailure;
        }

        using var ownedStack = stack;

        var results = await stack.Processor
            .RunDueAsync(maxJobs, sessionId, cancellationToken)
            .ConfigureAwait(false);

        if (results.Count == 0)
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
            "asr resume: jobs={Jobs} failures={Failures}",
            results.Count,
            failures);

        if (failures > 0)
        {
            context.Error.WriteLine($"meetcap asr resume: {failures} job(s) failed permanently.");
            return 1;
        }

        return 0;
    }

    private static ILogger Logger(CliContext context)
        => context.LoggerFactory.CreateLogger(CliContext.LoggerCategory);
}
