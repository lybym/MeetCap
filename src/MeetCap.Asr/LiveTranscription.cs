namespace MeetCap.Asr;

using MeetCap.Asr.Batching;
using MeetCap.Core.Asr;
using MeetCap.Core.Sessions;

/// <summary>Knobs for <see cref="LiveTranscription"/>, populated from configuration.</summary>
public sealed record LiveTranscriptionOptions
{
    /// <summary>
    /// Delay between queue drains while a recording is running.
    /// </summary>
    /// <remarks>
    /// Short enough that a long meeting's transcription keeps up with its batch windows, and
    /// irrelevant to capture: the drain runs on its own task and never on the capture
    /// callback. A drain that finds nothing to do costs one SQLite read.
    /// </remarks>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Jobs one drain may advance. A live session queues one job per batch window, so a small
    /// bound is enough and keeps a drain from monopolizing the process.
    /// </summary>
    public int MaxJobsPerDrain { get; init; } = 4;

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>One drain of the persistent ASR queue.</summary>
public sealed record LiveTranscriptionDrain(int Jobs, int Failures, int AwaitingRetry, int StillRunning)
{
    public static readonly LiveTranscriptionDrain Empty = new(0, 0, 0, 0);
}

/// <summary>
/// Runs the file-first transcription path beside a live recording: closed capture chunks are
/// batched, each batch is queued as a persistent file-ASR job, and the queue is drained
/// while capture continues (<c>docs/ROADMAP.md</c> M4).
/// </summary>
/// <remarks>
/// <para>
/// Everything on this path is off the capture callback. Chunk batching happens on the
/// recording consumer thread through <see cref="RecordingSession.ChunkClosed"/>, and
/// submission and polling happen here on a background loop, so a network outage cannot
/// reach — let alone pause — audio capture (<c>docs/RELIABILITY.md</c> sections 1 and 9).
/// </para>
/// <para>
/// A failed drain is not a failed recording. Transient provider failures already leave their
/// job in the durable <c>retry_wait</c> state; an unexpected failure of the drain itself is
/// counted and reported, and the loop keeps going, because the audio on disk is the product
/// and the transcript is a downstream consumer of it.
/// </para>
/// <para>
/// The session stays <c>PROCESSING</c> while its queue holds work and is completed when the
/// queue is terminal. A session with no batches at all (a very short recording) never
/// becomes <c>PROCESSING</c> in the first place, so it must be completed explicitly.
/// </para>
/// </remarks>
public sealed class LiveTranscription
{
    private readonly AsrBatchBuilder _batches;
    private readonly AsrJobProcessor _processor;
    private readonly ISessionStore _sessions;
    private readonly ISessionArtifactWriter _artifacts;
    private readonly LiveTranscriptionOptions _options;

    private int _queuedBatches;
    private int _transientFailures;
    private volatile bool _stopRequested;

    /// <summary>Serializes queue drains so the background loop and the stop-time drain cannot race.</summary>
    private readonly SemaphoreSlim _drainGate = new(1, 1);

    public LiveTranscription(
        AsrBatchBuilder batches,
        AsrJobProcessor processor,
        ISessionStore sessions,
        ISessionArtifactWriter artifacts,
        LiveTranscriptionOptions options)
    {
        _batches = batches ?? throw new ArgumentNullException(nameof(batches));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Batches queued as persistent jobs so far.</summary>
    public int QueuedBatches => _queuedBatches;

    /// <summary>Drain failures that were not provider failures (each of which is durable).</summary>
    public int TransientFailures => _transientFailures;

    /// <summary>Takes one closed chunk into its batch window and queues the job when it closes.</summary>
    public void OnChunkClosed(ClosedAudioChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        if (_batches.OnChunkClosed(chunk) is not null)
        {
            _queuedBatches++;
        }
    }

    /// <summary>
    /// Closes the remaining partial batch of every track. Called at stop, so the audio
    /// recorded after the last full window still reaches the provider
    /// (<c>docs/ROADMAP.md</c> M4).
    /// </summary>
    public int FlushPendingBatches()
    {
        var flushed = _batches.FlushPendingBatches();
        _queuedBatches += flushed.Count;
        return flushed.Count;
    }

    /// <summary>
    /// Drains the queue every <see cref="LiveTranscriptionOptions.PollInterval"/> until
    /// <see cref="Stop"/> or process shutdown. Never throws for provider errors.
    /// </summary>
    /// <remarks>
    /// The token stops the loop, and is deliberately not the token a drain runs under: a drain
    /// that is cancelled half-way through leaves its job in a durable but not-yet-processed
    /// state (<c>submitted</c>), and the stop-time drain would then have to poll it rather than
    /// finish it. This is the same "persist before the next side effect" rule the job state
    /// machine is built on (<c>docs/ARCHITECTURE.md</c> section 12).
    /// </remarks>
    public async Task RunAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        using var timer = new PeriodicTimer(_options.PollInterval);

        // Drain once before waiting: recovery may have queued a job for a batch that a
        // previous process finalized, and that should not wait a whole interval.
        await DrainAsync(sessionId).ConfigureAwait(false);

        try
        {
            while (!_stopRequested
                   && await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_stopRequested)
                {
                    return;
                }

                await DrainAsync(sessionId).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Process shutdown; the caller performs the final drain.
        }
    }

    /// <summary>
    /// Asks the background loop to finish its current drain and stop, so a stop-time drain can
    /// run without racing it.
    /// </summary>
    public void Stop() => _stopRequested = true;

