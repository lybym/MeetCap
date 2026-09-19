namespace MeetCap.Asr;

using System.Text;
using MeetCap.Core.Asr;
using MeetCap.Core.Sessions;
using MeetCap.Core.Transcripts;

/// <summary>Knobs for <see cref="AsrJobProcessor"/>, populated from configuration.</summary>
public sealed record AsrJobProcessorOptions
{
    /// <summary>MeetCap data root; session artifacts are resolved under it.</summary>
    public required string DataRoot { get; init; }

    public AsrRetryPolicy RetryPolicy { get; init; } = AsrRetryPolicy.Default;

    /// <summary>Seconds between provider result queries.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Wall-clock budget for one invocation's polling. When it expires the job is left
    /// in <c>polling</c> with its state persisted, so a later <c>meetcap asr resume</c>
    /// continues instead of losing the provider task.
    /// </summary>
    public TimeSpan PollTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Estimate-only hourly rate in CNY, used to fill <c>estimated_cost_cny</c>.</summary>
    public double CostPerHourCny { get; init; } = 0.8;

    /// <summary>Whether to (re)write <c>transcript/live.md</c> after each completed job.</summary>
    public bool WriteMarkdown { get; init; } = true;

    public TranscriptRenderOptions TranscriptOptions { get; init; } = TranscriptRenderOptions.Default;

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Injected so tests do not sleep. Defaults to <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</summary>
    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }
}

/// <summary>How one <see cref="AsrJobProcessor.ProcessAsync"/> call ended.</summary>
public enum AsrJobOutcome
{
    Succeeded,

    Failed,

    /// <summary>Transient failure; the durable <c>next_retry_at</c> schedules the next attempt.</summary>
    AwaitingRetry,

    /// <summary>The provider is still working and the invocation's poll budget expired.</summary>
    StillRunning,

    /// <summary>Nothing to do (job already terminal, or owned by another provider).</summary>
    NoWork,
}

public sealed record AsrJobProcessResult(
    AsrJob Job,
    AsrJobOutcome Outcome,
    int SegmentCount,
    string? Message);

/// <summary>
/// Drives one persistent ASR job from its stored state to a terminal or resumable
/// state (<c>docs/ARCHITECTURE.md</c> section 12).
/// </summary>
/// <remarks>
/// <para>
/// The SQLite job row is authoritative. Every state change is persisted before the
/// next side effect, so a process killed at any point resumes from the stored state
/// rather than from memory. Polly is not involved: it only smooths transient HTTP
/// execution inside the provider adapter.
/// </para>
/// <para>
/// A job in <c>submitting</c> at startup is treated as accepted, because the provider
/// request id is the provider's task identifier and was persisted before the request
/// was sent; recovery therefore polls instead of re-submitting, which is what keeps a
/// crash from paying for the same audio twice.
/// </para>
/// </remarks>
public sealed class AsrJobProcessor
{
    private const string ConfigurationErrorCode = "asr.configuration";
    private const string NormalizationErrorCode = "asr.normalization_failed";
    private const string TaskNotFoundErrorCode = "provider.task_not_found";

    /// <summary>
    /// Upper bound on the submit/poll passes one drain performs for a single job. Reaching it
    /// is impossible for a healthy provider (one pass submits, the next polls) and would only
    /// mean a store that keeps reporting a non-terminal status for a job nothing can advance.
    /// </summary>
    private const int MaxPassesPerJob = 4;

    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IAsrJobStore _jobs;
    private readonly IAsrProvider _provider;
    private readonly IAsrResponseNormalizer _normalizer;
    private readonly ITranscriptStore _transcripts;
    private readonly ISessionStore _sessions;
    private readonly ISessionArtifactWriter _artifacts;
    private readonly AsrJobProcessorOptions _options;

    /// <summary>
    /// Latched by <see cref="RunDueAsync"/> when a submit failed because the provider could not
    /// be reached, and cleared as soon as a drain gets through again.
    /// </summary>
    private bool _providerUnreachable;

