namespace MeetCap.Cli.Commands;

using MeetCap.AudioPipeline;
using MeetCap.Core.Capture;
using MeetCap.Core.Diagnostics;
using MeetCap.Core.Sessions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Implements <c>meetcap start &lt;title&gt; --mode offline</c>: the startup scan, the
/// free-space pre-check, then a foreground recording that ends on Ctrl+C or on
/// <c>meetcap stop</c> from another process.
/// </summary>
internal static class StartCommand
{
    public static async Task<int> Run(CliContext context, string? title, string? mode)
    {
        var load = context.ConfigurationStore.Load();
        context.Secrets.UpdateFrom(load.Configuration);

        if (load.LoadError is not null)
        {
            context.Error.WriteLine($"meetcap start: {load.LoadError}");
            return 1;
        }

        var effectiveMode = string.IsNullOrWhiteSpace(mode)
            ? load.Configuration.Capture.DefaultMode
            : mode;

        if (!string.Equals(effectiveMode, SessionModes.Offline, StringComparison.Ordinal))
        {
            context.Error.WriteLine(
                $"meetcap start: mode '{effectiveMode}' is not available. " +
                "M1 records offline sessions only; online (loopback) capture arrives with M5 and " +
                "hybrid meetings are explicitly out of scope.");
            return 1;
        }

        var effectiveTitle = string.IsNullOrWhiteSpace(title)
            ? load.Configuration.App.DefaultTitle
            : title;

        CaptureSettings settings;
        CaptureService service;
        try
        {
            settings = CaptureSettings.FromConfiguration(load.Configuration, context.ResolveDataRoot(load.Configuration));
            service = context.CreateCaptureService(settings);
        }
        catch (MeetCapException ex)
        {
            context.Error.WriteLine($"meetcap start: {ex.Message}");
            return 1;
        }

        // Startup scan first: a previous run may have been killed leaving an active
        // chunk behind, and it has to be made durable before a new session starts.
        try
        {
            var recovery = service.RunStartupRecovery();
            if (recovery.HasFindings)
            {
                context.Out.WriteLine($"recovery: {recovery.Describe()}");
                foreach (var recoveredSession in recovery.Sessions)
                {
                    context.Out.WriteLine($"  {recoveredSession.SessionId}: {recoveredSession.Detail}");
                }
            }
        }
        catch (Exception ex) when (ex is MeetCapException or IOException or UnauthorizedAccessException)
        {
            context.Error.WriteLine($"meetcap start: startup recovery failed: {ex.Message}");
            return 1;
        }

        RecordingSession session;
        try
        {
            session = service.PrepareSession(effectiveTitle);
        }
        catch (MeetCapException ex)
        {
            context.Error.WriteLine($"meetcap start: {ex.Message}");
            return 1;
        }

        context.Out.WriteLine($"session: {session.SessionId}");
        context.Out.WriteLine($"title: {effectiveTitle}");
        context.Out.WriteLine($"mode: {SessionModes.Offline}");
        context.Out.WriteLine($"output: {session.SessionDirectory}");
        context.Out.WriteLine("recording. press Ctrl+C or run 'meetcap stop' to finish.");

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, args) =>
        {
            // Take over Ctrl+C so the session finalizes instead of dying mid-chunk.
            args.Cancel = true;
            cancellation.Cancel();
        };

        Console.CancelKeyPress += handler;
        RecordingSessionOutcome outcome;
        try
        {
            outcome = await session.RunAsync(cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }

        context.Out.WriteLine();
        context.Out.WriteLine($"session: {outcome.SessionId} ({outcome.Status})");
        context.Out.WriteLine($"duration: {FormatDuration(outcome.DurationMs)}");
        context.Out.WriteLine($"chunks closed: {outcome.ChunksClosed} ({outcome.ClosedDataBytes / 1024 / 1024} MB)");

        if (outcome.Degraded)
        {
            context.Out.WriteLine($"degraded: yes ({outcome.EndReason ?? "unknown"})");
        }

        Logger(context).LogInformation(
            "start: session={SessionId} status={Status} durationMs={DurationMs} chunks={Chunks} degraded={Degraded}",
            outcome.SessionId,
            outcome.Status,
            outcome.DurationMs,
            outcome.ChunksClosed,
            outcome.Degraded);

        if (outcome.IsClean)
        {
            return 0;
        }

        context.Error.WriteLine(
            outcome.Status == SessionStatus.Interrupted
                ? "meetcap start: the session did not complete cleanly; see the session event log."
                : "meetcap start: the session ended with reported audio or storage problems.");
        return 1;
    }

    private static string FormatDuration(long milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(milliseconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
    }

    private static ILogger Logger(CliContext context)
        => context.LoggerFactory.CreateLogger(CliContext.LoggerCategory);
}
