namespace MeetCap.Cli.Commands.Watch;

using MeetCap.Asr;
using MeetCap.Asr.Batching;
using MeetCap.AudioPipeline;
using MeetCap.Core.Configuration;
using MeetCap.Core.Diagnostics;
using MeetCap.Core.Sessions;
using Microsoft.Extensions.Logging;

/// <summary>
/// Production <see cref="IWatchRecorder"/>: starts a watched recording through the
/// existing capture stack — the same <see cref="CaptureService"/> / <see cref="RecordingSession"/>
/// path <c>meetcap start</c> uses — and refuses to double-record when a recording is
/// already active.
/// </summary>
/// <remarks>
/// <para>
/// The cross-process ownership guard is <see cref="CaptureService.FindActiveSession"/>:
/// if any session still holds the recording surface (a manual <c>meetcap start</c>, a
/// previous watcher session still finalizing, or another watcher), <see cref="TryStart"/>
/// returns <c>null</c> rather than publishing a second concurrent recording. The watcher
/// then defers until that recording finishes.
/// </para>
/// <para>
/// When <c>watch.auto_asr</c> is <c>true</c> the host is non-null and each watched session
/// runs live file-first transcription exactly like <c>meetcap start</c>; when <c>false</c>
/// (the default) no ASR host is built, so no provider network request is made during
/// watched recordings.
/// </para>
/// </remarks>
internal sealed class WatchRecorder : IWatchRecorder
{
    private readonly CaptureService _service;
    private readonly ILiveAsrHost? _asrHost;
    private readonly MeetCapConfiguration _configuration;
    private readonly string _dataRoot;
    private readonly ILogger _logger;

