namespace MeetCap.Core.Speakers;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// One entry in the per-session speaker attribution artifact
/// (<c>docs/DATA_MODEL.md</c> section 10).
/// </summary>
/// <remarks>
/// The top-level fields are the original contract and are never modified by diagnostics
/// work. The <see cref="Diagnostics"/> block is additive (issue #50): it is nullable so
/// an older <c>attribution.json</c> written without it still deserializes, and existing
/// consumers that read only the top-level fields are unaffected.
/// </remarks>
public sealed record SpeakerAttributionEntry
{
    public required string SpeakerLabel { get; init; }

    public string? SpeakerId { get; init; }

    public string? SpeakerName { get; init; }

    public double? Confidence { get; init; }

    public required SpeakerAssignmentSource Source { get; init; }

    public bool Locked { get; init; }

    /// <summary>
    /// Per-label diagnostics (issue #50): sampled ranges, sample outcome, top-N candidates
    /// with similarities, configured threshold/margin, and registry size — for every label,
    /// resolved or not. Null only on artifacts written before issue #50.
    /// </summary>
    public SpeakerLabelDiagnostics? Diagnostics { get; init; }
}

/// <summary>
/// The per-session <c>speakers/attribution.json</c> artifact. Maps each anonymous
/// provider speaker label to its resolved identity, source, confidence, and lock
/// state. Manual assignments always win (<c>docs/ARCHITECTURE.md</c> section 17.3).
/// </summary>
public sealed record SpeakerAttributionArtifact
{
    public required string SessionId { get; init; }

    public IReadOnlyList<SpeakerAttributionEntry> Entries { get; init; } = [];
}

/// <summary>Stable JSON serialization for <c>attribution.json</c> (<c>docs/DATA_MODEL.md</c> section 13).</summary>
public static class SpeakerAttributionJson
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string ToJson(SpeakerAttributionArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return JsonSerializer.Serialize(artifact, s_options);
    }

    public static SpeakerAttributionArtifact? FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<SpeakerAttributionArtifact>(json, s_options);
    }
}
