namespace MeetCap.Core.Asr;

/// <summary>
/// A snapshot of the persistent ASR queue, for <c>meetcap status</c>
/// (<c>docs/ROADMAP.md</c> M4: "expose ASR queue/degraded state").
/// </summary>
/// <remarks>
/// The queue is described, not acted on: a backlog of <c>pending</c> or <c>retry_wait</c>
/// jobs is the documented behaviour of a lost network connection, not a session failure
/// (<c>docs/RELIABILITY.md</c> section 9). <see cref="IsDegraded"/> exists so the CLI can
/// say "transcription is behind" instead of implying that recording was affected.
/// </remarks>
public sealed record AsrQueueStatus(
    int Pending,
    int InFlight,
    int Retrying,
    int Succeeded,
    int Failed,
    int Cancelled,
    int Active)
{
    public static readonly AsrQueueStatus Empty = new(0, 0, 0, 0, 0, 0, 0);

    /// <summary>Jobs that still need work: pending, submitting, submitted, polling, retry_wait.</summary>
    public int Outstanding => Pending + InFlight + Retrying;

    /// <summary>True when the transcript is behind or permanently incomplete.</summary>
    public bool IsDegraded => Failed > 0 || Retrying > 0 || Pending > 0;

    /// <summary>One-line report for the CLI.</summary>
    public string Describe()
    {
        var text =
            $"{Outstanding} outstanding ({Pending} pending, {InFlight} in flight, {Retrying} awaiting retry), " +
            $"{Succeeded} succeeded, {Failed} failed";

        if (Active > 0)
        {
            text += $" across {Active} session(s)";
        }

        return text;
    }
}

/// <summary>
/// The persistent ASR queue's read-only state for user-facing reporting. Implemented by
/// <c>MeetCap.Persistence</c> over the same <c>asr_jobs</c> table the queue itself uses.
/// </summary>
public interface IAsrQueueInspector
{
    /// <summary>Counts every job by status group.</summary>
    AsrQueueStatus Inspect();

    /// <summary>Counts one session's jobs by status group.</summary>
    AsrQueueStatus InspectSession(string sessionId);
}