    public WatchRecorder(
        CaptureService service,
        ILiveAsrHost? asrHost,
        MeetCapConfiguration configuration,
        string dataRoot,
        ILogger logger)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _asrHost = asrHost;
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _dataRoot = dataRoot ?? throw new ArgumentNullException(nameof(dataRoot));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public WatchSessionHandle? TryStart(string title, WatchTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(trigger);

        // Cross-process ownership: refuse to publish a second recording while one is
        // already active. This is the guard the watcher cannot enforce on its own, because
        // the conflicting owner is another process (a manual `meetcap start` or another
        // watcher) whose session the watcher never started.
        if (_service.FindActiveSession() is not null)
        {
            _logger.LogInformation(
                "watch: deferred start for {Process} (pid {Pid}); a recording is already active",
                trigger.ProcessName,
                trigger.ProcessId);
            return null;
        }

        RecordingSession session;
        try
        {
            session = _service.PrepareSession(title);
        }
        catch (MeetCapException ex)
        {
            // A device, free-space, or ownership failure (the lock could not be claimed)
            // surfaces here. The watcher treats this as a refused start and retries on the
            // next poll rather than crashing.
            _logger.LogInformation("watch: could not prepare session for {Process}: {Error}", trigger.ProcessName, ex.Message);
            return null;
        }

        // Persist the trigger provenance before the recording runs, so a watcher that dies
        // mid-session still leaves the trigger metadata behind (the stop section is added
        // at finalize).
        var sessionDirectory = session.SessionDirectory;
        WatchMetadataStore.WriteTrigger(sessionDirectory, trigger);

        LiveTranscription? transcription = null;
        Action<ClosedAudioChunk>? chunkHandler = null;
        Task? transcriptionLoop = null;
        var transcriptionCancellation = new CancellationTokenSource();
        try
        {
            if (_asrHost is not null)
            {
                transcription = CreateTranscription(session, out chunkHandler);
                session.BeginPostCaptureProcessing();
                session.ChunkClosed += chunkHandler;
                transcriptionLoop = Task.Run(() => transcription.RunAsync(session.SessionId, transcriptionCancellation.Token));
            }

            var runCts = new CancellationTokenSource();
            var completion = RunSessionAsync(session, transcription, transcriptionLoop, transcriptionCancellation, runCts);

            return new WatchSessionHandle(
                session.SessionId,
                trigger,
                completion,
                requestStop: () => runCts.Cancel(),
                persistStop: (reason, stoppedAt) => WatchMetadataStore.WriteStop(sessionDirectory, reason, stoppedAt),
                dispose: () => DisposeSession(session, chunkHandler, transcription, transcriptionLoop, transcriptionCancellation, runCts));
        }
        catch
        {
            // If transcription setup failed after the session was published, tear down what
            // we can so the session is not left half-wired.
            if (chunkHandler is not null)
            {
                session.ChunkClosed -= chunkHandler;
            }

            transcriptionCancellation.Cancel();
            transcription?.Stop();
            try { transcriptionLoop?.Wait(TimeSpan.FromSeconds(5)); } catch { }
            session.Dispose();
            transcriptionCancellation.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Runs the recording to completion and then performs the transcription teardown and
    /// final drain, mirroring <c>meetcap start</c>: the polling loop is stopped before the
    /// final drain so the two cannot drive the same queue at once. The returned task is the
    /// handle's <see cref="WatchSessionHandle.Completion"/>.
    /// </summary>
    private async Task<RecordingSessionOutcome> RunSessionAsync(
        RecordingSession session,
        LiveTranscription? transcription,
        Task? transcriptionLoop,
        CancellationTokenSource transcriptionCancellation,
        CancellationTokenSource runCts)
    {
        RecordingSessionOutcome outcome;
        try
        {
            outcome = await session.RunAsync(runCts.Token).ConfigureAwait(false);
        }
        finally
        {
            // Stop the polling loop before the final drain, so the two cannot drive the same
            // queue at once (same ordering as `meetcap start`).
            transcription?.Stop();
            transcriptionCancellation.Cancel();
            if (transcriptionLoop is not null)
            {
                await transcriptionLoop.ConfigureAwait(false);
            }
        }

        if (transcription is not null)
        {
            var summary = await transcription.CompleteAsync(outcome.SessionId).ConfigureAwait(false);
            transcription.CompleteSessionIfIdle(outcome.SessionId);

            _logger.LogInformation(
                "watch: asr summary session={SessionId} batches={Batches} remaining={Remaining} failed={Failed}",
                outcome.SessionId,
                summary.QueuedBatches,
                summary.JobsRemaining,
                summary.FailedJobs);
        }

        return outcome;
    }

    private LiveTranscription CreateTranscription(RecordingSession session, out Action<ClosedAudioChunk> handler)
    {
        var batches = new AsrBatchBuilder(
            _asrHost!.Jobs,
            new AsrBatchBuilderOptions
            {
                DataRoot = _dataRoot,
                ProviderName = _asrHost.Processor.ProviderName,
                BatchSeconds = _configuration.Asr.FileBatchSeconds,
                RequestSpeakerInfo = _configuration.Asr.Volcengine.RequestSpeakerInfo,
                CostPerHourCny = _configuration.Asr.Volcengine.CostPerHourCny,
            });

        batches.AttachEventSink(session.Events);
        batches.RecoverFinalizedBatches(session.SessionId);

        var transcription = new LiveTranscription(
            batches,
            _asrHost.Processor,
            _asrHost.Sessions,
            _asrHost.Artifacts,
            new LiveTranscriptionOptions());

        handler = transcription.OnChunkClosed;
        return transcription;
    }

    private static void DisposeSession(
        RecordingSession session,
        Action<ClosedAudioChunk>? chunkHandler,
        LiveTranscription? transcription,
        Task? transcriptionLoop,
        CancellationTokenSource transcriptionCancellation,
        CancellationTokenSource runCts)
    {
        if (chunkHandler is not null)
        {
            session.ChunkClosed -= chunkHandler;
        }

        transcriptionCancellation.Cancel();
        if (transcriptionLoop is not null)
        {
            try { transcriptionLoop.Wait(TimeSpan.FromSeconds(5)); } catch { }
        }

        session.Dispose();
        transcriptionCancellation.Dispose();
        runCts.Dispose();
    }
}
