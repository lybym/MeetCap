namespace MeetCap.Core.Capture;

using MeetCap.Core.Configuration;
using MeetCap.Core.Diagnostics;
using MeetCap.Core.Sessions;

/// <summary>
/// The resolved, session-scoped capture settings for the loopback track of an online
/// session (docs/ARCHITECTURE.md section 5, docs/ROADMAP.md M5). Carried as a
/// MeetCap-owned value so no NAudio type crosses the assembly boundary.
/// </summary>
public sealed record OnlineCaptureSettings(
    string MicrophoneDeviceId,
    string LoopbackMode,
    string RenderDeviceId,
    string ProcessName)
{
    public string ProcessName { get; } = ProcessName ?? string.Empty;
}

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
        int configVersion,
        string mode = SessionModes.Offline,
        OnlineCaptureSettings? online = null,
        int deviceRecoverySeconds = 20)
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

        if (deviceRecoverySeconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deviceRecoverySeconds),
                deviceRecoverySeconds,
                "Device recovery seconds must not be negative. Use 0 to disable device recovery.");
        }

        if (!SessionModes.IsKnown(mode))
        {
            throw new ArgumentException(
                $"Unknown session mode '{mode}'. Allowed: {SessionModes.Offline}, {SessionModes.Online}.",
                nameof(mode));
        }

        if (online is not null && !string.Equals(mode, SessionModes.Online, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Online capture settings were supplied for a non-online session mode. " +
                $"Mode is '{mode}'; online settings are only valid for '{SessionModes.Online}'.",
                nameof(online));
        }

        DataRoot = dataRoot;
        ChunkSeconds = chunkSeconds;
        BufferSeconds = bufferSeconds;
        FlushIntervalMs = flushIntervalMs;
        MinimumFreeSpaceGb = minimumFreeSpaceGb;
        MicrophoneDeviceId = microphoneDeviceId ?? string.Empty;
        ConfigVersion = configVersion;
        Mode = mode;
        Online = online;
        DeviceRecoverySeconds = deviceRecoverySeconds;
    }

    public string DataRoot { get; }

    public int ChunkSeconds { get; }

    public int BufferSeconds { get; }

    public int FlushIntervalMs { get; }

    public double MinimumFreeSpaceGb { get; }

    public string MicrophoneDeviceId { get; }

    public int ConfigVersion { get; }

    /// <summary>
    /// The bounded device-recovery window, in seconds, that every track of this session
    /// retries its configured endpoint for after the device disappears
    /// (docs/RELIABILITY.md section 8.1). <c>0</c> means "do not attempt recovery".
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the policy an operator configures, and the retry budget is derived from it
    /// rather than configured separately, so a window and the number of attempts that spend
    /// it can never disagree (docs/CONFIGURATION.md section 6).
    /// </para>
    /// <para>
    /// It is snapshotted at session start with the rest of the settings, so a recording that
    /// is already running keeps the policy it started with (docs/CONFIGURATION.md section 12).
    /// </para>
    /// </remarks>
    public int DeviceRecoverySeconds { get; }

    /// <summary>
    /// The session mode these settings started a recording in: <c>offline</c> or
    /// <c>online</c> (docs/PRD.md section 4). The loopback track is captured only when
    /// this is <c>online</c>.
    /// </summary>
    public string Mode { get; }

    /// <summary>
    /// Loopback capture configuration for an online session. <c>null</c> for offline,
    /// in which case only the microphone track is captured (docs/ARCHITECTURE.md
    /// section 5).
    /// </summary>
    public OnlineCaptureSettings? Online { get; }

    /// <summary>True when this session captures both the microphone and loopback tracks.</summary>
    public bool IsOnline => string.Equals(Mode, SessionModes.Online, StringComparison.Ordinal);

    /// <summary>The configured warning threshold in bytes.</summary>
    public long MinimumFreeSpaceBytes => (long)(MinimumFreeSpaceGb * 1024 * 1024 * 1024);

    /// <summary>
    /// Builds the session settings from a configuration snapshot and the already
    /// resolved data root (which may come from the one-shot <c>--data-root</c>
    /// override rather than the file).
    /// </summary>
    /// <param name="mode">
    /// The effective session mode (the <c>--mode</c> override when supplied, otherwise
    /// <c>capture.default_mode</c>). When <c>null</c> or empty, the configured default
    /// mode is used.
    /// </param>
    public static CaptureSettings FromConfiguration(
        MeetCapConfiguration configuration,
        string dataRoot,
        string? mode = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        var capture = configuration.Capture;
        var effectiveMode = string.IsNullOrWhiteSpace(mode)
            ? capture.DefaultMode
            : mode;

        OnlineCaptureSettings? online = null;
        if (string.Equals(effectiveMode, SessionModes.Online, StringComparison.Ordinal))
        {
            online = new OnlineCaptureSettings(
                capture.Online.MicrophoneDeviceId,
                capture.Online.LoopbackMode,
                capture.Online.RenderDeviceId,
                capture.Online.ProcessName);
        }

        try
        {
            // The microphone device id is mode-specific: an online session uses the
            // online microphone setting; an offline session uses the offline one. The
            // pipeline resolves whichever one applies from the mode.
            var microphoneDeviceId = online is not null
                ? online.MicrophoneDeviceId
                : capture.Offline.MicrophoneDeviceId;

            return new CaptureSettings(
                dataRoot,
                capture.ChunkSeconds,
                capture.BufferSeconds,
                capture.FlushIntervalMs,
                configuration.Storage.MinimumFreeSpaceGb,
                microphoneDeviceId,
                configuration.ConfigVersion,
                effectiveMode,
                online,
                capture.DeviceRecoverySeconds);
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
