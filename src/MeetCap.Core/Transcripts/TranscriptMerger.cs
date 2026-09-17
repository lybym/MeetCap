namespace MeetCap.Core.Transcripts;

/// <summary>
/// Merges independently transcribed tracks into one unified, session-relative timeline
/// (docs/ARCHITECTURE.md section 16, docs/ROADMAP.md M5).
/// </summary>
/// <remarks>
/// <para>
/// An online session transcribes the microphone and the loopback independently, so a
/// session's segments arrive from two per-track batch streams. The merger combines them
/// onto one timeline without losing the source each segment came from and without
/// deleting overlapping speech, because the same words can legitimately appear on both
/// tracks (a local speaker picked up by the microphone and again by the loopback of
/// what the machine plays).
/// </para>
/// <para>
/// The merger is deliberately minimal: it orders by session-relative
/// <see cref="TranscriptSegment.StartMs"/> and preserves everything else. Track-relative
/// times are already converted to session-relative times during normalization (the batch's
/// start offset is added to every provider timestamp), so the merger does not re-derive
/// time. Echo-duplicate detection is left to a later milestone
/// (docs/ARCHITECTURE.md section 16 step 6).
/// </para>
/// </remarks>
public static class TranscriptMerger
{
    /// <summary>
    /// Merges segments from one or more tracks into a single timeline ordered by
    /// session-relative <see cref="TranscriptSegment.StartMs"/>.
    /// </summary>
    /// <param name="segments">
    /// All segments for the session, from every track's per-job
    /// <c>normalized.jsonl</c>. May include mic and loopback segments.
    /// </param>
    /// <returns>
    /// A stable, start-ms-ordered view of the segments. Source, speaker labels and
    /// overlapping speech are preserved; nothing is deleted
    /// (docs/ARCHITECTURE.md section 16 steps 3–5).
    /// </returns>
    /// <remarks>
    /// The sort is stable, so segments that share a <see cref="TranscriptSegment.StartMs"/>
    /// keep the order in which they were supplied. A caller that feeds the microphone
    /// track's segments before the loopback track's therefore keeps the microphone first
    /// at a tie, which matches the local-user-first reading order an operator expects.
    /// </remarks>
    public static IReadOnlyList<TranscriptSegment> Merge(IEnumerable<TranscriptSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        // OrderBy is a stable sort, so equal start_ms preserves insertion order. Sorting
        // is safe for a single-track session too: a track's batches are sequential and its
        // segments are already start_ms-ordered, so the sort is a no-op there and only
        // interleavees the second track when one exists.
        return segments
            .OrderBy(s => s.StartMs)
            .ToList();
    }
}
