using MeetCap.Core.Asr;
using MeetCap.Core.Sessions;

namespace MeetCap.Asr.Tests;

/// <summary>In-memory provider boundary. No test reaches the live service.</summary>
internal sealed class FakeAsrProvider : IAsrProvider
{
    private readonly Queue<AsrPollResult> _polls = new();

    public string Name => "volcengine";

    public List<AsrFileRequest> Submissions { get; } = new();

    public List<string> PolledRequestIds { get; } = new();

    /// <summary>Overrides the submit behaviour; throw to script a failure.</summary>
    public Func<AsrFileRequest, AsrSubmission>? OnSubmit { get; set; }

    /// <summary>Overrides polling. When null the queued results are used.</summary>
    public Func<AsrFileRequest, AsrPollResult>? OnPoll { get; set; }

    /// <summary>
    /// Overrides the release of a staged transport copy. The default reports that nothing was
    /// staged, which is what a provider with an inline-only transport does.
    /// </summary>
    public Func<AsrJob, AsrAudioRelease>? OnRelease { get; set; }

    /// <summary>Jobs whose staged copy this provider was asked to release, in order.</summary>
    public List<AsrJob> Released { get; } = new();

    public void EnqueuePoll(params AsrPollResult[] results)
    {
        foreach (var result in results)
        {
            _polls.Enqueue(result);
        }
    }

    public Task<AsrSubmission> SubmitFileAsync(AsrFileRequest request, CancellationToken cancellationToken = default)
    {
        Submissions.Add(request);
        if (OnSubmit is not null)
        {
            return Task.FromResult(OnSubmit(request));
        }

        return Task.FromResult(new AsrSubmission
        {
            ProviderRequestId = request.ProviderRequestId,
            SanitizedRequestJson = "{\"job_id\":\"" + request.JobId + "\"}",
        });
    }

    public Task<AsrPollResult> GetResultAsync(
        AsrSubmission submission,
        AsrFileRequest request,
        CancellationToken cancellationToken = default)
    {
        PolledRequestIds.Add(submission.ProviderRequestId);
        if (OnPoll is not null)
        {
            return Task.FromResult(OnPoll(request));
        }

        return Task.FromResult(_polls.Count > 0 ? _polls.Dequeue() : AsrPollResult.Pending());
    }

    public Task<AsrAudioRelease> ReleaseAudioAsync(AsrJob job, CancellationToken cancellationToken = default)
    {
        Released.Add(job);
        return Task.FromResult(OnRelease is not null ? OnRelease(job) : AsrAudioRelease.NotNeeded);
    }
}

/// <summary>Deterministic normalizer stand-in, so processor tests do not depend on provider JSON.</summary>
internal sealed class FakeNormalizer : IAsrResponseNormalizer
{
    public Func<string, AsrNormalizationContext, AsrNormalizationResult>? OnNormalize { get; set; }

    public AsrNormalizationResult Normalize(string rawResponseJson, AsrNormalizationContext context)
    {
        if (OnNormalize is not null)
        {
            return OnNormalize(rawResponseJson, context);
        }

        return new AsrNormalizationResult
        {
            Segments = new[]
            {
                new Core.Transcripts.TranscriptSegment
                {
                    SegmentId = "seg_1",
                    SessionId = context.SessionId,
                    Source = context.Source,
                    StartMs = 0,
                    EndMs = 1000,
                    RawText = "hello",
                    SpeakerLabel = "speaker_1",
                    ProviderJobId = context.JobId,
                },
            },
            SpeakerInfoReturned = true,
        };
    }
}

internal sealed class InMemoryAsrJobStore : IAsrJobStore
{
    private readonly Dictionary<string, AsrJob> _jobs = new(StringComparer.Ordinal);

    /// <summary>
    /// Simulates a process death at a chosen persistence point: when this predicate returns
    /// true the update is abandoned and an exception is thrown, exactly as if the process had
    /// been killed between a side effect and the status write that would have recorded it.
    /// </summary>
    public Func<AsrJob, bool>? AbandonUpdateWhen { get; set; }

    public void Create(AsrJob job) => _jobs[job.Id] = job;

    /// <summary>
    /// Drops a job row. Used by tests that model a process which died before a job existed,
    /// so a later recovery pass sees exactly what a fresh process would see.
    /// </summary>
    public void Remove(string jobId) => _jobs.Remove(jobId);

    public AsrJob? Get(string jobId) => _jobs.TryGetValue(jobId, out var job) ? job : null;

    public IReadOnlyList<AsrJob> ListBySession(string sessionId) =>
        _jobs.Values.Where(j => j.SessionId == sessionId).OrderBy(j => j.CreatedAt).ToArray();

    public IReadOnlyList<AsrJob> ListResumable(DateTimeOffset now, int limit, string? sessionId = null)
    {
        if (limit <= 0)
        {
            return Array.Empty<AsrJob>();
        }

        return _jobs.Values
            .Where(j => AsrJobStatuses.IsResumable(j.Status))
            .Where(j => j.NextRetryAt is null || j.NextRetryAt <= now)
            .Where(j => sessionId is null || j.SessionId == sessionId)
            .OrderBy(j => j.CreatedAt)
            .ThenBy(j => j.Id, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
    }

    public void Update(AsrJob job)
    {
        if (AbandonUpdateWhen is not null && AbandonUpdateWhen(job))
        {
            // Models a process death between a side effect and its status write: the update
            // never lands, and the caller sees the process go away.
            throw new InvalidOperationException(
                $"Simulated process death before persisting job '{job.Id}' as {job.Status}.");
        }

        _jobs[job.Id] = job;
    }

    public IReadOnlyList<AsrJob> ListCleanupPending(int limit, string? sessionId = null)
    {
        if (limit <= 0)
        {
            return Array.Empty<AsrJob>();
        }

        return _jobs.Values
            .Where(j => j.TosCleanupPending)
            .Where(j => AsrJobStatuses.IsTerminal(j.Status))
            .Where(j => sessionId is null || j.SessionId == sessionId)
            .OrderBy(j => j.CreatedAt)
            .ThenBy(j => j.Id, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
    }

    public int CountByStatus(AsrJobStatus status) => _jobs.Values.Count(j => j.Status == status);
}

internal sealed class InMemorySessionStore : ISessionStore
{
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    public void Create(Session session) => _sessions[session.Id] = session;

    public Session? Get(string sessionId) => _sessions.TryGetValue(sessionId, out var session) ? session : null;

    public void Update(Session session) => _sessions[session.Id] = session;

    public int CountActive() => _sessions.Values.Count(s => SessionStatus.IsActive(s.Status));
}

/// <summary>Records events instead of writing events.jsonl; session.json is not the concern here.</summary>
internal sealed class RecordingArtifactWriter : ISessionArtifactWriter
{
    public List<string> Events { get; } = new();

    public void EnsureLayout(SessionArtifactPaths paths) => Directory.CreateDirectory(paths.SessionDirectory);

    public string WriteSessionDocument(SessionArtifactPaths paths, SessionDocument document)
    {
        EnsureLayout(paths);
        File.WriteAllText(paths.SessionJson, "{}");
        return paths.SessionJson;
    }

    public SessionDocument? ReadSessionDocument(SessionArtifactPaths paths) => null;

    public void AppendEvent(
        SessionArtifactPaths paths,
        string name,
        long atMs,
        IReadOnlyDictionary<string, object?>? details = null) => Events.Add(name);
}
