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

    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IAsrJobStore _jobs;
    private readonly IAsrProvider _provider;
    private readonly IAsrResponseNormalizer _normalizer;
    private readonly ITranscriptStore _transcripts;
    private readonly ISessionStore _sessions;
    private readonly ISessionArtifactWriter _artifacts;
    private readonly AsrJobProcessorOptions _options;

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
        CancellationToken cancellationToken = default)
    {
        if (maxJobs <= 0)
        {
            return Array.Empty<AsrJobProcessResult>();
        }

        var due = _jobs.ListResumable(Now(), maxJobs, sessionId);
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

            results.Add(await ProcessAsync(job, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>Advances one job by the smallest useful amount of work.</summary>
    public async Task<AsrJobProcessResult> ProcessAsync(AsrJob job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (job.ProviderRequestId is null)
        {
            // Invariant enforced by the migration and the job store: without a stable
            // provider task id a restart could not avoid submitting twice.
            var broken = AsrJobTransitions.MarkFailed(
                job,
                Now(),
                ConfigurationErrorCode,
                $"Job '{job.Id}' has no provider request id and cannot be resumed safely.");
            _jobs.Update(broken);
            return new AsrJobProcessResult(broken, AsrJobOutcome.Failed, 0, broken.ErrorMessage);
        }

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
        // [transcript] says (docs/CONFIGURATION.md section 10).
        _transcripts.AppendJsonl(paths.RawTranscriptJsonl, normalized.Segments);

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
                ["raw_response_path"] = rawResponsePath,
            });

        CompleteSessionIfDone(job.SessionId, paths);
        return new AsrJobProcessResult(job, AsrJobOutcome.Succeeded, normalized.Segments.Count, null);
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
        DurationMs = job.DurationMs,
        ProviderRequestId = job.ProviderRequestId!,
        ServiceTier = job.Tier,
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
            ProviderRequestId = job.ProviderRequestId
                ?? throw new InvalidOperationException($"Job '{job.Id}' has no provider request id."),
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
