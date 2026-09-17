using MeetCap.Core.Transcripts;
using Xunit;

namespace MeetCap.Core.Tests.Transcripts;

/// <summary>
/// The unified timeline merger combines independently transcribed mic and loopback
/// tracks onto one session-relative timeline (docs/ARCHITECTURE.md section 16,
/// docs/ROADMAP.md M5).
/// </summary>
public class TranscriptMergerTests
{
    private static TranscriptSegment Seg(string source, long startMs, long endMs, string text, string? label = null)
        => new()
        {
            SegmentId = $"seg_{source}_{startMs}",
            SessionId = "ses_1",
            Source = source,
            StartMs = startMs,
            EndMs = endMs,
            RawText = text,
            SpeakerLabel = label,
        };

    [Fact]
    public void Merge_OrdersBySessionRelativeStartMsAcrossBothTracks()
    {
        var mic = new[]
        {
            Seg("mic", 0, 1000, "hello"),
            Seg("mic", 4000, 5000, "are you there"),
        };
        var loopback = new[]
        {
            Seg("loopback", 1000, 2000, "hi, yes"),
            Seg("loopback", 3000, 3500, "go ahead"),
        };

        var merged = TranscriptMerger.Merge(mic.Concat(loopback));

        Assert.Equal(new[] { 0L, 1000, 3000, 4000 }, merged.Select(s => s.StartMs));
        Assert.Equal(new[] { "mic", "loopback", "loopback", "mic" }, merged.Select(s => s.Source));
    }

    [Fact]
    public void Merge_PreservesTheSourceOfEverySegment()
    {
        var segments = new[]
        {
            Seg("loopback", 0, 1000, "remote"),
            Seg("mic", 0, 1000, "local"),
        };

        var merged = TranscriptMerger.Merge(segments);

        Assert.All(merged, s => Assert.True(s.Source is "mic" or "loopback"));
        Assert.Contains(merged, s => s.Source == "mic");
        Assert.Contains(merged, s => s.Source == "loopback");
    }

    [Fact]
    public void Merge_DoesNotDeleteOverlappingSpeech()
    {
        // The same words can legitimately appear on both tracks: a local speaker picked
        // up by the microphone and again by the loopback of what the machine plays.
        // Overlapping speech is preserved rather than silently deleted
        // (docs/ARCHITECTURE.md section 16 step 5).
        var segments = new[]
        {
            Seg("mic", 1000, 2000, "I think we should ship"),
            Seg("loopback", 1000, 2000, "I think we should ship"),
        };

        var merged = TranscriptMerger.Merge(segments);

        Assert.Equal(2, merged.Count);
        Assert.Equal("I think we should ship", merged[0].RawText);
        Assert.Equal("I think we should ship", merged[1].RawText);
        Assert.NotEqual(merged[0].Source, merged[1].Source);
    }

    [Fact]
    public void Merge_IsStableAtTiedStartMsSoInsertionOrderIsPreserved()
    {
        // Feeding the microphone track first keeps the microphone first at a tie, which
        // matches the local-user-first reading order an operator expects.
        var micFirst = Seg("mic", 1000, 2000, "local");
        var loopbackFirst = Seg("loopback", 1000, 2000, "remote");

        var micThenLoopback = TranscriptMerger.Merge(new[] { micFirst, loopbackFirst });
        Assert.Equal(new[] { "mic", "loopback" }, micThenLoopback.Select(s => s.Source));

        var loopbackThenMic = TranscriptMerger.Merge(new[] { loopbackFirst, micFirst });
        Assert.Equal(new[] { "loopback", "mic" }, loopbackThenMic.Select(s => s.Source));
    }

    [Fact]
    public void Merge_PreservesAnonymousSpeakerLabels()
    {
        var segments = new[]
        {
            Seg("loopback", 0, 1000, "remote one", "speaker_1"),
            Seg("loopback", 1000, 2000, "remote two", "speaker_2"),
            Seg("mic", 500, 800, "local", "speaker_0"),
        };

        var merged = TranscriptMerger.Merge(segments);

        // Ordered by start_ms: 0 (loopback), 500 (mic), 1000 (loopback).
        Assert.Equal(new[] { "speaker_1", "speaker_0", "speaker_2" }, merged.Select(s => s.SpeakerLabel));
        Assert.All(merged, s => Assert.Null(s.SpeakerId));
        Assert.Equal(new[] { "loopback", "mic", "loopback" }, merged.Select(s => s.Source));
    }

    [Fact]
    public void Merge_IsANoopForASingleAlreadyOrderedTrack()
    {
        // A single-track session's batches are sequential and its segments are already
        // start_ms-ordered, so the merge is a stable no-op there and only interleavees
        // the second track when one exists.
        var segments = new[]
        {
            Seg("mic", 0, 1000, "one"),
            Seg("mic", 1000, 2000, "two"),
            Seg("mic", 2000, 3000, "three"),
        };

        var merged = TranscriptMerger.Merge(segments);

        Assert.Same(segments[0], Assert.Single(merged, s => s.StartMs == 0));
        Assert.Same(segments[1], Assert.Single(merged, s => s.StartMs == 1000));
        Assert.Same(segments[2], Assert.Single(merged, s => s.StartMs == 2000));
    }

    [Fact]
    public void Merge_EmptyInputProducesAnEmptyTimeline()
    {
        Assert.Empty(TranscriptMerger.Merge(Array.Empty<TranscriptSegment>()));
    }
}
