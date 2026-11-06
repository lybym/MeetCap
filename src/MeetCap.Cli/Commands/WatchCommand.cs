namespace MeetCap.Cli.Commands;

using MeetCap.Cli.Commands.Watch;
using MeetCap.AudioPipeline;
using MeetCap.Core.Capture;
using MeetCap.Core.Configuration;
using MeetCap.Core.Diagnostics;
using MeetCap.Core.Sessions;
using MeetCap.Core.Time;
using Microsoft.Extensions.Logging;

/// <summary>
/// Implements <c>meetcap watch</c>: a process-lifecycle auto-capture daemon that starts a
/// recording when a configured meeting-app main process launches and stops it when the
/// whole watched cohort exits (issue #52).
/// </summary>
/// <remarks>
/// This is <strong>process-lifecycle auto-capture, not meeting-state detection</strong>: a
/// watched app may stay open outside a meeting, so capture can include non-meeting audio.
/// The feature is opt-in (<c>[watch] enabled = false</c> by default). On watcher restart,
/// already-running processes do <em>not</em> trigger capture — only new launches do.
/// </remarks>
internal static class WatchCommand
{
    public static async Task<int> RunAsync(CliContext context, CancellationToken cancellationToken)
    {
        if (!CommandSupport.TryLoadConfiguration(context, out var configuration, out var dataRoot, out var failure))
        {
            return failure;
        }

        var watch = configuration.Watch;
        if (!watch.Enabled)
        {
            context.Error.WriteLine(
                "meetcap watch: [watch] enabled is false. Process-lifecycle auto-capture is opt-in; " +
                "set [watch] enabled = true in config.toml to allow it. " +
                "Note: a watched meeting app may stay open outside a meeting, so this can record non-meeting audio.");
            return 1;
        }

        if (watch.Processes.Count == 0)
        {
            context.Error.WriteLine(
                "meetcap watch: [watch] processes is empty. Configure at least one main process name " +
                "(without .exe), e.g. processes = [\"WeMeetApp\", \"CloudVideo\"].");
            return 1;
        }

        var options = WatchOptions.FromConfiguration(watch);

        if (!string.Equals(options.CaptureMode, SessionModes.Online, StringComparison.Ordinal) &&
            !string.Equals(options.CaptureMode, SessionModes.Offline, StringComparison.Ordinal))
        {
            context.Error.WriteLine(
                $"meetcap watch: watch.capture_mode='{options.CaptureMode}' is invalid. " +
                "Use 'offline' or 'online'.");
            return 1;
        }

        // Validate the loopback mode an online session would use before any recording is
        // prepared, mirroring `meetcap start` so an unusable loopback_mode fails here.
        if (string.Equals(options.CaptureMode, SessionModes.Online, StringComparison.Ordinal) &&
            !LoopbackModes.TryParse(configuration.Capture.Online.LoopbackMode, out _))
        {
            context.Error.WriteLine(
                $"meetcap watch: capture.online.loopback_mode='{configuration.Capture.Online.LoopbackMode}' is invalid. " +
                $"Allowed: {LoopbackModes.System} or {LoopbackModes.Process}.");
            return 1;
        }

        CaptureSettings settings;
        CaptureService service;
        try
        {
            settings = CaptureSettings.FromConfiguration(configuration, dataRoot, options.CaptureMode);
            service = context.CreateCaptureService(settings);
        }
        catch (MeetCapException ex)
        {
            context.Error.WriteLine($"meetcap watch: {ex.Message}");
            return 1;
        }

        // The ASR host is built only when auto_asr is on. With the default (false) no host
        // is constructed, so no provider adapter or HTTP handler exists and no network
        // request can be made during watched recordings.
        ILiveAsrHost? asrHost = null;
        if (options.AutoAsr)
        {
            if (!context.TryCreateAsrHost(configuration, dataRoot, out asrHost, out var asrFailure))
            {
                return asrFailure;
            }
        }

        using var ownedHost = asrHost as IDisposable;

        // Startup recovery runs once at watcher start, before the first session, so a
        // previous killed run's half-written chunks are made durable before a new cohort
        // session begins (same guarantee as `meetcap start`).
        try
        {
            var recovery = service.RunStartupRecovery();
            if (recovery.HasFindings)
            {
                context.Out.WriteLine($"recovery: {recovery.Describe()}");
                foreach (var recovered in recovery.Sessions)
                {
                    context.Out.WriteLine($"  {recovered.SessionId}: {recovered.Detail}");
                }
            }
        }
        catch (Exception ex) when (ex is MeetCapException or IOException or UnauthorizedAccessException)
        {
            context.Error.WriteLine($"meetcap watch: startup recovery failed: {ex.Message}");
            return 1;
        }

        var recorder = new WatchRecorder(service, asrHost, configuration, dataRoot, Logger(context));
        var probe = new ProcessProbe(options.Processes);
        var observer = new TextWriterWatchObserver(context.Out, Logger(context));
        var watcher = new MeetingWatcher(probe, recorder, SystemClock.Instance, options, observer);

        context.Out.WriteLine($"watch: monitoring {options.Processes.Count} process(es): {string.Join(", ", options.Processes)}");
        context.Out.WriteLine($"watch: poll every {options.PollInterval.TotalSeconds:0.#}s, exit debounce {options.ExitDebounce.TotalSeconds:0.#}s, capture_mode={options.CaptureMode}, auto_asr={options.AutoAsr}");
        context.Out.WriteLine("watch: on restart, already-running watched processes do NOT trigger capture (only new launches).");
        context.Out.WriteLine("watch: recording. press Ctrl+C to stop the watcher.");

        // Take over Ctrl+C so the watcher performs a fail-safe stop of any active recording
        // rather than dying mid-chunk.
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler handler = (_, args) =>
        {
            args.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;

        try
        {
            await watcher.RunAsync(cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }

        return 0;
    }

    private static ILogger Logger(CliContext context)
        => context.LoggerFactory.CreateLogger(CliContext.LoggerCategory);

    /// <summary>
    /// Writes watcher activity to stdout and the structured log. Best-effort: a hook that
    /// throws is swallowed by the watcher so reporting never stops capture.
    /// </summary>
    private sealed class TextWriterWatchObserver : IWatchObserver
    {
        private readonly TextWriter _out;
        private readonly ILogger _logger;

        public TextWriterWatchObserver(TextWriter output, ILogger logger)
        {
            _out = output;
            _logger = logger;
        }

        public void OnWatcherStarted(int preExistingProcessCount)
        {
            if (preExistingProcessCount > 0)
            {
                _out.WriteLine($"watch: {preExistingProcessCount} watched process(es) already running — not triggering (new launches only).");
            }

            _logger.LogInformation("watch: started; preExistingWatched={Count}", preExistingProcessCount);
        }

        public void OnPollIdle()
        {
            // Polled frequently; only logged at debug to avoid spamming stdout.
            _logger.LogDebug("watch: idle poll");
        }

        public void OnRecordingStarted(string sessionId, WatchTrigger trigger)
        {
            _out.WriteLine($"watch: started session {sessionId} for {trigger.ProcessName} (pid {trigger.ProcessId}).");
            _logger.LogInformation(
                "watch: recording started session={SessionId} process={Process} pid={Pid} startedAt={StartedAt}",
                sessionId,
                trigger.ProcessName,
                trigger.ProcessId,
                trigger.ProcessStartTime);
        }

        public void OnStartRefused(string reason)
        {
            _logger.LogInformation("watch: start deferred: {Reason}", reason);
        }

        public void OnRecordingFinalized(string sessionId, string stopReason, RecordingSessionOutcome? outcome)
        {
            if (outcome is not null)
            {
                _out.WriteLine($"watch: finalized session {sessionId} ({outcome.Status}, {FormatDuration(outcome.DurationMs)}).");
            }
            else
            {
                _out.WriteLine($"watch: session {sessionId} did not finalize cleanly; left for recovery.");
            }

            _logger.LogInformation(
                "watch: recording finalized session={SessionId} stopReason={Reason} status={Status} degraded={Degraded}",
                sessionId,
                stopReason,
                outcome?.Status ?? "unknown",
                outcome?.Degraded ?? false);
        }

        private static string FormatDuration(long milliseconds)
        {
            var span = TimeSpan.FromMilliseconds(milliseconds);
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}"
                : $"{span.Minutes:00}:{span.Seconds:00}";
        }
    }
}
