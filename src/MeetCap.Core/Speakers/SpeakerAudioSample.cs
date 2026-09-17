namespace MeetCap.Core.Speakers;

/// <summary>
/// A clean audio sample selected for embedding extraction
/// (<c>docs/ARCHITECTURE.md</c> section 17.3, <c>docs/CONFIGURATION.md</c> section 9.2).
/// Samples are mono float arrays normalized to [-1, 1] at the model's sample rate
/// (16 kHz for 3D-Speaker ERes2Net-base).
/// </summary>
public sealed record SpeakerAudioSample
{
    /// <summary>Mono PCM samples normalized to [-1, 1].</summary>
    public required float[] Samples { get; init; }

    public int SampleRate { get; init; } = 16000;

    /// <summary>The session this sample was drawn from, when known.</summary>
    public string? SourceSessionId { get; init; }

    /// <summary>The anonymous provider speaker label this sample represents (e.g. <c>speaker_0</c>).</summary>
    public string? SpeakerLabel { get; init; }

    /// <summary>Transcript segment ids the sample was assembled from.</summary>
    public string[] SourceSegmentIds { get; init; } = [];

    public long StartMs { get; init; }

    public long EndMs { get; init; }
}
