namespace MeetCap.Core.Transcripts;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// A normalized transcript segment (<c>docs/ARCHITECTURE.md</c> section 15 and
/// <c>docs/DATA_MODEL.md</c> section 7).
/// </summary>
/// <remarks>
/// <see cref="RawText"/> is immutable and is never overwritten by later processing.
/// <see cref="SpeakerLabel"/> is an anonymous, session/provider-scoped label such as
/// <c>speaker_1</c>; it is not a stable human identity. <see cref="SpeakerId"/>,
/// <see cref="SpeakerName"/>, and <see cref="SpeakerConfidence"/> stay null until the
/// local identity pipeline (M6) resolves them, and
/// <see cref="ManualSpeakerLock"/> records that a human owns that resolution.
/// </remarks>
public sealed record TranscriptSegment
{
    public required string SegmentId { get; init; }

    public required string SessionId { get; init; }

    /// <summary>Source track, e.g. <c>import</c>, <c>mic</c>, <c>loopback</c>.</summary>
    public required string Source { get; init; }

    public long StartMs { get; init; }

    public long EndMs { get; init; }

    public required string RawText { get; init; }

    /// <summary>Anonymous provider/session-scoped speaker label. Never a person.</summary>
    public string? SpeakerLabel { get; init; }

    /// <summary>Persistent identity, resolved only by the local identity pipeline. Null in M3.</summary>
    public string? SpeakerId { get; init; }

    /// <summary>Human-readable name, resolved only by the local identity pipeline. Null in M3.</summary>
    public string? SpeakerName { get; init; }

    public double? SpeakerConfidence { get; init; }

    public bool ManualSpeakerLock { get; init; }

    /// <summary>The ASR job that produced this segment.</summary>
    public string? ProviderJobId { get; init; }
}

/// <summary>
/// The stable JSONL representation of a segment (<c>docs/DATA_MODEL.md</c>
/// section 13). JSONL is the agent-facing interface, so the mapping lives in one
/// place rather than being re-derived per writer.
/// </summary>
public static class TranscriptJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    /// <summary>Serializes one segment as a single JSONL line (no trailing newline).</summary>
    public static string ToJsonLine(TranscriptSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return JsonSerializer.Serialize(segment, Options);
    }

    public static TranscriptSegment? FromJsonLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        return JsonSerializer.Deserialize<TranscriptSegment>(line, Options);
    }
}
