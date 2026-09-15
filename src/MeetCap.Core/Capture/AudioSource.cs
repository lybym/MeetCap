namespace MeetCap.Core.Capture;

/// <summary>
/// Identifies one captured audio track. Tracks are never mixed before ASR
/// (docs/ARCHITECTURE.md section 6), so every packet, chunk and event carries the
/// source it came from.
/// </summary>
public enum AudioSource
{
    /// <summary>The local microphone track. The only track M1 captures.</summary>
    Mic,

    /// <summary>
    /// The system loopback ("what the machine plays") track. Declared from M0 so the
    /// artifact contract cannot drift, but captured from M5.
    /// </summary>
    Loopback,
}

/// <summary>
/// Wire/artifact names for <see cref="AudioSource"/>. These are the values stored in
/// <c>audio_chunks.source</c> and in <c>events.jsonl</c> (docs/DATA_MODEL.md).
/// </summary>
public static class AudioSources
{
    public const string Mic = "mic";
    public const string Loopback = "loopback";

    /// <summary>The set of source names that may appear in a stored artifact.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Mic, Loopback };

    public static string ToWireName(this AudioSource source) => source switch
    {
        AudioSource.Mic => Mic,
        AudioSource.Loopback => Loopback,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown audio source."),
    };

    public static bool TryParse(string? value, out AudioSource source)
    {
        switch (value)
        {
            case Mic:
                source = AudioSource.Mic;
                return true;
            case Loopback:
                source = AudioSource.Loopback;
                return true;
            default:
                source = default;
                return false;
        }
    }

    public static AudioSource Parse(string value)
        => TryParse(value, out var source)
            ? source
            : throw new ArgumentException(
                $"Unknown audio source '{value}'. Allowed: {Mic}, {Loopback}.",
                nameof(value));
}
