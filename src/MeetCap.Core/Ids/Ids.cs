namespace MeetCap.Core.Ids;

/// <summary>
/// Identifier allocation for sessions, ASR jobs, and segments. Prefixes make an id
/// self-describing in logs and artifacts (<c>docs/DATA_MODEL.md</c> sections 3 and 7).
/// </summary>
public static class Ids
{
    public const string SessionPrefix = "ses_";
    public const string JobPrefix = "job_";
    public const string SegmentPrefix = "seg_";

    public static string NewSessionId() => SessionPrefix + NewToken();

    public static string NewJobId() => JobPrefix + NewToken();

    public static string NewSegmentId() => SegmentPrefix + NewToken();

    public static string NewSegmentId(string jobId, int index)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        return $"{SegmentPrefix}{Sanitize(jobId)}_{index:D6}";
    }

    /// <summary>
    /// Provider-facing request id. Volcengine expects a UUID as the task identifier,
    /// and the job persists it before the first submit so a restart never allocates a
    /// second task for the same audio.
    /// </summary>
    public static string NewProviderRequestId() => Guid.NewGuid().ToString("D");

    private static string NewToken() => Guid.NewGuid().ToString("N");

    /// <summary>Keeps a generated segment id filesystem/JSONL safe.</summary>
    private static string Sanitize(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray();
        return new string(chars);
    }
}
