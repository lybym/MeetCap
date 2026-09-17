namespace MeetCap.Speakers;

using MeetCap.Core.Transcripts;

/// <summary>
/// A contiguous speech time range selected for embedding extraction.
/// </summary>
public sealed record SpeakerSampleRange(long StartMs, long EndMs, string[] SegmentIds)
{
    public long DurationMs => EndMs - StartMs;
}

/// <summary>
/// Selects clean 5-15 second speech samples from provider-labeled speaker segments
/// (<c>docs/ARCHITECTURE.md</c> section 17.3, <c>docs/CONFIGURATION.md</c> section 9.2).
/// </summary>
/// <remarks>
/// The selector is pure logic over transcript segments: it groups adjacent segments
/// into contiguous speech ranges, prefers ranges whose duration falls in the
/// configured [min, max] window, and returns at most a few ranges so enrollment and
/// identification do not over-sample. Audio extraction for the returned ranges is
/// performed by the caller (the CLI composition root), which owns the audio
/// infrastructure; this class has no audio dependency.
/// </remarks>
public static class CleanSampleSelector
{
    /// <summary>
    /// Segments further apart than this are treated as separate speech blocks, so a
    /// long silence is not folded into a sample.
    /// </summary>
    private const long GapThresholdMs = 2000;

    /// <summary>
    /// Selects up to <paramref name="maxSamples"/> clean speech ranges from the given
    /// segments. Ranges whose total speech duration is at least <paramref name="minSeconds"/>
    /// are preferred; a range longer than <paramref name="maxSeconds"/> is capped.
    /// </summary>
    public static IReadOnlyList<SpeakerSampleRange> Select(
        IReadOnlyList<TranscriptSegment> segments,
        int minSeconds,
        int maxSeconds,
        int maxSamples = 3)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (minSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(minSeconds));
        if (maxSeconds < minSeconds) throw new ArgumentOutOfRangeException(nameof(maxSeconds));
        if (maxSamples <= 0) maxSamples = 3;

        if (segments.Count == 0) return [];

        var minMs = minSeconds * 1000L;
        var maxMs = maxSeconds * 1000L;

        var sorted = segments
            .Where(s => !string.IsNullOrWhiteSpace(s.SpeakerLabel) && s.EndMs > s.StartMs)
            .OrderBy(s => s.StartMs)
            .ToList();

        if (sorted.Count == 0) return [];

        var blocks = BuildContiguousBlocks(sorted);
        var ranges = new List<SpeakerSampleRange>();

        // Prefer blocks already in the clean window, then fall back to longer blocks
        // (capped) and finally to shorter-than-minimum blocks (still useful for identification).
        foreach (var block in blocks)
        {
            if (ranges.Count >= maxSamples) break;

            if (block.DurationMs >= minMs)
            {
                ranges.Add(Cap(block, maxMs));
            }
        }

        // If no block met the minimum, use the longest available blocks anyway — a short
        // sample is still better than no sample for identification.
        if (ranges.Count == 0)
        {
            foreach (var block in blocks.OrderByDescending(b => b.DurationMs))
            {
                if (ranges.Count >= maxSamples) break;
                ranges.Add(Cap(block, maxMs));
            }
        }

        return ranges;
    }

    private static List<SpeakerSampleRange> BuildContiguousBlocks(List<TranscriptSegment> sorted)
    {
        var blocks = new List<SpeakerSampleRange>();
        var start = sorted[0].StartMs;
        var end = sorted[0].EndMs;
        var segIds = new List<string> { sorted[0].SegmentId };

        for (var i = 1; i < sorted.Count; i++)
        {
            var seg = sorted[i];
            if (seg.StartMs - end <= GapThresholdMs)
            {
                // Extend the current block.
                end = Math.Max(end, seg.EndMs);
                segIds.Add(seg.SegmentId);
            }
            else
            {
                blocks.Add(new SpeakerSampleRange(start, end, segIds.ToArray()));
                start = seg.StartMs;
                end = seg.EndMs;
                segIds = [seg.SegmentId];
            }
        }

        blocks.Add(new SpeakerSampleRange(start, end, segIds.ToArray()));
        return blocks;
    }

    private static SpeakerSampleRange Cap(SpeakerSampleRange range, long maxMs)
    {
        if (range.DurationMs <= maxMs) return range;
        return range with { EndMs = range.StartMs + maxMs };
    }
}
