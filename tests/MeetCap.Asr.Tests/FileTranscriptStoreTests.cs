using MeetCap.Asr.Transcripts;
using MeetCap.Core.Transcripts;
using Xunit;

namespace MeetCap.Asr.Tests;

public class FileTranscriptStoreTests : IDisposable
{
    private readonly string _root;
    private readonly FileTranscriptStore _store = new();

    public FileTranscriptStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "meetcap-transcript-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private static TranscriptSegment Segment(int index, string? speaker = "speaker_1", string text = "hello") => new()
    {
        SegmentId = $"seg_{index}",
        SessionId = "ses_1",
        Source = "import",
        StartMs = index * 1000,
        EndMs = (index * 1000) + 900,
        RawText = text,
        SpeakerLabel = speaker,
        ProviderJobId = "job_1",
    };

    [Fact]
    public void AppendJsonl_AppendsRatherThanReplaces()
    {
        var path = Path.Combine(_root, "transcript", "raw.jsonl");

        Assert.Equal(1, _store.AppendJsonl(path, new[] { Segment(0) }));
        Assert.Equal(1, _store.AppendJsonl(path, new[] { Segment(1) }));

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Contains("seg_0", lines[0], StringComparison.Ordinal);
        Assert.Contains("seg_1", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void ReadJsonl_RoundTripsAndToleratesAMissingFile()
    {
        var path = Path.Combine(_root, "transcript", "raw.jsonl");
        Assert.Empty(_store.ReadJsonl(path));

        _store.WriteJsonl(path, new[] { Segment(0), Segment(1) });
        var read = _store.ReadJsonl(path);

        Assert.Equal(2, read.Count);
        Assert.Equal("seg_0", read[0].SegmentId);
        Assert.Equal("speaker_1", read[0].SpeakerLabel);
    }

    [Fact]
    public void WriteJsonl_ReplacesExistingContent()
    {
        var path = Path.Combine(_root, "transcript", "normalized.jsonl");
        _store.WriteJsonl(path, new[] { Segment(0), Segment(1) });
        _store.WriteJsonl(path, new[] { Segment(2) });

        Assert.Single(_store.ReadJsonl(path));
    }

    [Fact]
    public void WriteMarkdown_RendersTimestampsSourceAndAnonymousSpeakerLabels()
    {
        var path = Path.Combine(_root, "transcript", "live.md");

        _store.WriteMarkdown(
            path,
            "ses_1",
            new[] { Segment(0, text: "first line"), Segment(61, speaker: null, text: "second line") },
            TranscriptRenderOptions.Default);

        var markdown = File.ReadAllText(path);

        Assert.Contains("# Meeting Transcript", markdown, StringComparison.Ordinal);
        Assert.Contains("`ses_1`", markdown, StringComparison.Ordinal);
        Assert.Contains("00:00:00.000 - 00:00:00.900", markdown, StringComparison.Ordinal);
        Assert.Contains("00:01:01.000", markdown, StringComparison.Ordinal);
        Assert.Contains("`import`", markdown, StringComparison.Ordinal);
        Assert.Contains("`speaker_1`", markdown, StringComparison.Ordinal);
        Assert.Contains("first line", markdown, StringComparison.Ordinal);
        Assert.Contains("not persistent identities", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteMarkdown_HonoursTheRenderOptions()
    {
        var path = Path.Combine(_root, "transcript", "live.md");

        _store.WriteMarkdown(
            path,
            "ses_1",
            new[] { Segment(0) },
            new TranscriptRenderOptions(IncludeSource: false, IncludeTimestamps: false, IncludeSpeakerLabels: false));

        var markdown = File.ReadAllText(path);

        Assert.DoesNotContain("00:00:00.000", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("`import`", markdown, StringComparison.Ordinal);
        // The anonymous-label notice mentions speaker_1 by name; the rendered segment
        // must not, which is what "labels suppressed" means.
        Assert.Equal(1, CountOccurrences(markdown, "`speaker_1`"));
        Assert.Contains("hello", markdown, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    [Fact]
    public void WriteMarkdown_WithNoSegments_StillProducesAReadableDocument()
    {
        var path = Path.Combine(_root, "transcript", "live.md");

        _store.WriteMarkdown(path, "ses_1", Array.Empty<TranscriptSegment>(), TranscriptRenderOptions.Default);

        var markdown = File.ReadAllText(path);
        Assert.Contains("Segments: 0", markdown, StringComparison.Ordinal);
    }
}