    public AsrJobProcessor(
        IAsrJobStore jobs,
        IAsrProvider provider,
        IAsrResponseNormalizer normalizer,
        ITranscriptStore transcripts,
        ISessionStore sessions,
        ISessionArtifactWriter artifacts,
        AsrJobProcessorOptions options)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        _transcripts = transcripts ?? throw new ArgumentNullException(nameof(transcripts));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DataRoot);
    }

    /// <summary>The provider this processor serves; jobs for other providers are left alone.</summary>
    public string ProviderName => _provider.Name;

    /// <summary>MeetCap data root this processor resolves session artifacts under.</summary>
    public string DataRoot => _options.DataRoot;

    /// <summary>Every job of a session, oldest first.</summary>
    public IReadOnlyList<AsrJob> ListJobs(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return _jobs.ListBySession(sessionId);
    }

    /// <summary>
    /// Jobs of a session that still need work, including a <c>pending</c> job whose retry
    /// schedule has not come due yet.
    /// </summary>
    /// <remarks>
    /// Deliberately different from <see cref="RunDueAsync"/>'s due list: "does anything
    /// remain" is the question a caller asks before declaring a session finished, and a job
    /// waiting out its backoff still remains (<c>docs/RELIABILITY.md</c> section 9).
    /// </remarks>
    public int CountOutstanding(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return _jobs.ListBySession(sessionId).Count(job => AsrJobStatuses.IsResumable(job.Status));
    }

    /// <summary>
    /// Resumes every job whose durable state still needs work, oldest first.
    /// </summary>
    /// <remarks>
    /// This is the restart entry point: it is intentionally stateless beyond the job
    /// store, so <c>meetcap asr resume</c> after a crash does the same thing the
    /// original process would have done.
    /// </remarks>
    public async Task<IReadOnlyList<AsrJobProcessResult>> RunDueAsync(
        int maxJobs,
        string? sessionId = null,
        CancellationToken cancellationToken = default,
        bool ignoreRetrySchedule = false)
    {
        if (maxJobs <= 0)
        {
            return Array.Empty<AsrJobProcessResult>();
        }

        var due = _jobs.ListResumable(Now(), maxJobs, sessionId);

        // `meetcap asr resume --force` is the operator's statement that the reason for the
        // backoff is gone — the network is back — so a job waiting out its durable
        // `next_retry_at` is processed anyway. The schedule is otherwise respected, which is
        // what keeps repeated commands from creating a retry storm.
        if (ignoreRetrySchedule)
        {
            var scheduled = _jobs
                .ListResumable(DateTimeOffset.MaxValue, int.MaxValue, sessionId)
                .Where(job => job.Status is AsrJobStatus.RetryWait)
                .Where(job => due.All(candidate => !string.Equals(candidate.Id, job.Id, StringComparison.Ordinal)))
                .OrderBy(job => job.NextRetryAt ?? DateTimeOffset.MinValue)
                .ThenBy(job => job.Id, StringComparer.Ordinal)
                .Take(maxJobs);

            due = due.Concat(scheduled).ToList();
        }

        var results = new List<AsrJobProcessResult>(due.Count);
        foreach (var job in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.Equals(job.Provider, _provider.Name, StringComparison.Ordinal))
            {
                results.Add(new AsrJobProcessResult(
                    job,
                    AsrJobOutcome.NoWork,
                    SegmentCount: 0,
                    Message: $"Job '{job.Id}' targets provider '{job.Provider}', not '{_provider.Name}'."));
                continue;
            }

            var result = await ProcessAsync(job, cancellationToken).ConfigureAwait(false);

            // One pass advances a job by one step (submit, then poll), so a drain keeps passing
            // over the same job until it reaches a state that is genuinely waiting on something
            // — a terminal state, `retry_wait` with its durable backoff, or `polling` because the
            // provider is still working. Stopping at a bare `submitted` would leave that job
            // unattended even though its audio is ready and the provider is reachable.
            var passes = 0;
            while (result.Outcome == AsrJobOutcome.NoWork && ++passes < MaxPassesPerJob)
            {
                var current = _jobs.Get(result.Job.Id);
                if (current is null || !AsrJobStatuses.IsResumable(current.Status))
                {
                    break;
                }

                result = await ProcessAsync(current, cancellationToken).ConfigureAwait(false);
            }

            results.Add(result);

            // A transport failure means the provider is unreachable right now, so the rest of
            // this drain would only repeat the same failure. The remaining jobs stay durable and
            // due — the durable `next_retry_at` schedule, not this loop, is what paces them — and
            // the next drain tries again (docs/RELIABILITY.md section 9).
            if (result.Outcome == AsrJobOutcome.AwaitingRetry && IsTransportFailure(result.Job.ErrorCode))
            {
                _providerUnreachable = true;
                break;
            }
        }

        // A submit or poll that actually completed proves the provider is reachable again.
        if (_providerUnreachable && results.Exists(r => r.Outcome != AsrJobOutcome.AwaitingRetry))
        {
            _providerUnreachable = false;
        }

        return results;
    }

    /// <summary>
    /// True when the last drain stopped early because the provider could not be reached.
    /// </summary>
    /// <remarks>
    /// Informational. Nothing is dropped: the untouched jobs keep their durable state and their
    /// <c>next_retry_at</c> schedule, which is what paces them
    /// (<c>docs/ARCHITECTURE.md</c> section 12).
    /// </remarks>
    public bool ProviderUnreachable => _providerUnreachable;

    /// <summary>
    /// True for a failure that says "the provider could not be reached", as opposed to a
    /// provider-side rejection of the audio itself.
    /// </summary>
    private static bool IsTransportFailure(string? errorCode)
    {
        if (string.IsNullOrEmpty(errorCode))
        {
            return false;
        }

        if (string.Equals(errorCode, TaskNotFoundErrorCode, StringComparison.Ordinal))
        {
            // The provider answered, so it is reachable; the task itself is what is missing.
            return false;
        }

        return errorCode.StartsWith("http.", StringComparison.Ordinal)
            || string.Equals(errorCode, "audio.unreadable", StringComparison.Ordinal);
    }

    /// <summary>Advances one job by the smallest useful amount of work.</summary>
    public async Task<AsrJobProcessResult> ProcessAsync(AsrJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (AsrJobStatuses.IsTerminal(job.Status))
        {
            return new AsrJobProcessResult(job, AsrJobOutcome.NoWork, 0, $"Job '{job.Id}' is already {job.Status}.");
        }


        var recovered = AsrJobTransitions.RecoverAfterRestart(job, Now());
        if (recovered.Status != job.Status)
        {
            _jobs.Update(recovered);
            job = recovered;
        }

        var paths = new SessionArtifactPaths(_options.DataRoot, job.SessionId);
        var request = BuildRequest(job, paths);
        var submission = RehydrateSubmission(job);

        if (job.Status is AsrJobStatus.Pending or AsrJobStatus.RetryWait)
        {
            var submitResult = await SubmitAsync(job, paths, request, cancellationToken).ConfigureAwait(false);
            if (submitResult is not null)
            {
                return submitResult;
            }

            submission = RehydrateSubmission(_jobs.Get(job.Id) ?? job);
        }

        var current = _jobs.Get(job.Id) ?? job;
        if (current.Status == AsrJobStatus.Submitted)
        {
            current = AsrJobTransitions.BeginPolling(current, Now());
            _jobs.Update(current);
        }

        return await PollAsync(current, paths, request, submission, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AsrJobProcessResult?> SubmitAsync(
        AsrJob job,
        SessionArtifactPaths paths,
        AsrFileRequest request,
        CancellationToken cancellationToken)
    {
        // Persist "submitting" before the request: if the process dies here, recovery
        // sees the attempt and the persisted provider request id instead of guessing.
        job = AsrJobTransitions.BeginSubmit(job, Now());
        _jobs.Update(job);

        AsrSubmission submission;
        try
        {
            submission = await _provider.SubmitFileAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (AsrTransientException ex)
        {
            return HandleTransient(job, ex.Code, ex.Message);
        }
        catch (AsrPermanentException ex)
        {
            return Fail(job, ex.Code, ex.Message);
        }
        catch (AsrConfigurationException ex)
        {
            return Fail(job, ConfigurationErrorCode, ex.Message);
        }

        var requestPath = paths.JobRequestJson(job.Id);
        WriteText(requestPath, submission.SanitizedRequestJson);

        // The provider's trace id is retained with the job so a support request about this
        // task can name it without re-reading the exchange (docs/ASR_STRATEGY.md section 13).
        job = AsrJobTransitions.RecordProviderLogId(job, submission.ProviderLogId, Now());

        job = AsrJobTransitions.MarkSubmitted(job, requestPath, Now());
        _jobs.Update(job);

        _artifacts.AppendEvent(
            paths,
            SessionEvents.AsrJobSubmitted,
            job.StartMs,
            new Dictionary<string, object?>
            {
                ["job_id"] = job.Id,
                ["provider"] = job.Provider,
                ["provider_request_id"] = submission.ProviderRequestId,
                ["provider_log_id"] = job.ProviderLogId,
                ["attempt"] = job.AttemptCount,
            });

        return null;
    }

    private async Task<AsrJobProcessResult> PollAsync(
        AsrJob job,
        SessionArtifactPaths paths,
        AsrFileRequest request,
        AsrSubmission submission,
        CancellationToken cancellationToken)
    {
        if (job.Status != AsrJobStatus.Polling)
        {
            return new AsrJobProcessResult(
                job,
                AsrJobOutcome.NoWork,
                SegmentCount: 0,
                Message: $"Job '{job.Id}' is '{AsrJobStatuses.ToWire(job.Status)}' and cannot be polled.");
        }

        var deadline = Now() + _options.PollTimeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            AsrPollResult poll;
            try
            {
                poll = await _provider.GetResultAsync(submission, request, cancellationToken).ConfigureAwait(false);
            }
            catch (AsrTransientException ex)
            {
                return HandleTransient(job, ex.Code, ex.Message);
            }
            catch (AsrPermanentException ex)
            {
                return Fail(job, ex.Code, ex.Message);
            }
            catch (AsrConfigurationException ex)
            {
                return Fail(job, ConfigurationErrorCode, ex.Message);
            }

            switch (poll.State)
            {
                case AsrPollState.Pending:
                    if (Now() >= deadline)
                    {
                        return new AsrJobProcessResult(
                            job,
                            AsrJobOutcome.StillRunning,
                            SegmentCount: 0,
                            Message:
                                $"Provider is still processing job '{job.Id}'. Its state is persisted; " +
                                "run 'meetcap asr resume' to continue polling.");
                    }

                    await DelayAsync(_options.PollInterval, cancellationToken).ConfigureAwait(false);
                    continue;

                case AsrPollState.TaskNotFound:
                    return HandleTransient(
                        job,
                        TaskNotFoundErrorCode,
                        $"Provider '{_provider.Name}' does not know task '{submission.ProviderRequestId}'; " +
                        "the submission will be retried.");
                case AsrPollState.Failed:
                    var error = poll.Error
                        ?? new AsrProviderError("provider.unknown", "Provider reported a failure.", IsTransient: false);
                    return error.IsTransient
                        ? HandleTransient(job, error.Code, error.Message)
                        : Fail(job, error.Code, error.Message);
                case AsrPollState.Completed:
                    return Complete(job, paths, poll.Completion!, cancellationToken);
                default:
                    return Fail(job, "provider.unknown_state", $"Unhandled poll state '{poll.State}'.");
            }
        }
    }

    private AsrJobProcessResult Complete(
        AsrJob job,
        SessionArtifactPaths paths,
        AsrCompletion completion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Retain the raw provider response before any parsing, so a parser fix never
        // requires re-billing the same audio (docs/ARCHITECTURE.md section 14).
        var rawResponsePath = paths.JobResponseJson(job.Id);
        WriteText(rawResponsePath, completion.RawResponseJson);
        job = AsrJobTransitions.RecordRawResponse(job, rawResponsePath, Now());
        job = AsrJobTransitions.RecordProviderLogId(job, completion.ProviderLogId, Now());
        _jobs.Update(job);

        AsrNormalizationResult normalized;
        try
        {
            normalized = _normalizer.Normalize(
                completion.RawResponseJson,
                new AsrNormalizationContext
                {
                    SessionId = job.SessionId,
                    JobId = job.Id,
                    Source = job.Source,
                    // The provider's timestamps are relative to the artifact it was given.
                    // For a live ASR batch that artifact is a window into the session, so the
                    // batch's start position on the session timeline is the offset that puts
                    // its segments where they were actually spoken (docs/DATA_MODEL.md
                    // section 6).
                    StartOffsetMs = job.StartMs,
                });
        }
        catch (AsrNormalizationException ex)
        {
            return Fail(
                job,
                NormalizationErrorCode,
                $"{ex.Message} The raw provider response is retained at '{rawResponsePath}', " +
                "so a parser fix can rebuild the transcript without re-billing.");
        }

        if (normalized.ErrorCode is not null)
        {
            return Fail(job, normalized.ErrorCode, normalized.ErrorMessage ?? "Provider reported an error.");
        }

        var normalizedPath = paths.JobNormalizedJsonl(job.Id);
        _transcripts.WriteJsonl(normalizedPath, normalized.Segments);

        // raw.jsonl is the stable machine interface and is always written, whatever
        // [transcript] says (docs/CONFIGURATION.md section 10). It is REBUILT from the
        // per-job normalized artifacts rather than appended to, so re-completing a job
        // replaces its contribution instead of duplicating it. That matters because the
        // terminal status is persisted after these writes: a process killed in between
        // leaves the job resumable, the provider returns the same immutable result, and
        // this method runs again (docs/ARCHITECTURE.md section 14).
        RebuildRawTranscript(paths, job.SessionId);

        if (_options.WriteMarkdown)
        {
            var all = _transcripts.ReadJsonl(paths.RawTranscriptJsonl);
            _transcripts.WriteMarkdown(
                paths.LiveTranscriptMarkdown,
                job.SessionId,
                all,
                _options.TranscriptOptions);
        }

        var now = Now();
        job = AsrJobTransitions.MarkSucceeded(
            job,
            now,
            rawResponsePath,
            normalizedPath,
            normalized.SpeakerInfoReturned);
        job = AsrJobTransitions.WithEstimatedCost(
            job,
            AsrCostEstimator.Estimate(job.DurationMs, _options.CostPerHourCny),
            now);
        _jobs.Update(job);

        // The completion event is emitted only after the terminal status is durable, so a
        // resumed re-completion cannot append a second asr.job.completed record: once the
        // row is `succeeded` the queue no longer lists the job for work.
        _artifacts.AppendEvent(
            paths,
            SessionEvents.AsrJobCompleted,
            job.EndMs,
            new Dictionary<string, object?>
            {
                ["job_id"] = job.Id,
                ["segments"] = normalized.Segments.Count,
                ["speaker_info_requested"] = job.SpeakerInfoRequested,
                ["speaker_info_returned"] = job.SpeakerInfoReturned,
                ["estimated_cost_cny"] = job.EstimatedCostCny,
                ["provider_log_id"] = job.ProviderLogId,
                ["raw_response_path"] = rawResponsePath,
            });

        CompleteSessionIfDone(job.SessionId, paths);
        return new AsrJobProcessResult(job, AsrJobOutcome.Succeeded, normalized.Segments.Count, null);
    }

    /// <summary>
    /// Rebuilds <c>transcript/raw.jsonl</c> as the merged, session-relative timeline of
    /// every job's <c>normalized.jsonl</c> for the session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deriving the session transcript from the per-job artifacts keeps the write
    /// idempotent: finishing the same job twice produces the same file, because the job's
    /// own contribution is replaced rather than appended. Appending would duplicate every
    /// segment after a crash between the transcript write and the terminal status update.
    /// </para>
    /// <para>
    /// An online session transcribes the microphone and loopback tracks independently, so
    /// the segments arrive from two per-track batch streams. They are merged onto one
    /// session-relative timeline (sorted by <c>start_ms</c>) without deleting overlapping
    /// speech and without losing the source each segment came from
    /// (docs/ARCHITECTURE.md section 16, docs/ROADMAP.md M5). For a single-track session
    /// the merge is a stable no-op because a track's segments are already ordered.
    /// </para>
    /// </remarks>
    private void RebuildRawTranscript(SessionArtifactPaths paths, string sessionId)
    {
        var segments = new List<TranscriptSegment>();
        foreach (var sessionJob in _jobs.ListBySession(sessionId))
        {
            var normalizedPath = string.IsNullOrEmpty(sessionJob.NormalizedResultPath)
                ? paths.JobNormalizedJsonl(sessionJob.Id)
                : sessionJob.NormalizedResultPath;

            if (!File.Exists(normalizedPath))
            {
                continue;
            }

            segments.AddRange(_transcripts.ReadJsonl(normalizedPath));
        }

        var merged = TranscriptMerger.Merge(segments);
        _transcripts.WriteJsonl(paths.RawTranscriptJsonl, merged);
    }

    private AsrJobProcessResult HandleTransient(AsrJob job, string code, string message)
    {
        var now = Now();
        if (_options.RetryPolicy.CanRetry(job.AttemptCount))
        {
            var retried = AsrJobTransitions.ScheduleRetry(job, now, code, message, _options.RetryPolicy);
            _jobs.Update(retried);
            _artifacts.AppendEvent(
                new SessionArtifactPaths(_options.DataRoot, job.SessionId),
                SessionEvents.AsrJobRetryWait,
                job.EndMs,
                new Dictionary<string, object?>
                {
                    ["job_id"] = job.Id,
                    ["attempt"] = retried.AttemptCount,
                    ["next_retry_at"] = retried.NextRetryAt?.ToString("o"),
                    ["error_code"] = code,
                });

            return new AsrJobProcessResult(retried, AsrJobOutcome.AwaitingRetry, 0, message);
        }

        return Fail(
            job,
            code,
            $"{message} Retry budget of {_options.RetryPolicy.MaxAttempts} attempt(s) is exhausted.");
    }

    private AsrJobProcessResult Fail(AsrJob job, string code, string message)
    {
        var failed = AsrJobTransitions.MarkFailed(job, Now(), code, message);
        _jobs.Update(failed);

        _artifacts.AppendEvent(
            new SessionArtifactPaths(_options.DataRoot, job.SessionId),
            SessionEvents.AsrJobFailed,
            job.EndMs,
            new Dictionary<string, object?>
            {
                ["job_id"] = job.Id,
                ["attempts"] = failed.AttemptCount,
                ["error_code"] = code,
                ["error_message"] = message,
            });

        return new AsrJobProcessResult(failed, AsrJobOutcome.Failed, 0, message);
    }

    /// <summary>
    /// Marks the session complete only when every job for it reached a terminal
    /// success. A failed job leaves the session in <c>PROCESSING</c>: the audio is
    /// safe and the job can still be retried, which is the recoverability rule from
    /// <c>docs/RELIABILITY.md</c> section 9.
    /// </summary>
    private void CompleteSessionIfDone(string sessionId, SessionArtifactPaths paths)
    {
        var session = _sessions.Get(sessionId);
        if (session is null || !string.Equals(session.Status, SessionStatus.Processing, StringComparison.Ordinal))
        {
            return;
        }

        var jobs = _jobs.ListBySession(sessionId);
        if (jobs.Count == 0 || jobs.Any(job =>
                job.Status is not (AsrJobStatus.Succeeded or AsrJobStatus.Cancelled)))
        {
            return;
        }

        var now = Now();
        _sessions.Update(session.WithStatus(SessionStatus.Completed, now));
        _artifacts.AppendEvent(
            paths,
            SessionEvents.SessionCompleted,
            session.DurationMs,
            new Dictionary<string, object?> { ["session_id"] = sessionId });
    }

    private AsrFileRequest BuildRequest(AsrJob job, SessionArtifactPaths paths) => new()
    {
        JobId = job.Id,
        SessionId = job.SessionId,
        Source = job.Source,
        InputArtifactPath = paths.ResolveRelative(job.InputArtifact),
        AudioFormat = Path.GetExtension(job.InputArtifact).TrimStart('.').ToLowerInvariant(),
        // A live batch's artifact starts partway through the session; the job's stored
        // start position is what maps provider timestamps back to the session timeline.
        StartOffsetMs = job.StartMs,
        DurationMs = job.DurationMs,
        ProviderRequestId = job.ProviderRequestId,
        RequestSpeakerInfo = job.SpeakerInfoRequested,
    };

    private AsrSubmission RehydrateSubmission(AsrJob job)
    {
        var sanitized = "{}";
        if (job.RequestMetadataPath is not null && File.Exists(job.RequestMetadataPath))
        {
            sanitized = File.ReadAllText(job.RequestMetadataPath);
        }

        return new AsrSubmission
        {
            ProviderRequestId = job.ProviderRequestId,
            SanitizedRequestJson = sanitized,
        };
    }

    private DateTimeOffset Now() => _options.TimeProvider.GetUtcNow();

    private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        _options.Delay is not null
            ? _options.Delay(delay, cancellationToken)
            : Task.Delay(delay, cancellationToken);

    private static void WriteText(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, content, s_utf8);
    }
}
