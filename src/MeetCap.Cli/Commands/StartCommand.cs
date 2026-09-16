namespace MeetCap.Cli.Commands;

using MeetCap.Asr;
using MeetCap.Asr.Batching;
using MeetCap.AudioPipeline;
using MeetCap.Core.Capture;
using MeetCap.Core.Configuration;
using MeetCap.Core.Diagnostics;
using MeetCap.Core.Sessions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Implements <c>meetcap start &lt;title&gt; --mode offline</c>: the startup scan, the
/// free-space pre-check, then a foreground recording that ends on Ctrl+C or on
/// <c>meetcap stop</c> from another process.
/// </summary>
/// <remarks>
/// M4 adds live file-first transcription to the same command: durable capture chunks are
/// batched while the meeting runs, each batch is queued as a persistent file-ASR job, and
/// the queue is drained in the background (<c>docs/ROADMAP.md</c> M4). None of that work
/// happens on the capture callback, and none of it can stop the recording: a lost network
/// only leaves jobs queued (<c>docs/RELIABILITY.md</c> sections 1 and 9).
/// </remarks>
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

        string dataRoot;
        CaptureSettings settings;
        CaptureService service;
        try
        {
            dataRoot = context.ResolveDataRoot(load.Configuration);
            settings = CaptureSettings.FromConfiguration(load.Configuration, dataRoot);
            service = context.CreateCaptureService(settings);
        }
        catch (MeetCapException ex)
        {
            context.Error.WriteLine($"meetcap start: {ex.Message}");
            return 1;
        }

        // The ASR stack is built before any session exists, so a credential or provider
        // configuration problem fails visibly without leaving a half-written session behind
        // (docs/DEVELOPMENT.md section 7). `asr.enabled = false` records without a transcript.
        if (!TryCreateAsrHost(context, load.Configuration, dataRoot, out var host, out var asrFailure))
        {
            return asrFailure;
        }

        using var ownedHost = host as IDisposable;

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

        Action<ClosedAudioChunk>? chunkHandler = null;
        Task? transcriptionLoop = null;
        using var transcriptionCancellation = new CancellationTokenSource();
        try
        {
            LiveTranscription? transcription = null;
            if (host is not null)
            {
                transcription = CreateTranscription(context, load.Configuration, dataRoot, host, session, out chunkHandler);
                session.BeginPostCaptureProcessing();
                session.ChunkClosed += chunkHandler;
            }

            context.Out.WriteLine($"session: {session.SessionId}");
            context.Out.WriteLine($"title: {effectiveTitle}");
            context.Out.WriteLine($"mode: {SessionModes.Offline}");
            context.Out.WriteLine($"output: {session.SessionDirectory}");
            if (transcription is not null)
            {
                context.Out.WriteLine(
                    $"asr: file ASR, batch window {load.Configuration.Asr.FileBatchSeconds}s, " +
                    $"tier {load.Configuration.Asr.ServiceTier}");
            }

            context.Out.WriteLine("recording. press Ctrl+C or run 'meetcap stop' to finish.");

            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, args) =>
            {
                // Take over Ctrl+C so the session finalizes instead of dying mid-chunk.
                args.Cancel = true;
                cancellation.Cancel();
            };

            Console.CancelKeyPress += handler;

            // The queue is drained on a background loop for as long as the recording runs, so
            // transcription advances during the meeting without ever touching the capture
            // callback (docs/ROADMAP.md M4).
            transcriptionLoop = transcription is null
                ? Task.CompletedTask
                : Task.Run(() => transcription.RunAsync(session.SessionId, transcriptionCancellation.Token));
            RecordingSessionOutcome outcome;
            try
            {
                outcome = await session.RunAsync(cancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                Console.CancelKeyPress -= handler;
            }

            // Stop the polling loop before the final drain, so the two cannot drive the same
            // queue at once. The loop finishes the drain it is in rather than being cancelled
            // mid-job, which would leave a submitted job for the final drain to poll.
            transcription?.Stop();
            transcriptionCancellation.Cancel();
            await transcriptionLoop.ConfigureAwait(false);

            return await FinishAsync(context, session, outcome, transcription).ConfigureAwait(false);
        }
        finally
        {
            if (chunkHandler is not null)
            {
                session.ChunkClosed -= chunkHandler;
            }

            // A failure before the ordered stop would otherwise leave the drain running while the
            // session artifacts are released. The sink and the queue both outlive this method, so
            // the loop is stopped and waited for rather than abandoned.
            transcriptionCancellation.Cancel();
            if (transcriptionLoop is not null)
            {
                await transcriptionLoop.ConfigureAwait(false);
            }

            session.Dispose();
        }
    }

    /// <summary>
    /// Builds the live transcription path for this session: the batch builder, the recovered
    /// batch files, and the queue drain.
    /// </summary>
    private static LiveTranscription CreateTranscription(
        CliContext context,
        MeetCapConfiguration configuration,
        string dataRoot,
        ILiveAsrHost host,
        RecordingSession session,
        out Action<ClosedAudioChunk> handler)
    {
        handler = _ => { };

        var batches = new AsrBatchBuilder(
            host.Jobs,
            new AsrBatchBuilderOptions
            {
                DataRoot = dataRoot,
                ProviderName = host.Processor.ProviderName,
                BatchSeconds = configuration.Asr.FileBatchSeconds,
                ServiceTier = configuration.Asr.ServiceTier,
                RequestSpeakerInfo = configuration.Asr.Volcengine.RequestSpeakerInfo,
                CostPerHourCny = configuration.Asr.Volcengine.CostPerHourCny,
            });

        // Batch events share the recorder's own append lock rather than opening a second
        // writer on events.jsonl.
        batches.AttachEventSink(session.Events);

        // Re-queue batches an earlier process finalized but never queued a job for; jobs that
        // already exist stay the queue's business (docs/RELIABILITY.md section 6).
        var recoveredBatches = batches.RecoverFinalizedBatches(session.SessionId);
        if (recoveredBatches.Count > 0)
        {
            context.Out.WriteLine($"asr: re-queued {recoveredBatches.Count} recovered batch(es)");
        }

        var transcription = new LiveTranscription(
            batches,
            host.Processor,
            host.Sessions,
            host.Artifacts,
            new LiveTranscriptionOptions());

        handler = transcription.OnChunkClosed;
        return transcription;
    }

    private static bool TryCreateAsrHost(
        CliContext context,
        MeetCapConfiguration configuration,
        string dataRoot,
        out ILiveAsrHost? host,
        out int exitCode)
    {
        host = null;
        exitCode = 0;

        // `asr.enabled = false` is a supported configuration: MeetCap records without a
        // transcript. Everything else must build a provider before recording starts.
        if (!configuration.Asr.Enabled)
        {
            return true;
        }

        return context.TryCreateAsrHost(configuration, dataRoot, out host, out exitCode);
    }

    private static async Task<int> FinishAsync(
        CliContext context,
        RecordingSession session,
        RecordingSessionOutcome outcome,
        LiveTranscription? transcription)
    {
        context.Out.WriteLine();
        context.Out.WriteLine($"session: {outcome.SessionId} ({outcome.Status})");
        context.Out.WriteLine($"duration: {FormatDuration(outcome.DurationMs)}");
        context.Out.WriteLine($"chunks closed: {outcome.ChunksClosed} ({outcome.ClosedDataBytes / 1024 / 1024} MB)");

        // The bounded-buffer accounting is reported on a clean run too: "the queue never
        // came close to its bound" is the evidence docs/RELIABILITY.md section 4 asks for,
        // and it is only useful if it is stated while the run succeeded.
        context.Out.WriteLine(
            $"capture buffer: peak {outcome.CaptureHealth.PeakQueuedPackets}/{outcome.CaptureHealth.CapacityPackets} packets, " +
            $"dropped {outcome.CaptureHealth.DroppedPackets}, " +
            $"stalled {outcome.CaptureHealth.StallEvents} time(s) (longest {outcome.CaptureHealth.LongestStallMs} ms)");

        if (outcome.GapCount > 0)
        {
            context.Out.WriteLine($"audio gaps: {outcome.GapCount} ({outcome.GapTotalMs} ms missing)");
        }

        if (outcome.Degraded)
        {
            context.Out.WriteLine($"degraded: yes ({outcome.EndReason ?? "unknown"})");
        }

        LiveTranscriptionSummary? summary = null;
        if (transcription is not null)
        {
            // The final partial batch is flushed first, so the audio recorded after the last
            // full window still reaches the provider (docs/ROADMAP.md M4).
            summary = await transcription.CompleteAsync(outcome.SessionId).ConfigureAwait(false);
            transcription.CompleteSessionIfIdle(outcome.SessionId);

            context.Out.WriteLine(summary.Describe());

            if (summary.JobsRemaining > 0)
            {
                context.Out.WriteLine(
                    $"asr: {summary.JobsRemaining} job(s) still need work; " +
                    $"run 'meetcap asr resume --session {outcome.SessionId}' when the provider is reachable.");
            }

            if (summary.FailedJobs > 0)
            {
                context.Error.WriteLine(
                    $"meetcap start: {summary.FailedJobs} ASR job(s) failed. " +
                    "The recording and every closed chunk are intact; see the session event log.");
            }
        }

        Logger(context).LogInformation(
            "start: session={SessionId} status={Status} durationMs={DurationMs} chunks={Chunks} degraded={Degraded} " +
            "gapMs={GapMs} droppedPackets={Dropped} stalled={Stalled} asrBatches={Batches} asrRemaining={Remaining}",
            outcome.SessionId,
            outcome.Status,
            outcome.DurationMs,
            outcome.ChunksClosed,
            outcome.Degraded,
            outcome.GapTotalMs,
            outcome.CaptureHealth.DroppedPackets,
            outcome.CaptureHealth.StallEvents,
            summary?.QueuedBatches ?? 0,
            summary?.JobsRemaining ?? 0);

        if (outcome.Status != SessionStatus.Interrupted && !outcome.Degraded)
        {
            // A failed or still-running ASR job does not make the recording unclean: the audio
            // is safe and the transcript is a downstream consumer of it
            // (docs/RELIABILITY.md section 1). The lines above state exactly where the queue
            // stands, and the exit code stays reserved for the recording itself. A session that
            // stopped cleanly but still has ASR work is PROCESSING, not INTERRUPTED.
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
