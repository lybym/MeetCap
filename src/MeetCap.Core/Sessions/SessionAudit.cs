namespace MeetCap.Core.Sessions;

using MeetCap.Core.Capture;

/// <summary>Why a stretch of the session timeline holds no durable audio.</summary>
public static class AudioGapReasons
{
    /// <summary>
    /// The audio was never captured: the device skipped it, the session was interrupted,
    /// or the bounded queue had to drop it.
    /// </summary>
    public const string NotCaptured = "not_captured";

    /// <summary>A chunk exists for this position but is not durable (unrepairable bytes).</summary>
    public const string ChunkUnreadable = "chunk_unreadable";

    /// <summary>The index knows a chunk at this position, but no file is on disk.</summary>
    public const string ChunkMissing = "chunk_missing";

    /// <summary>Every reason value an audit may report.</summary>
    public static readonly IReadOnlyList<string> All = new[] { NotCaptured, ChunkUnreadable, ChunkMissing };
}

/// <summary>
/// One stretch of missing audio on the session timeline, derived from the durable chunk
/// index rather than from an in-memory counter.
/// </summary>
/// <remarks>
/// docs/RELIABILITY.md section 7 requires a discontinuity to be an explicit gap event,
/// never something hidden by shifting later timestamps. A gap computed here is therefore
/// a fact about the artifacts on disk, and a recovery that still finds one must never
/// report the session as repaired.
/// </remarks>
public sealed record AudioGap(
    int Sequence,
    string Source,
    long StartMs,
    long EndMs,
    string Reason,
    string Detail)
{
    /// <summary>Length of the missing stretch in milliseconds.</summary>
    public long GapMs => EndMs - StartMs;

    /// <summary>
    /// Index entries the track's sequence numbering skips across this stretch. A chunk
    /// number that was never used is evidence of an artifact that never reached disk,
    /// which is exactly the kind of loss docs/RELIABILITY.md section 6 forbids hiding.
    /// </summary>
    public IReadOnlyList<int> MissingSequences { get; init; } = Array.Empty<int>();
}

/// <summary>Result of auditing one session's chunk index for missing audio.</summary>
public sealed record SessionAudit(
    string SessionId,
    IReadOnlyList<AudioGap> Gaps,
    IReadOnlyList<string> Problems)
{
    /// <summary>An audit that found nothing wrong.</summary>
    public static SessionAudit Clean(string sessionId)
        => new(sessionId, Array.Empty<AudioGap>(), Array.Empty<string>());

    /// <summary>True when the session's timeline has a known hole or its index is unreadable.</summary>
    public bool HasGap => Gaps.Count > 0;

    /// <summary>True when the audit itself could not be completed honestly.</summary>
    public bool HasProblems => Problems.Count > 0;

    /// <summary>
    /// True when recovery must not claim success: a known gap remains, or the audit could
    /// not finish (docs/RELIABILITY.md section 6).
    /// </summary>
    public bool RecoveryIncomplete => HasGap || HasProblems;

    /// <summary>Total missing audio on the session timeline.</summary>
    public long TotalGapMs => Gaps.Sum(g => g.GapMs);

    /// <summary>Gaps caused by an artifact that could not be repaired.</summary>
    public int UnreadableGapCount
        => Gaps.Count(g => !string.Equals(g.Reason, AudioGapReasons.NotCaptured, StringComparison.Ordinal));

    /// <summary>A one-line summary for the CLI.</summary>
    public string Describe()
    {
        if (HasGap)
        {
            return $"{Gaps.Count} gap(s), {TotalGapMs} ms of audio missing" +
                   (UnreadableGapCount > 0 ? $" ({UnreadableGapCount} from unreadable artifacts)" : string.Empty);
        }

        return HasProblems ? "the gap audit could not be completed" : "no gaps";
    }

    /// <summary>
    /// One line per gap, so a report states where the audio is missing rather than only
    /// that something is missing.
    /// </summary>
    public IEnumerable<string> DescribeGaps()
        => Gaps.Select(g =>
            $"{g.Source} {g.StartMs}..{g.EndMs} ms ({g.GapMs} ms, chunk {g.Sequence:D6}, {g.Reason}): {g.Detail}");
}
