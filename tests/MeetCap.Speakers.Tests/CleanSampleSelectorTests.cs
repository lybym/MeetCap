using MeetCap.Core.Transcripts;
using MeetCap.Speakers;
using Xunit;

namespace MeetCap.Speakers.Tests;

public class CleanSampleSelectorTests
{
    private static TranscriptSegment Seg(string label, long start, long end, string id) => new()
    {
        SegmentId = id, SessionId = "ses_1", Source = "mic",
        StartMs = start, EndMs = end, RawText = "test", SpeakerLabel = label,
    };

    [Fact]
    public void SelectsRangesInTargetDuration()
    {
        var segments = new List<TranscriptSegment>
        {
            Seg("speaker_0", 0, 7000, "s1"),
            Seg("speaker_0", 8000, 16000, "s2"),
        };

        var ranges = CleanSampleSelector.Select(segments, minSeconds: 5, maxSeconds: 15);

        Assert.NotEmpty(ranges);
        Assert.All(ranges, r => Assert.True(r.DurationMs >= 5000));
    }

    [Fact]
    public void CombinesAdjacentSegmentsToReachMinimum()
    {
        var segments = new List<TranscriptSegment>
        {
            Seg("speaker_0", 0, 2000, "s1"),
            Seg("speaker_0", 2100, 4000, "s2"),
            Seg("speaker_0", 4100, 6500, "s3"),
        };

        var ranges = CleanSampleSelector.Select(segments, minSeconds: 5, maxSeconds: 15);

        Assert.NotEmpty(ranges);
        Assert.True(ranges[0].DurationMs >= 5000);
    }

    [Fact]
    public void SeparatesByGapThreshold()
    {
        var segments = new List<TranscriptSegment>
        {
            Seg("speaker_0", 0, 3000, "s1"),
            // 5-second gap (> 2000ms threshold) creates a new block
            Seg("speaker_0", 8000, 14000, "s2"),
        };

        var ranges = CleanSampleSelector.Select(segments, minSeconds: 5, maxSeconds: 15);

        Assert.NotEmpty(ranges);
        // The second block is 6 seconds (8000-14000), which is in range.
        Assert.Contains(ranges, r => r.StartMs == 8000 && r.EndMs == 14000);
    }

    [Fact]
    public void CapsAtMaxDuration()
    {
        var segments = new List<TranscriptSegment>
        {
            Seg("speaker_0", 0, 30000, "s1"),
        };

        var ranges = CleanSampleSelector.Select(segments, minSeconds: 5, maxSeconds: 15, maxSamples: 1);

        Assert.Single(ranges);
        Assert.True(ranges[0].DurationMs <= 15000);
    }

    [Fact]
    public void EmptySegments_ReturnsEmpty()
    {
        var ranges = CleanSampleSelector.Select(Array.Empty<TranscriptSegment>(), 5, 15);
        Assert.Empty(ranges);
    }

    [Fact]
    public void ReturnsAtMostMaxSamples()
    {
        var segments = new List<TranscriptSegment>();
        for (var i = 0; i < 10; i++)
        {
            segments.Add(Seg("speaker_0", i * 7000, i * 7000 + 6000, $"s{i}"));
        }

        var ranges = CleanSampleSelector.Select(segments, 5, 15, maxSamples: 3);
        Assert.True(ranges.Count <= 3);
    }
}
