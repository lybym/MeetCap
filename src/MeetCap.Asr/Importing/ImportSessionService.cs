namespace MeetCap.Asr.Importing;

using System.Security.Cryptography;
using System.Text.Json;
using MeetCap.Core.Asr;
using MeetCap.Core.Configuration;
using MeetCap.Core.Ids;
using MeetCap.Core.Media;
using MeetCap.Core.Sessions;
using MeetCap.Core.Transcripts;

/// <summary>One <c>meetcap import</c> invocation.</summary>
public sealed record ImportRequest
{
    /// <summary>Path the user supplied. The original is only ever read.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Explicit title; falls back to the file name, then to the configured default.</summary>
    public string? Title { get; init; }

    /// <summary>Test/pre-allocation hooks; normally left null so ids are generated.</summary>
    public string? SessionId { get; init; }

    public string? JobId { get; init; }

    public string? ProviderRequestId { get; init; }
}

/// <summary>Import configuration snapshot for one invocation.</summary>
public sealed record ImportOptions
{
    public required string DataRoot { get; init; }

    /// <summary>Provider name recorded on the job, e.g. <c>volcengine</c>.</summary>
    public required string ProviderName { get; init; }

    public string DefaultTitle { get; init; } = "Untitled Meeting";

    /// <summary>Configured <c>asr.volcengine.request_speaker_info</c>.</summary>
    public bool RequestSpeakerInfo { get; init; } = true;

    public double CostPerHourCny { get; init; } = 0.8;

    public string ConfigSnapshotJson { get; init; } = "{}";

    public int ConfigVersion { get; init; } = SchemaVersion.Current;

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>Outcome of one import.</summary>
public sealed record ImportResult
{
    public required string SessionId { get; init; }

    public required string SessionDirectory { get; init; }

    public required string JobId { get; init; }

    public required AsrJobStatus JobStatus { get; init; }

    public required bool Succeeded { get; init; }

    /// <summary>True while the provider is still working and the job is resumable.</summary>
    public required bool StillRunning { get; init; }

    public int SegmentCount { get; init; }

    public required string RawTranscriptPath { get; init; }

    public string? MarkdownTranscriptPath { get; init; }

    public required MediaInfo SourceMedia { get; init; }

    public required bool Normalized { get; init; }

    public string? NormalizedArtifactPath { get; init; }

