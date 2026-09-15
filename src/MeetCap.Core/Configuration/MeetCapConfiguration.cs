namespace MeetCap.Core.Configuration;

/// <summary>
/// The effective runtime configuration loaded from <c>%APPDATA%\MeetCap\config.toml</c>.
/// </summary>
/// <remarks>
/// Property names map to TOML snake_case keys via the serializer's naming policy.
/// Default values are expressed as property initializers so a freshly constructed
/// instance is the documented baseline (see <see cref="ConfigurationDefaults"/>).
/// This type intentionally has no serialization attributes: MeetCap.Core must stay
/// free of infrastructure concerns. Persistence owns TOML (de)serialization.
/// </remarks>
public sealed class MeetCapConfiguration
{
    public int ConfigVersion { get; set; } = SchemaVersion.Current;

    public AppSection App { get; set; } = new();

    public CaptureSection Capture { get; set; } = new();

    public StorageSection Storage { get; set; } = new();

    public MediaSection Media { get; set; } = new();

    public AsrSection Asr { get; set; } = new();

    public SpeakersSection Speakers { get; set; } = new();

    public TranscriptSection Transcript { get; set; } = new();

    public LoggingSection Logging { get; set; } = new();

    public RetentionSection Retention { get; set; } = new();
}