    /// <summary>
    /// Runs the queue after recording stopped: flush the partial batch, drain until the
    /// queue is terminal, and leave the session in a described state.
    /// </summary>
    public async Task<LiveTranscriptionSummary> CompleteAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        // Stop the background loop first, so the stop-time drain cannot race it on the same
        // queue.
        Stop();

        FlushPendingBatches();

        var drain = await DrainAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return new LiveTranscriptionSummary(
            _queuedBatches,
            drain.Jobs,
            drain.Failures,
            drain.AwaitingRetry,
            drain.StillRunning,
            _transientFailures,
            Remaining(sessionId));
    }

    /// <summary>Jobs of this session that still need work.</summary>
    public int Remaining(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return _processor.CountOutstanding(sessionId);
    }

    /// <summary>Advances this session's queue once.</summary>
    /// <remarks>
    /// Drains are serialized. The background loop and the stop-time drain both drive the same
    /// durable queue, and two concurrent passes could observe one job in an intermediate state
    /// and leave it there — the second pass would see <c>submitted</c> and a first pass's
    /// in-flight poll. Serializing them costs nothing on this path and removes the race
    /// (<c>docs/ARCHITECTURE.md</c> section 12).
    /// </remarks>
    public async Task<LiveTranscriptionDrain> DrainAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        await _drainGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await DrainCoreAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _drainGate.Release();
        }
    }

    private async Task<LiveTranscriptionDrain> DrainCoreAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        // One drain keeps advancing this session's queue until nothing is immediately due. A
        // provider that is still working leaves its job in `polling` with its state persisted,
        // so the loop ends there instead of waiting: the next drain — or `meetcap asr resume` —
        // continues it (docs/ARCHITECTURE.md section 12).
        var jobs = 0;
        var failures = 0;
        var awaitingRetry = 0;
        var stillRunning = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<AsrJobProcessResult> results;
            try
            {
                results = await _processor
                    .RunDueAsync(_options.MaxJobsPerDrain, sessionId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (AsrTransientException)
            {
                // A provider transport failure is the expected offline case: the durable job
                // state already records it and `next_retry_at` schedules the next attempt, so
                // nothing has to be invented here and capture is unaffected.
                _transientFailures++;
                break;
            }
            catch (AsrPermanentException)
            {
                // The job itself records a permanent provider failure. The drain survives so the
                // remaining jobs still get their turn.
                _transientFailures++;
                break;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                // A background drain must not take the recorder down with it. The jobs keep their
                // durable state, so the next drain — or `meetcap asr resume` — continues.
                _transientFailures++;
                break;
            }

            if (results.Count == 0)
            {
                break;
            }


            foreach (var result in results)
            {
                if (result.Outcome == AsrJobOutcome.NoWork)
                {
                    continue;
                }

                jobs++;
                switch (result.Outcome)
                {
                    case AsrJobOutcome.Failed:
                        failures++;
                        break;
                    case AsrJobOutcome.AwaitingRetry:
                        awaitingRetry++;
                        break;
                    case AsrJobOutcome.StillRunning:
                        stillRunning++;
                        break;
                }
            }

            // The loop yields to a stop request between passes, so the stop-time drain does not
            // have to wait for a whole queue to be re-scanned.
            if (_stopRequested)
            {
                break;
            }
        }

        return new LiveTranscriptionDrain(jobs, failures, awaitingRetry, stillRunning);
    }

    /// <summary>
    /// Completes the session when it is <c>PROCESSING</c> with nothing left to do.
    /// </summary>
    /// <remarks>
    /// The ASR job processor completes a session itself as soon as its last job succeeds.
    /// This covers the two cases that processor cannot see: a session that queued no batch at
    /// all, and a session whose queue is terminal because of a successful final drain.
    /// </remarks>
    public bool CompleteSessionIfIdle(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var session = _sessions.Get(sessionId);
        if (session is null || !string.Equals(session.Status, SessionStatus.Processing, StringComparison.Ordinal))
        {
            return false;
        }

        var jobs = _processor.ListJobs(sessionId);
        if (jobs.Any(job => AsrJobStatuses.IsResumable(job.Status)))
        {
            return false;
        }

        var now = _options.TimeProvider.GetUtcNow();
        _sessions.Update(session.WithStatus(SessionStatus.Completed, now));
        _artifacts.AppendEvent(
            new SessionArtifactPaths(_processor.DataRoot, sessionId),
            SessionEvents.SessionCompleted,
            session.DurationMs,
            new Dictionary<string, object?> { ["session_id"] = sessionId });
        return true;
    }
}

/// <summary>What the post-stop drain of one live session achieved.</summary>
public sealed record LiveTranscriptionSummary(
    int QueuedBatches,
    int ProcessedJobs,
    int FailedJobs,
    int AwaitingRetry,
    int StillRunning,
    int DrainFailures,
    int JobsRemaining)
{
    /// <summary>True when this session's transcription has no outstanding or failed work.</summary>
    public bool IsComplete => JobsRemaining == 0 && FailedJobs == 0;

    /// <summary>True when the transcript is behind only because the provider is still working.</summary>
    public bool IsBehind => JobsRemaining > 0;

    /// <summary>One-line report for the CLI.</summary>
    public string Describe()
    {
        var text =
            $"asr batches: {QueuedBatches} queued, {ProcessedJobs} job(s) advanced, " +
            $"{AwaitingRetry} awaiting retry, {StillRunning} still running";

        if (FailedJobs > 0)
        {
            text += $", {FailedJobs} failed";
        }

        if (DrainFailures > 0)
        {
            text += $", {DrainFailures} drain failure(s)";
        }

        return text;
    }
}
