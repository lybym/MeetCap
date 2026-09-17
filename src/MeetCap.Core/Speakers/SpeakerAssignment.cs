namespace MeetCap.Core.Speakers;

/// <summary>
/// How a speaker label was resolved to an identity
/// (<c>docs/DATA_MODEL.md</c> section 10).
/// </summary>
public enum SpeakerAssignmentSource
{
    /// <summary>A human bound this label to a person. Authoritative and locked.</summary>
    Manual,

    /// <summary>The local identity provider matched a voiceprint above threshold.</summary>
    Voiceprint,

    /// <summary>The microphone track is assumed to be the local owner (online mode).</summary>
    OwnerAssumption,

    /// <summary>No confident match was found; the label stays anonymous.</summary>
    Unknown,
}

/// <summary>Wire-format helpers for <see cref="SpeakerAssignmentSource"/>.</summary>
public static class SpeakerAssignmentSources
{
    public static string ToWire(SpeakerAssignmentSource source) => source switch
    {
        SpeakerAssignmentSource.Manual => "manual",
        SpeakerAssignmentSource.Voiceprint => "voiceprint",
        SpeakerAssignmentSource.OwnerAssumption => "owner_assumption",
        SpeakerAssignmentSource.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown speaker assignment source."),
    };

    public static SpeakerAssignmentSource Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value switch
        {
            "manual" => SpeakerAssignmentSource.Manual,
            "voiceprint" => SpeakerAssignmentSource.Voiceprint,
            "owner_assumption" => SpeakerAssignmentSource.OwnerAssumption,
            "unknown" => SpeakerAssignmentSource.Unknown,
            _ => throw new InvalidOperationException($"Stored speaker assignment source '{value}' is unrecognised."),
        };
    }
}

/// <summary>
/// A per-session binding of an anonymous speaker label to a person
/// (<c>docs/DATA_MODEL.md</c> section 10). Manual assignments are locked and are
/// never overwritten by automatic inference (<c>docs/ARCHITECTURE.md</c> section 17.3).
/// </summary>
public sealed record SpeakerAssignment
{
    public required string Id { get; init; }

    public required string SessionId { get; init; }

    /// <summary>The anonymous provider label being resolved (e.g. <c>speaker_0</c>).</summary>
    public required string SpeakerLabel { get; init; }

    /// <summary>The resolved person id, or null when the label is unknown.</summary>
    public string? SpeakerId { get; init; }

    public string? SpeakerName { get; init; }

    public double? Confidence { get; init; }

    public SpeakerAssignmentSource Source { get; init; } = SpeakerAssignmentSource.Unknown;

    /// <summary>True when a human owns this resolution and automatic rematching must not overwrite it.</summary>
    public bool Locked { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}
