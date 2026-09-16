using System.Text.Json;
using MeetCap.Core.Transcripts;
using Xunit;

namespace MeetCap.Core.Tests.Transcripts;

/// <summary>
/// JSONL is the stable agent-facing interface (docs/DATA_MODEL.md section 13) and the
/// transcript model is where the anonymous-diarization vs persistent-identity boundary
/// is enforced (docs/ARCHITECTURE.md section 15).
/// </summary>
public class TranscriptJsonTests
{
    private static TranscriptSegment Segment() => new()
    {
        SegmentId = "seg_1",
        SessionId = "ses_1",
        Source = "import",
        StartMs = 12340,
        EndMs = 16420,
        RawText = "The quotation needs another review.",
        SpeakerLabel = "speaker_1",
        SpeakerId = null,
        SpeakerName = null,
        SpeakerConfidence = null,
        ManualSpeakerLock = false,
        ProviderJobId = "job_1",
    };

    [Fact]
    public void SerializesTheDocumentedSnakeCaseShape()
    {
        var line = TranscriptJson.ToJsonLine(Segment());
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;

        Assert.Equal("seg_1", root.GetProperty("segment_id").GetString());
        Assert.Equal("ses_1", root.GetProperty("session_id").GetString());
        Assert.Equal("import", root.GetProperty("source").GetString());
        Assert.Equal(12340, root.GetProperty("start_ms").GetInt64());
        Assert.Equal(16420, root.GetProperty("end_ms").GetInt64());
        Assert.Equal("speaker_1", root.GetProperty("speaker_label").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("speaker_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("speaker_name").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("speaker_confidence").ValueKind);
        Assert.False(root.GetProperty("manual_speaker_lock").GetBoolean());
        Assert.Equal("job_1", root.GetProperty("provider_job_id").GetString());
    }

    [Fact]
    public void AnonymousSpeakerLabels_AreNotPersistentIdentities()
    {
        // Nothing in the M3 model may assign identity: speaker_id/speaker_name stay null
        // until the local identity pipeline (M6) resolves them.
        var segment = TranscriptJson.FromJsonLine(TranscriptJson.ToJsonLine(Segment()));

        Assert.NotNull(segment);
        Assert.Equal("speaker_1", segment!.SpeakerLabel);
        Assert.Null(segment.SpeakerId);
        Assert.Null(segment.SpeakerName);
        Assert.False(segment.ManualSpeakerLock);
    }

    [Fact]
    public void RoundTripsWithoutLosingFields()
    {
        var original = Segment();
        var parsed = TranscriptJson.FromJsonLine(TranscriptJson.ToJsonLine(original));

        Assert.Equal(original, parsed);
    }

    [Fact]
    public void BlankLinesAreIgnored()
    {
        Assert.Null(TranscriptJson.FromJsonLine(string.Empty));
        Assert.Null(TranscriptJson.FromJsonLine("   "));
    }
}
