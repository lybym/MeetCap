namespace MeetCap.Core.Sessions;

/// <summary>
/// The durable session artifact layout (<c>docs/ARCHITECTURE.md</c> section 19 and
/// <c>docs/DATA_MODEL.md</c> section 2). The filesystem is a first-class
/// persistence layer: SQLite indexes state, these files are the record.
/// </summary>
/// <remarks>
/// This type is pure path arithmetic and therefore domain knowledge, not
/// infrastructure. It exists so every component agrees on where artifacts live
/// without re-deriving the contract.
/// </remarks>
public sealed class SessionArtifactPaths
{
    public SessionArtifactPaths(string dataRoot, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        DataRoot = dataRoot;
        SessionId = sessionId;
        SessionDirectory = Path.Combine(dataRoot, "sessions", sessionId);
    }

    public string DataRoot { get; }

    public string SessionId { get; }

    public string SessionDirectory { get; }

    public string SessionJson => Path.Combine(SessionDirectory, "session.json");

    public string EventsJsonl => Path.Combine(SessionDirectory, "events.jsonl");

    /// <summary>Directory holding the materialized import source and its normalized derivative.</summary>
    public string ImportAudioDirectory => Path.Combine(SessionDirectory, "audio", "import");

    public string AsrJobsDirectory => Path.Combine(SessionDirectory, "asr", "jobs");

    public string AsrBatchesDirectory => Path.Combine(SessionDirectory, "asr", "batches");

    /// <summary>
    /// Sessions under a data root whose <c>asr/batches/</c> surface holds at least one entry,
    /// ordered by session id.
    /// </summary>
    /// <remarks>
    /// Read from the artifact tree rather than from the database on purpose: the state this
    /// answers for is a batch that was finalized before its job row existed, which the database
    /// cannot describe (<c>docs/ARCHITECTURE.md</c> section 10.1).
    /// </remarks>
    public static IReadOnlyList<string> EnumerateSessionsWithBatchArtifacts(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        var sessionsRoot = Path.Combine(Path.GetFullPath(dataRoot), "sessions");
        if (!Directory.Exists(sessionsRoot))
        {
            return Array.Empty<string>();
        }

        var sessions = new List<string>();
        foreach (var directory in Directory.EnumerateDirectories(sessionsRoot))
        {
            var batches = Path.Combine(directory, "asr", "batches");
            if (Directory.Exists(batches) && Directory.EnumerateFileSystemEntries(batches).Any())
            {
                sessions.Add(Path.GetFileName(directory));
            }
        }

        sessions.Sort(StringComparer.Ordinal);
        return sessions;
    }

    /// <summary>
    /// Batch WAV artifacts under this session, as session-relative paths ordered by source then
    /// batch number.
    /// </summary>
    /// <remarks>
    /// A batch is named <c>batch-NNNNNN.wav</c>, so its order within the track is part of its
    /// name and the listing does not depend on directory enumeration order.
    /// </remarks>
    public IReadOnlyList<string> BatchArtifacts() =>
        !Directory.Exists(AsrBatchesDirectory)
            ? Array.Empty<string>()
            : Directory
                .EnumerateFiles(AsrBatchesDirectory, "*.wav", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(ToRelative)
                .ToArray();

    public string TranscriptDirectory => Path.Combine(SessionDirectory, "transcript");

    /// <summary>Mandatory normalized transcript (<c>docs/CONFIGURATION.md</c> section 10).</summary>
    public string RawTranscriptJsonl => Path.Combine(TranscriptDirectory, "raw.jsonl");

    public string LiveTranscriptMarkdown => Path.Combine(TranscriptDirectory, "live.md");

    /// <summary>Speaker-attributed transcript (M6+, arrives with speaker attribution).</summary>
    public string FinalTranscriptJsonl => Path.Combine(TranscriptDirectory, "final.jsonl");

    /// <summary>Speaker-attributed Markdown transcript (M6+).</summary>
    public string FinalTranscriptMarkdown => Path.Combine(TranscriptDirectory, "final.md");

    /// <summary>Per-session speaker attribution artifact (<c>speakers/attribution.json</c>).</summary>
    public string SpeakersDirectory => Path.Combine(SessionDirectory, "speakers");

    /// <summary>The per-session speaker attribution artifact (docs/DATA_MODEL.md section 10).</summary>
    public string SpeakerAttributionJson => Path.Combine(SpeakersDirectory, "attribution.json");

    public string LogsDirectory => Path.Combine(SessionDirectory, "logs");

    public string SessionLog => Path.Combine(LogsDirectory, "session.log");

    public string JobDirectory(string jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        return Path.Combine(AsrJobsDirectory, jobId);
    }

    /// <summary>Sanitized provider request metadata (never contains credentials).</summary>
    public string JobRequestJson(string jobId) => Path.Combine(JobDirectory(jobId), "request.json");

    /// <summary>Raw provider response, retained so parser fixes never require re-billing.</summary>
    public string JobResponseJson(string jobId) => Path.Combine(JobDirectory(jobId), "response.json");

    public string JobNormalizedJsonl(string jobId) => Path.Combine(JobDirectory(jobId), "normalized.jsonl");

    /// <summary>A path under the session's import audio directory.</summary>
    public string ImportAudioFile(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        return Path.Combine(ImportAudioDirectory, fileName);
    }

    /// <summary>
    /// Resolves a session-relative artifact path (the form stored in
    /// <c>asr_jobs.input_artifact</c>) back to an absolute path. Session-relative
    /// storage keeps the artifact contract valid when the data root moves.
    /// </summary>
    public string ResolveRelative(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(SessionDirectory, normalized));
    }

    /// <summary>Converts an absolute path inside the session directory to its relative form.</summary>
    public string ToRelative(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        var relative = Path.GetRelativePath(SessionDirectory, absolutePath);
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }
}