    public required double EstimatedCostCny { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Implements <c>meetcap import</c>: inspect/normalize a file through
/// <see cref="IMediaPipeline"/>, create a normal session with
/// <c>source_type=import</c>, queue a persistent file-ASR job, and produce
/// <c>transcript/raw.jsonl</c> plus the Markdown transcript
/// (<c>docs/ARCHITECTURE.md</c> section 21).
/// </summary>
/// <remarks>
/// <para>
/// The original file is never modified, moved, or deleted: it is copied into the
/// session artifact directory and the mapping is recorded in <c>session.json</c>.
/// </para>
/// <para>
/// Session state is only written after the provider has been constructed by the
/// caller, so invalid credentials or configuration fail before any session exists
/// (<c>docs/DEVELOPMENT.md</c> section 7).
/// </para>
/// </remarks>
public sealed class ImportSessionService
{
    private static readonly JsonSerializerOptions s_snapshotJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IMediaPipeline _media;
    private readonly ISessionStore _sessions;
    private readonly IAsrJobStore _jobs;
    private readonly ISessionArtifactWriter _artifacts;
    private readonly ITranscriptStore _transcripts;
    private readonly AsrJobProcessor _processor;
    private readonly ImportOptions _options;

    public ImportSessionService(
        IMediaPipeline media,
        ISessionStore sessions,
        IAsrJobStore jobs,
        ISessionArtifactWriter artifacts,
        ITranscriptStore transcripts,
        AsrJobProcessor processor,
        ImportOptions options)
    {
        _media = media ?? throw new ArgumentNullException(nameof(media));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _transcripts = transcripts ?? throw new ArgumentNullException(nameof(transcripts));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ProviderName);
    }

    public async Task<ImportResult> ImportAsync(
        ImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sourcePath = Path.GetFullPath(request.SourcePath);
        if (!File.Exists(sourcePath))
        {
            throw new MediaProbeException($"Import source not found: {sourcePath}");
        }

        // Inspect first: an unusable file must fail before a session is created, so a
        // failed import never leaves an empty session behind.
        var sourceMedia = _media.Inspect(sourcePath);
        var plan = MediaNormalizationPlanner.Plan(sourceMedia);

        var now = Now();
        var sessionId = request.SessionId ?? Ids.NewSessionId();
        var jobId = request.JobId ?? Ids.NewJobId();
        var providerRequestId = request.ProviderRequestId ?? Ids.NewProviderRequestId();
        var paths = new SessionArtifactPaths(_options.DataRoot, sessionId);

        var title = ResolveTitle(request.Title, sourcePath);
        var session = new Session
        {
            Id = sessionId,
            Title = title,
            Mode = SessionMode.Import,
            SourceType = SessionSourceType.Import,
            Status = SessionStatus.Processing,
            StartedAt = now,
            DurationMs = sourceMedia.DurationMs,
            ConfigSnapshotJson = _options.ConfigSnapshotJson,
            ConfigVersion = _options.ConfigVersion,
            Tracks = new[] { AudioTrackName.Import },
            CreatedAt = now,
            UpdatedAt = now,
        };

        // The session.json document is the durable record (docs/DATA_MODEL.md section 3), so
        // it is (re)written as soon as the session exists and again as each artifact lands.
        // Copying or normalizing can still fail, and the user must be able to see the
        // half-materialized session rather than an orphaned row with no durable document.
        var artifacts = new List<SourceArtifact>();

        void PersistSessionDocument() =>
            _artifacts.WriteSessionDocument(paths, SessionDocument.From(session, artifacts));

        _artifacts.EnsureLayout(paths);
        PersistSessionDocument();
        _sessions.Create(session);
        _artifacts.AppendEvent(
            paths,
            SessionEvents.SessionCreated,
            0,
            new Dictionary<string, object?>
            {
                ["session_id"] = sessionId,
                ["mode"] = session.Mode,
                ["source_type"] = session.SourceType,
                ["title"] = title,
                ["provider"] = _options.ProviderName,
            });

        artifacts.Add(CopySourceArtifact(paths, sourcePath));
        PersistSessionDocument();
        _artifacts.AppendEvent(
            paths,
            SessionEvents.SourceImported,
            0,
            new Dictionary<string, object?>
            {
                ["original_path"] = artifacts[0].OriginalPath,
                ["stored_path"] = artifacts[0].StoredPath,
                ["sha256"] = artifacts[0].Sha256,
                ["bytes"] = artifacts[0].ByteLength,
            });

        var inputArtifactPath = artifacts[0].StoredPath;
        if (plan.Required)
        {
            var normalizedPath = paths.ImportAudioFile("normalized.wav");
            await _media.NormalizeAsync(sourcePath, normalizedPath, plan, cancellationToken)
                .ConfigureAwait(false);

            // Re-inspect the derivative: a normalized artifact is only useful if it is
            // actually in the shape the provider accepts.
            var normalizedMedia = _media.Inspect(normalizedPath);
            var confirmed = MediaNormalizationPlanner.Plan(normalizedMedia);
            if (confirmed.Required)
            {
                throw new MediaProbeException(
                    $"Normalized artifact '{normalizedPath}' is still not ASR-ready: {confirmed.Reason}.");
            }

            artifacts.Add(BuildArtifact(SourceArtifact.Roles.Normalized, sourcePath, normalizedPath));
            inputArtifactPath = normalizedPath;
            PersistSessionDocument();

            _artifacts.AppendEvent(
                paths,
                SessionEvents.MediaNormalized,
                0,
                new Dictionary<string, object?>
                {
                    ["required"] = true,
                    ["reason"] = plan.Reason,
                    ["input"] = sourcePath,
                    ["output"] = normalizedPath,
                });
        }

        var job = new AsrJob
        {
            Id = jobId,
            SessionId = sessionId,
            Source = AudioTrackName.Import,
            Provider = _options.ProviderName,
            StartMs = 0,
            EndMs = sourceMedia.DurationMs,
            InputArtifact = paths.ToRelative(inputArtifactPath),
            Status = AsrJobStatus.Pending,
            ProviderRequestId = providerRequestId,
            DurationMs = (int)Math.Min(int.MaxValue, sourceMedia.DurationMs),
            SpeakerInfoRequested = _options.RequestSpeakerInfo,
            EstimatedCostCny = AsrCostEstimator.Estimate(sourceMedia.DurationMs, _options.CostPerHourCny),
            CreatedAt = now,
            UpdatedAt = now,
        };

        _jobs.Create(job);
        _artifacts.AppendEvent(
            paths,
            SessionEvents.AsrJobQueued,
            job.StartMs,
            new Dictionary<string, object?>
            {
                ["job_id"] = job.Id,
                ["source"] = job.Source,
                ["provider"] = job.Provider,
                ["start_ms"] = job.StartMs,
                ["end_ms"] = job.EndMs,
                ["input_artifact"] = job.InputArtifact,
                ["estimated_cost_cny"] = job.EstimatedCostCny,
            });

        var processed = await _processor.ProcessAsync(job, cancellationToken).ConfigureAwait(false);
        var segments = _transcripts.ReadJsonl(paths.RawTranscriptJsonl);

        return new ImportResult
        {
            SessionId = sessionId,
            SessionDirectory = paths.SessionDirectory,
            JobId = jobId,
            JobStatus = processed.Job.Status,
            Succeeded = processed.Outcome == AsrJobOutcome.Succeeded,
            StillRunning = processed.Outcome == AsrJobOutcome.StillRunning,
            SegmentCount = segments.Count,
            RawTranscriptPath = paths.RawTranscriptJsonl,
            MarkdownTranscriptPath = File.Exists(paths.LiveTranscriptMarkdown)
                ? paths.LiveTranscriptMarkdown
                : null,
            SourceMedia = sourceMedia,
            Normalized = plan.Required,
            NormalizedArtifactPath = plan.Required ? inputArtifactPath : null,
            EstimatedCostCny = processed.Job.EstimatedCostCny,
            ErrorCode = processed.Job.ErrorCode,
            ErrorMessage = processed.Message ?? processed.Job.ErrorMessage,
        };
    }

    private string ResolveTitle(string? explicitTitle, string sourcePath)
    {
        if (!string.IsNullOrWhiteSpace(explicitTitle))
        {
            return explicitTitle.Trim();
        }

        var fromFile = Path.GetFileNameWithoutExtension(sourcePath);
        return string.IsNullOrWhiteSpace(fromFile) ? _options.DefaultTitle : fromFile;
    }

    private SourceArtifact CopySourceArtifact(SessionArtifactPaths paths, string sourcePath)
    {
        var fileName = Path.GetFileName(sourcePath);
        var destination = paths.ImportAudioFile(fileName);
        if (File.Exists(destination))
        {
            // A second import must not silently overwrite the first session's source.
            throw new InvalidOperationException(
                $"Session '{paths.SessionId}' already holds an import source named '{fileName}'.");
        }

        File.Copy(sourcePath, destination, overwrite: false);
        return BuildArtifact(SourceArtifact.Roles.Original, sourcePath, destination);
    }

    private static SourceArtifact BuildArtifact(string role, string originalPath, string storedPath)
    {
        var info = new FileInfo(storedPath);
        return new SourceArtifact
        {
            Role = role,
            OriginalPath = originalPath,
            StoredPath = storedPath,
            FileName = info.Name,
            ByteLength = info.Length,
            Sha256 = Sha256OfFile(storedPath),
        };
    }

    /// <summary>SHA-256 of a file, used for provenance and re-verification.</summary>
    public static string Sha256OfFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private DateTimeOffset Now() => _options.TimeProvider.GetUtcNow();

    /// <summary>Serializes the effective configuration for the session snapshot.</summary>
    public static string SnapshotJson(MeetCapConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return JsonSerializer.Serialize(configuration, s_snapshotJson);
    }
}
