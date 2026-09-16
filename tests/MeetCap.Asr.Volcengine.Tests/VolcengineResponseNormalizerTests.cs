using MeetCap.Core.Asr;
using Xunit;

namespace MeetCap.Asr.Volcengine.Tests;

/// <summary>
/// The normalizer is where provider JSON becomes domain data. Two properties matter
/// most: timestamps and anonymous speaker labels survive intact, and no provider
/// speaker label is ever promoted to a persistent identity.
/// </summary>
public class VolcengineResponseNormalizerTests
{
    private static readonly AsrNormalizationContext Context = new()
    {
        SessionId = "ses_1",
        JobId = "job_1",
        Source = "import",
    };

    private static readonly VolcengineResponseNormalizer Normalizer = new();

    [Fact]
    public void Utterances_BecomeTimestampedSegmentsWithAnonymousSpeakerLabels()
    {
        const string raw = """
        {"result":{"text":"hello world","utterances":[
          {"text":"hello","start_time":0,"end_time":1200,"speaker":"1"},
          {"text":"world","start_time":1200,"end_time":2400,"additions":{"speaker":"2"}}
        ]}}
        """;

        var result = Normalizer.Normalize(raw, Context);

        Assert.Equal(2, result.Segments.Count);
        Assert.True(result.SpeakerInfoReturned);
        Assert.Null(result.ErrorCode);

        var first = result.Segments[0];
        Assert.Equal(0, first.StartMs);
        Assert.Equal(1200, first.EndMs);
        Assert.Equal("hello", first.RawText);
        Assert.Equal("speaker_1", first.SpeakerLabel);
        Assert.Equal("ses_1", first.SessionId);
        Assert.Equal("import", first.Source);
        Assert.Equal("job_1", first.ProviderJobId);

        Assert.Equal("speaker_2", result.Segments[1].SpeakerLabel);
    }

    [Fact]
    public void ProviderSpeakerLabels_AreNeverPersistentIdentities()
    {
        const string raw = """
        {"result":{"utterances":[{"text":"hi","start_time":0,"end_time":100,"speaker":"7"}]}}
        """;

        var segment = Assert.Single(Normalizer.Normalize(raw, Context).Segments);

        Assert.Equal("speaker_7", segment.SpeakerLabel);
        Assert.Null(segment.SpeakerId);
        Assert.Null(segment.SpeakerName);
        Assert.Null(segment.SpeakerConfidence);
        Assert.False(segment.ManualSpeakerLock);
    }

    [Fact]
    public void ExistingSpeakerPrefixedLabels_ArePreserved()
    {
        const string raw = """
        {"result":{"utterances":[{"text":"hi","start_time":0,"end_time":100,"speaker":"speaker_3"}]}}
        """;

        Assert.Equal("speaker_3", Assert.Single(Normalizer.Normalize(raw, Context).Segments).SpeakerLabel);
    }

    [Fact]
    public void MissingSpeakerInformation_IsReportedAsNotReturned()
    {
        const string raw = """
        {"result":{"utterances":[{"text":"hi","start_time":0,"end_time":100}]}}
        """;

        var result = Normalizer.Normalize(raw, Context);

        Assert.False(result.SpeakerInfoReturned);
        Assert.Null(Assert.Single(result.Segments).SpeakerLabel);
    }

    [Fact]
    public void EmptyAndSilentResponses_ProduceAnEmptyTranscriptRatherThanAFailure()
    {
        Assert.Empty(Normalizer.Normalize(string.Empty, Context).Segments);
        Assert.Empty(Normalizer.Normalize("{}", Context).Segments);
        Assert.Empty(Normalizer.Normalize("""{"result":{"text":"","utterances":[]}}""", Context).Segments);
    }

    [Fact]
    public void TextWithoutUtterances_BecomesASingleWholeRecordingSegment()
    {
        const string raw = """
        {"result":{"text":"whole transcript","audio_info":{"duration":5000}}}
        """;

        var segment = Assert.Single(Normalizer.Normalize(raw, Context).Segments);

        Assert.Equal("whole transcript", segment.RawText);
        Assert.Equal(0, segment.StartMs);
        Assert.Equal(5000, segment.EndMs);
    }

    [Fact]
    public void EmptyUtterances_AreDroppedButSegmentIdsStayStable()
    {
        const string raw = """
        {"result":{"utterances":[
          {"text":"","start_time":0,"end_time":100},
          {"text":"kept","start_time":100,"end_time":200}
        ]}}
        """;

        var segment = Assert.Single(Normalizer.Normalize(raw, Context).Segments);

        Assert.Equal("kept", segment.RawText);
        // Ids are derived from the utterance index, so they do not shift when an empty
        // utterance is dropped.
        Assert.EndsWith("_000001", segment.SegmentId, StringComparison.Ordinal);
    }

    [Fact]
    public void InvertedTimestamps_AreClampedInsteadOfProducingNegativeDurations()
    {
        const string raw = """
        {"result":{"utterances":[{"text":"hi","start_time":900,"end_time":100}]}}
        """;

        var segment = Assert.Single(Normalizer.Normalize(raw, Context).Segments);

        Assert.Equal(900, segment.StartMs);
        Assert.Equal(900, segment.EndMs);
    }

    [Fact]
    public void ErrorEnvelope_IsSurfacedAsAnErrorCodeRatherThanThrowing()
    {
        const string raw = """
        {"error":{"code":"45000002","message":"empty audio"}}
        """;

        var result = Normalizer.Normalize(raw, Context);

        Assert.Empty(result.Segments);
        Assert.Equal("45000002", result.ErrorCode);
        Assert.Equal("empty audio", result.ErrorMessage);
    }

    [Fact]
    public void MalformedJson_ThrowsSoTheRetainedRawResponseIsUsedForAParserFix()
    {
        var ex = Assert.Throws<AsrNormalizationException>(
            () => Normalizer.Normalize("{not json", Context));

        Assert.Contains("not valid JSON", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonObjectJson_Throws()
    {
        Assert.Throws<AsrNormalizationException>(() => Normalizer.Normalize("[1,2,3]", Context));
    }
}
