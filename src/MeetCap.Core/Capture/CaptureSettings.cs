namespace MeetCap.Core.Capture;

using MeetCap.Core.Configuration;
using MeetCap.Core.Diagnostics;

/// <summary>
/// The resolved, session-scoped capture settings. A session uses the configuration
/// snapshot taken at start (docs/CONFIGURATION.md section 12), so everything the
/// pipeline needs is captured once here instead of re-reading config while recording.
/// </summary>
public sealed class CaptureSettings
{
    /// <summary>
    /// Hard floor below which recording stops immediately. Independent of the
    /// user-configured warning threshold: the warning gives the user time to react,
    /// this keeps a nearly full volume from corrupting an in-flight session.
    /// </summary>
    public const long CriticalFreeSpaceBytes = 256L * 1024 * 1024;

    public CaptureSettings(
        string dataRoot,
        int chunkSeconds,
        int bufferSeconds,
        int flushIntervalMs,
        double minimumFreeSpaceGb,
        string microphoneDeviceId,
        int configVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        if (chunkSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSeconds), chunkSeconds, "Chunk seconds must be positive.");
        }

        if (bufferSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferSeconds), bufferSeconds, "Buffer seconds must be positive.");
        }

        if (flushIntervalMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(flushIntervalMs), flushIntervalMs, "Flush interval must be positive.");
        }

        if (minimumFreeSpaceGb <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumFreeSpaceGb),
                minimumFreeSpaceGb,
                "Minimum free space must be greater than 0.");
        }

        DataRoot = dataRoot;
        ChunkSeconds = chunkSeconds;
        BufferSeconds = bufferSeconds;
        FlushIntervalMs = flushIntervalMs;
        MinimumFreeSpaceGb = minimumFreeSpaceGb;
        MicrophoneDeviceId = microphoneDeviceId ?? string.Empty;
        ConfigVersion = configVersion;
    }

    public string DataRoot { get; }

    public int ChunkSeconds { get; }

    public int BufferSeconds { get; }

    public int FlushIntervalMs { get; }

    public double MinimumFreeSpaceGb { get; }

    public string MicrophoneDeviceId { get; }

    public int ConfigVersion { get; }

    /// <summary>The configured warning threshold in bytes.</summary>
    public long MinimumFreeSpaceBytes => (long)(MinimumFreeSpaceGb * 1024 * 1024 * 1024);

    /// <summary>
    /// Builds the session settings from a configuration snapshot and the already
    /// resolved data root (which may come from the one-shot <c>--data-root</c>
    /// override rather than the file).
    /// </summary>
    public static CaptureSettings FromConfiguration(MeetCapConfiguration configuration, string dataRoot)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        var capture = configuration.Capture;
        try
        {
            return new CaptureSettings(
                dataRoot,
                capture.ChunkSeconds,
                capture.BufferSeconds,
                capture.FlushIntervalMs,
                configuration.Storage.MinimumFreeSpaceGb,
                capture.Offline.MicrophoneDeviceId,
                configuration.ConfigVersion);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new MeetCapException(
                $"The configuration on disk cannot start a recording session: {ex.Message} " +
                "Run 'meetcap config validate' for the full list of problems.",
                ex);
        }
    }
}
