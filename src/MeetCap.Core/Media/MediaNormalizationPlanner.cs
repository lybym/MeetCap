namespace MeetCap.Core.Media;

/// <summary>
/// Whether a source file must be normalized before it is submitted to file ASR,
/// and why. Normalization runs only when required
/// (<c>docs/ARCHITECTURE.md</c> section 13), so the decision is explicit and
/// testable instead of being buried in adapter code.
/// </summary>
public sealed record MediaNormalizationPlan
{
    public required bool Required { get; init; }

    /// <summary>Human-readable explanation, e.g. "container 'mov' is not ASR-safe".</summary>
    public required string Reason { get; init; }

    /// <summary>Target container for the normalized artifact.</summary>
    public required string TargetFormat { get; init; }

    public required string TargetAudioCodec { get; init; }

    public required int TargetSampleRateHz { get; init; }

    public required int TargetChannels { get; init; }

    /// <summary>The normalized artifact is a PCM WAV file, so the provider format label is fixed.</summary>
    public string TargetProviderFormat => "wav";
}

/// <summary>
/// Decides whether normalization is required. Pure policy: the same input always
/// yields the same plan, which is what makes "normalization only when required"
/// inspectable in tests.
/// </summary>
/// <remarks>
/// The normalized target is 16 kHz mono 16-bit PCM WAV: the narrowest common
/// denominator that every file-ASR tier accepts. A source that is already in that
/// shape is submitted unchanged, so MeetCap never re-encodes audio it does not
/// have to.
/// </remarks>
public static class MediaNormalizationPlanner
{
    public const string TargetFormat = "wav";
    public const string TargetAudioCodec = "pcm_s16le";
    public const int TargetSampleRateHz = 16000;
    public const int TargetChannels = 1;
    public const int TargetBitDepth = 16;

    private static readonly HashSet<string> s_asrSafeFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "wav",
    };

    public static MediaNormalizationPlan Plan(MediaInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        if (!info.HasAudio)
        {
            throw new MediaProbeException(
                $"'{info.Path}' contains no decodable audio stream, so there is nothing to transcribe.");
        }

        if (!IsAsrSafeContainer(info.FormatName))
        {
            return Normalize($"container '{info.FormatName}' is not an ASR-safe container");
        }

        if (!string.Equals(info.AudioCodec, TargetAudioCodec, StringComparison.OrdinalIgnoreCase))
        {
            return Normalize($"audio codec '{info.AudioCodec}' is not {TargetAudioCodec}");
        }

        if (info.SampleRateHz != TargetSampleRateHz)
        {
            return Normalize($"sample rate {info.SampleRateHz} Hz is not {TargetSampleRateHz} Hz");
        }

        if (info.Channels != TargetChannels)
        {
            return Normalize($"{info.Channels} channel(s) are not mono");
        }

        if (info.BitDepth is not null and not TargetBitDepth)
        {
            return Normalize($"bit depth {info.BitDepth} is not {TargetBitDepth}");
        }

        return new MediaNormalizationPlan
        {
            Required = false,
            Reason = "source is already 16 kHz mono 16-bit PCM WAV",
            TargetFormat = TargetFormat,
            TargetAudioCodec = TargetAudioCodec,
            TargetSampleRateHz = TargetSampleRateHz,
            TargetChannels = TargetChannels,
        };
    }

    /// <summary>
    /// FFprobe reports compound container names such as
    /// <c>mov,mp4,m4a,3gp,3g2,mj2</c>; every alternative must be ASR-safe.
    /// </summary>
    private static bool IsAsrSafeContainer(string formatName)
    {
        if (string.IsNullOrWhiteSpace(formatName))
        {
            return false;
        }

        return formatName
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(part => s_asrSafeFormats.Contains(part));
    }

    private static MediaNormalizationPlan Normalize(string reason) => new()
    {
        Required = true,
        Reason = reason,
        TargetFormat = TargetFormat,
        TargetAudioCodec = TargetAudioCodec,
        TargetSampleRateHz = TargetSampleRateHz,
        TargetChannels = TargetChannels,
    };
}
