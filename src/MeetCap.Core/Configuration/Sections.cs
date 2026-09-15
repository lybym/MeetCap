namespace MeetCap.Core.Configuration;

// All defaults mirror docs/CONFIGURATION.md and config.example.toml exactly.
// Defaults are the single source of truth; `config init` serializes a defaulted
// instance, so there is no duplicated example string to keep in sync.

public sealed class AppSection
{
    public string DefaultTitle { get; set; } = "Untitled Meeting";
}

public sealed class CaptureSection
{
    /// <summary>Allowed: <c>offline</c>, <c>online</c>. <c>import</c> is command-driven; <c>hybrid</c> is invalid.</summary>
    public string DefaultMode { get; set; } = "offline";

    public int ChunkSeconds { get; set; } = 60;
    public int BufferSeconds { get; set; } = 5;
    public int FlushIntervalMs { get; set; } = 1000;

    public CaptureOfflineSection Offline { get; set; } = new();
    public CaptureOnlineSection Online { get; set; } = new();
}

public sealed class CaptureOfflineSection
{
    public string MicrophoneDeviceId { get; set; } = "default";
}

public sealed class CaptureOnlineSection
{
    public string MicrophoneDeviceId { get; set; } = "default";

    /// <summary>Allowed: <c>system</c>, <c>process</c>.</summary>
    public string LoopbackMode { get; set; } = "system";

    public string RenderDeviceId { get; set; } = "default";
}

public sealed class StorageSection
{
    /// <summary>May contain <c>%LOCALAPPDATA%</c> placeholder, expanded by the caller.</summary>
    public string DataRoot { get; set; } = "%LOCALAPPDATA%\\MeetCap";

    public double MinimumFreeSpaceGb { get; set; } = 5;
}

public sealed class AsrSection
{
    public bool Enabled { get; set; } = true;

    /// <summary>Default <c>file</c>. Streaming is non-default and must never auto-fallback for failed file ASR.</summary>
    public string Strategy { get; set; } = "file";

    public int FileBatchSeconds { get; set; } = 300;

    /// <summary>Allowed: <c>standard</c>, <c>idle</c>, <c>turbo</c>.</summary>
    public string ServiceTier { get; set; } = "standard";

    public bool StreamingEnabled { get; set; } = false;
    public int RetryMaxAttempts { get; set; } = 8;
    public int RetryInitialSeconds { get; set; } = 5;
    public int RetryMaxSeconds { get; set; } = 300;
    public bool FinalFullSessionPass { get; set; } = false;

    public VolcengineSection Volcengine { get; set; } = new();
}

public sealed class VolcengineSection
{
    public string AppId { get; set; } = string.Empty;

    /// <summary>Secret-bearing. May use <c>env:</c> or <c>credman:</c> reference schemes.</summary>
    public string Credential { get; set; } = "env:MEETCAP_VOLCENGINE_ACCESS_TOKEN";

    public string ResourceId { get; set; } = "volc.bigasr.auc";
    public string HotwordTableId { get; set; } = string.Empty;
}

public sealed class SpeakersSection
{
    public bool Enabled { get; set; } = true;
    public string Provider { get; set; } = "local";
    public string OwnerName { get; set; } = string.Empty;
    public bool AutoSuggest { get; set; } = true;
    public double MatchThreshold { get; set; } = 0.82;
    public double MatchMargin { get; set; } = 0.08;
    public bool ManualAssignmentLocked { get; set; } = true;
}

public sealed class TranscriptSection
{
    public bool WriteJsonl { get; set; } = true;
    public bool WriteMarkdown { get; set; } = true;
    public bool LiveMarkdown { get; set; } = true;
    public bool IncludeSource { get; set; } = true;
    public bool IncludeTimestamps { get; set; } = true;
}

public sealed class LoggingSection
{
    /// <summary>Allowed: Trace, Debug, Information, Warning, Error, Critical.</summary>
    public string Level { get; set; } = "Information";

    public bool SessionLog { get; set; } = true;
}

public sealed class RetentionSection
{
    public bool AutomaticDelete { get; set; } = false;
}
