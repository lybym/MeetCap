namespace MeetCap.Cli.Commands;

using MeetCap.AudioPipeline;
using MeetCap.Core.Capture;
using MeetCap.Core.Diagnostics;
using Microsoft.Extensions.Logging;

/// <summary>
/// Implements <c>meetcap session repair</c>: the explicit repair and recovery entry point
/// for sessions left behind by a killed process (<c>docs/RELIABILITY.md</c> section 6).
/// </summary>
/// <remarks>
/// <para>
/// The startup scan already runs on every command, so this does not introduce a second
/// repair implementation; it exposes the same <see cref="SessionRecoveryScanner"/> pass as
/// an operator-triggered action with a machine-readable outcome.
/// </para>
/// <para>
/// The exit code is the honest part: a session whose timeline still has a known gap exits
/// non-zero, and prints exactly where the audio is missing. A repair that cannot make the
/// session whole must never report success.
/// </para>
/// </remarks>
internal static class SessionCommand
{
    public static Task<int> RepairAsync(CliContext context, string? sessionId)
    {
        if (!CommandSupport.TryLoadConfiguration(context, out var configuration, out var dataRoot, out var failure))
        {
            return Task.FromResult(failure);
        }

        SessionRepairOutcome outcome;
        try
        {
            var settings = CaptureSettings.FromConfiguration(configuration, dataRoot);
            outcome = context.CreateCaptureService(settings).RepairSession(sessionId);
        }
        catch (MeetCapException ex)
        {
            context.Error.WriteLine($"meetcap session repair: {ex.Message}");
            return Task.FromResult(1);
        }

        context.Out.WriteLine($"repair: {outcome.Describe()}");

        foreach (var session in outcome.Report.Sessions)
        {
            context.Out.WriteLine($"  {session.SessionId}: {session.Status} — {session.Detail}");
            context.Out.WriteLine($"      directory: {session.SessionDirectory}");
            context.Out.WriteLine($"      timeline:  {session.Audit.Describe()}");

            foreach (var gap in session.Audit.DescribeGaps())
            {
                context.Out.WriteLine($"      gap:       {gap}");
            }
        }

        foreach (var problem in outcome.Report.Problems)
        {
            context.Error.WriteLine($"warning: {problem}");
        }

        Logger(context).LogInformation(
            "session repair: session={SessionId} repaired={Repaired} gapsRemain={GapsRemain}",
            sessionId ?? "(all)",
            outcome.Report.RecoveredSessions,
            outcome.Report.RecoveryIncomplete);

        if (outcome.RecoveryIncomplete)
        {
            context.Error.WriteLine(
                "meetcap session repair: recovery is incomplete. " +
                (outcome.Found
                    ? $"{outcome.RemainingGapMs} ms of audio on the session timeline has no durable chunk; " +
                      "the affected audio is gone and the session is marked degraded rather than reported as repaired."
                    : "run 'meetcap status' to list the sessions that exist under the data root."));
            return Task.FromResult(1);
        }

        return Task.FromResult(0);
    }

    private static ILogger Logger(CliContext context)
        => context.LoggerFactory.CreateLogger(CliContext.LoggerCategory);
}
