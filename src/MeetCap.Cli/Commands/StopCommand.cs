namespace MeetCap.Cli.Commands;

using MeetCap.AudioPipeline;
using MeetCap.Core.Capture;
using MeetCap.Core.Diagnostics;
using Microsoft.Extensions.Logging;

/// <summary>
/// Implements <c>meetcap stop</c>: signals the recording process through the session's
/// stop-request marker and waits for the session to reach a terminal state.
/// </summary>
internal static class StopCommand
{
    /// <summary>How long <c>stop</c> waits for the recorder to finalize its last chunk.</summary>
    internal static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(15);

    public static Task<int> Run(CliContext context)
    {
        var load = context.ConfigurationStore.Load();
        context.Secrets.UpdateFrom(load.Configuration);

        CaptureSettings settings;
        CaptureService service;
        try
        {
            settings = CaptureSettings.FromConfiguration(load.Configuration, context.ResolveDataRoot(load.Configuration));
            service = context.CreateCaptureService(settings);
        }
        catch (MeetCapException ex)
        {
            context.Error.WriteLine($"meetcap stop: {ex.Message}");
            return Task.FromResult(1);
        }

        StopRequestOutcome outcome;
        try
        {
            outcome = service.RequestStop(DefaultWait);
        }
        catch (MeetCapException ex)
        {
            context.Error.WriteLine($"meetcap stop: {ex.Message}");
            return Task.FromResult(1);
        }

        if (!outcome.Signalled)
        {
            context.Error.WriteLine($"meetcap stop: {outcome.Message}.");
            return Task.FromResult(1);
        }

        Logger(context).LogInformation(
            "stop: session={SessionId} confirmed={Confirmed}",
            outcome.SessionId,
            outcome.ConfirmedStopped);

        if (outcome.ConfirmedStopped)
        {
            context.Out.WriteLine($"stopped session {outcome.SessionId}.");
            return Task.FromResult(0);
        }

        context.Error.WriteLine(
            $"meetcap stop: stop requested for session {outcome.SessionId}, but it is still finalizing. " +
            "Run 'meetcap status' to see the session state.");
        return Task.FromResult(1);
    }

    private static ILogger Logger(CliContext context)
        => context.LoggerFactory.CreateLogger(CliContext.LoggerCategory);
}
