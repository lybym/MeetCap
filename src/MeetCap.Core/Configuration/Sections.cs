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

    /// <summary>
    /// Target process name, used only when <see cref="LoopbackMode"/> is
    /// <c>process</c>. Process loopback is an additive capture-source option, never a
    /// separate meeting mode, and system loopback stays the baseline.
    /// </summary>
    public string ProcessName { get; set; } = string.Empty;
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

    /// <summary>
    /// Ask the provider for anonymous speaker information where the configured API
    /// tier supports it. These labels are the default MVP diarization source and
    /// stay anonymous session/provider-scoped data, never persistent identities.
    /// </summary>
    public bool RequestSpeakerInfo { get; set; } = true;
}

/// <summary>
/// Speaker attribution configuration. Diarization (who spoke when) and identity
/// (who is speaker_1) are separate concerns: the provider supplies anonymous
/// labels, and only the local identity provider may resolve them to a person.
/// </summary>
public sealed class SpeakersSection
{
    public bool Enabled { get; set; } = true;

    public string OwnerName { get; set; } = string.Empty;
    public bool AutoSuggest { get; set; } = true;

    /// <summary>Manual assignment is authoritative and is never overwritten by automatic inference.</summary>
    public bool ManualAssignmentLocked { get; set; } = true;

    public SpeakerIdentitySection Identity { get; set; } = new();

    public SpeakerSherpaOnnxSection SherpaOnnx { get; set; } = new();
}

/// <summary>
/// Local speaker identity matching policy: turning anonymous provider labels into
/// ranked candidates for enrolled people. Implemented by M6; the keys are part of
/// the configuration contract from M0 so the surface cannot drift.
/// </summary>
public sealed class SpeakerIdentitySection
{
    /// <summary>Allowed: <c>sherpa_onnx_3dspeaker</c>.</summary>
    public string Provider { get; set; } = "sherpa_onnx_3dspeaker";

    /// <summary>Placeholder until calibrated on real Chinese meeting recordings.</summary>
    public double MatchThreshold { get; set; } = 0.82;

    /// <summary>Required lead of the best candidate over the runner-up.</summary>
    public double MatchMargin { get; set; } = 0.08;

    /// <summary>Preferred lower bound of a clean enrollment/match speech sample.</summary>
    public int SampleMinSeconds { get; set; } = 5;

    /// <summary>Preferred upper bound of a clean enrollment/match speech sample.</summary>
    public int SampleMaxSeconds { get; set; } = 15;
}

/// <summary>Local sherpa-onnx + 3D-Speaker ERes2Net-base embedding runtime.</summary>
public sealed class SpeakerSherpaOnnxSection
{
    public string Model { get; set; } = "3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx";

    /// <summary>Optional explicit model directory; empty means "resolve from the data root".</summary>
    public string ModelPath { get; set; } = string.Empty;
}

public sealed class TranscriptSection
{
    public bool WriteJsonl { get; set; } = true;
    public bool WriteMarkdown { get; set; } = true;
    public bool LiveMarkdown { get; set; } = true;
    public bool IncludeSource { get; set; } = true;
    public bool IncludeTimestamps { get; set; } = true;

    /// <summary>
    /// Keep the anonymous <c>speaker_label</c> separate from the resolved
    /// <c>speaker_id</c>/<c>speaker_name</c> in transcript output.
    /// </summary>
    public bool IncludeSpeakerLabels { get; set; } = true;
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
