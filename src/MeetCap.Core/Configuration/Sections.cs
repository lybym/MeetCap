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

    /// <summary>
    /// How long one capture track keeps retrying its configured endpoint after the device
    /// disappears before it ends that track (docs/RELIABILITY.md section 8).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A wired endpoint that is unplugged and an endpoint that Windows is re-enumerating are
    /// indistinguishable to WASAPI, but they do not come back on the same timescale. A
    /// Bluetooth headset that is switched off and on again — or that simply walks back into
    /// range — takes seconds to reappear as an audio endpoint, which is why the window
    /// defaults comfortably above the fraction of a second a cable replug needs. Without an
    /// explicit window the endpoint was reported unrecoverable after three one-second
    /// retries, so a headset that returned a few seconds later found a track that had
    /// already ended (issue #34).
    /// </para>
    /// <para>
    /// The window stays bounded: when it closes the endpoint is reported unrecoverable and
    /// the track ends, exactly as before. <c>0</c> means "do not attempt recovery at all".
    /// </para>
    /// </remarks>
    public int DeviceRecoverySeconds { get; set; } = 20;

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

/// <summary>
/// External media toolchain location. FFmpeg/FFprobe are an external runtime
/// dependency (<c>docs/ARCHITECTURE.md</c> section 2), so MeetCap resolves them from
/// explicit configuration first and only then falls back to well-known install
/// locations and finally to <c>PATH</c>.
/// </summary>
public sealed class MediaSection
{
    /// <summary>
    /// Directory holding <c>ffmpeg.exe</c> and <c>ffprobe.exe</c>. Empty means
    /// "auto-detect". May use the <c>env:</c> reference scheme.
    /// </summary>
    public string FfmpegBinaryFolder { get; set; } = string.Empty;

    /// <summary>Optional working directory for FFmpeg temporary files. Empty means "auto-detect".</summary>
    public string FfmpegTemporaryFolder { get; set; } = string.Empty;
}

public sealed class AsrSection
{
    public bool Enabled { get; set; } = true;

    /// <summary>Default <c>file</c>. Streaming is non-default and must never auto-fallback for failed file ASR.</summary>
    public string Strategy { get; set; } = "file";

    public int FileBatchSeconds { get; set; } = 300;

    public bool StreamingEnabled { get; set; } = false;
    public int RetryMaxAttempts { get; set; } = 8;
    public int RetryInitialSeconds { get; set; } = 5;
    public int RetryMaxSeconds { get; set; } = 300;
    public bool FinalFullSessionPass { get; set; } = false;

    public VolcengineSection Volcengine { get; set; } = new();

    /// <summary>Optional private TOS staging for oversized file-ASR inputs.</summary>
    public TosSection Tos { get; set; } = new();
}

public sealed class TosSection
{
    public string Bucket { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// Optional role TRN from the cross-service authorization (IAM role trusted by
    /// <c>speech_asr_prod</c>, e.g. <c>AucAccessTosRole</c>). When set, it travels as
    /// <c>audio.role_trn</c> so the provider-side fetch of <c>audio.url</c> is authorized
    /// and billed as internal egress (free in cn-beijing/cn-shanghai) instead of public.
    /// </summary>
    public string RoleTrn { get; set; } = string.Empty;

    /// <summary>
    /// Optional endpoint used only to mint the presigned GET URL, e.g. the bucket's
    /// internal endpoint family (<c>tos-cn-beijing.ivolces.com</c>). The upload and the
    /// delete keep using <see cref="Endpoint"/>; only the URL handed to the provider is
    /// built on this host. Empty means "sign on <see cref="Endpoint"/>" (public).
    /// </summary>
    public string PresignEndpoint { get; set; } = string.Empty;
}

/// <summary>
/// Volcengine provider configuration. Only the API key is user-supplied authentication;
/// the endpoint family, resource id, and request shape are the adapter's fixed contract
/// (<c>docs/CONFIGURATION.md</c> section 8, issue #26).
/// </summary>
public sealed class VolcengineSection
{
    /// <summary>
    /// New-console API key, sent as <c>X-Api-Key</c>. Secret-bearing. May use the
    /// <c>env:</c> reference scheme.
    /// </summary>
    public string ApiKey { get; set; } = "env:MEETCAP_VOLCENGINE_API_KEY";

    public string HotwordTableId { get; set; } = string.Empty;

    /// <summary>Optional per-request hotwords in word or word|weight form.</summary>
    public string[] Hotwords { get; set; } = Array.Empty<string>();

    /// <summary>Optional absolute UTF-8 TXT path; one word or word|weight per line.</summary>
    public string HotwordsFile { get; set; } = string.Empty;

    /// <summary>
    /// Ask the provider for anonymous speaker information. These labels are the default
    /// MVP diarization source and stay anonymous provider-job-scoped data, never
    /// persistent identities.
    /// </summary>
    public bool RequestSpeakerInfo { get; set; } = true;

    /// <summary>
    /// Hourly rate in CNY used only to estimate per-job cost for local accounting.
    /// Provider pricing is configuration, not a product guarantee.
    /// </summary>
    public double CostPerHourCny { get; set; } = 0.8;

    /// <summary>Seconds between provider result queries while a job is polling.</summary>
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Wall-clock budget for polling one job in a single command invocation. When it
    /// expires the job stays persisted and resumable instead of being lost.
    /// </summary>
    public int PollTimeoutSeconds { get; set; } = 900;

    /// <summary>Per-request HTTP timeout in seconds inside the provider adapter.</summary>
    public int HttpTimeoutSeconds { get; set; } = 30;
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

    /// <summary>
    /// Post-finalize archive compression for long audio artifacts
    /// (<c>docs/CONFIGURATION.md</c> section 12, issue #53). Allowed: <c>none</c>,
    /// <c>mp3</c>, <c>aac</c>. <c>none</c> (the default) leaves every artifact as the
    /// lossless WAV the ASR path already produces, so behavior is byte-identical to
    /// today.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The threshold (<see cref="CompressMinSeconds"/>) is mandatory only
    /// <em>when compression is enabled</em>: with <c>compress = "none"</c> no artifact is
    /// ever compressed, so the "over five minutes are always compressed" statement
    /// applies solely while a codec is selected. Artifacts at or below the threshold are
    /// never compressed even when a codec is enabled, so the live 300-second batch
    /// window stays WAV-inline exactly as today.
    /// </para>
    /// <para>
    /// Compression runs off the capture callback and only from a complete, finalized,
    /// lossless artifact (the normalized WAV for imports). While capture is running the
    /// authoritative chunk tree is never rewritten or deleted, so crash recovery is
    /// unaffected (<c>docs/RELIABILITY.md</c> section 3).
    /// </para>
    /// </remarks>
    public string Compress { get; set; } = "none";

    /// <summary>
    /// Target bitrate for the compressed derivative, in kbps. Used only when
    /// <see cref="Compress"/> is not <c>none</c>. ASR accuracy against lossless WAV must
    /// be measured before lowering this default (issue #53 review update).
    /// </summary>
    public int CompressBitrateKbps { get; set; } = 128;

    /// <summary>
    /// Artifacts at or below this many seconds are never compressed. Applies to a
    /// complete finalized session/import derivative, not to 300-second live batches.
    /// Default 300 means artifacts ≤ 5 minutes stay WAV.
    /// </summary>
    public int CompressMinSeconds { get; set; } = 300;

    /// <summary>
    /// Whether the lossless original is kept after a verified successful encode. Default
    /// <c>true</c>: raw sources are kept by default and <c>false</c> deletion is deferred
    /// until every consumer and recovery dependency has been audited (issue #53 review
    /// update). Deletion only ever happens after the derivative's duration has been
    /// verified equal to the source within tolerance.
    /// </summary>
    public bool CompressKeepOriginal { get; set; } = true;

    /// <summary>
    /// Whether the compressed derivative — not the lossless WAV — is the ASR upload
    /// payload (issue #53 Part B). Default <c>false</c>: this is opt-in and must only be
    /// enabled after a real Seed-ASR TOS/inline smoke test with a compressed payload. When
    /// <c>false</c>, <see cref="Compress"/> still produces the archive derivative (Part A)
    /// but the WAV remains the upload payload. When <c>true</c> and a derivative exists,
    /// the job's input artifact points at the derivative so <c>audio.format</c>,
    /// inline-vs-TOS selection, and <c>request.json</c> all reflect the compressed file.
    /// </summary>
    public bool CompressAsrTransport { get; set; } = false;
}

/// <summary>
/// Process-lifecycle auto-capture (<c>meetcap watch</c>, issue #52). Watches a
/// configurable list of meeting-app main processes and starts a recording session when
/// one launches, stopping it when the whole watched cohort exits.
/// </summary>
/// <remarks>
/// <para>
/// This is <strong>process-lifecycle auto-capture, not meeting-state detection</strong>
/// (review update 2026-11-06): a watched app may stay open outside a meeting, so
/// presence only proves the process is running, never that a meeting is in progress.
/// The section is opt-in (<see cref="Enabled"/> defaults to <c>false</c>) and the docs
/// warn that non-meeting audio can be recorded.
/// </para>
/// <para>
/// One active watched-process cohort produces exactly one session spanning the union of
/// overlapping watched apps, not one session per app run. Trigger metadata (process
/// name/ID/start time and stop reason) is persisted per session by the watcher.
/// </para>
/// <para>
/// <see cref="AutoAsr"/> defaults to <c>false</c>: an auto-triggered recording must
/// never silently incur provider billing, and no provider network request is made while
/// <c>false</c>.
/// </para>
/// </remarks>
public sealed class WatchSection
{
    /// <summary>
    /// Whether <c>meetcap watch</c> is allowed to auto-capture. Off by default: the
    /// operator must opt in because process presence is not a verified in-meeting signal.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Main process names to watch (without the <c>.exe</c> extension), e.g.
    /// <c>WeMeetApp</c>, <c>CloudVideo</c>. Helper/child processes never trigger or
    /// extend a recording; only these exact main process names do. A trailing
    /// <c>.exe</c> is stripped by the probe so a user-supplied value still matches.
    /// </summary>
    public List<string> Processes { get; set; } = new() { "WeMeetApp", "CloudVideo" };

    /// <summary>How often the watcher polls for watched processes, in seconds.</summary>
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Allowed: <c>offline</c>, <c>online</c>. The capture mode a watched session uses,
    /// reusing the existing dual-track semantics (<c>online</c> = microphone + loopback).
    /// </summary>
    public string CaptureMode { get; set; } = "online";

    /// <summary>
    /// Whether watched recordings run live file-ASR. <c>false</c> by default so an
    /// auto-triggered recording never makes a provider network request or incurs billing
    /// without an explicit opt-in.
    /// </summary>
    public bool AutoAsr { get; set; } = false;

    /// <summary>
    /// Exit debounce in seconds. When every watched process is gone the watcher waits
    /// this long before finalizing, so brief helper churn or an app relaunch within the
    /// window keeps one cohort/session alive instead of producing a gap.
    /// </summary>
    public int ExitDebounceSeconds { get; set; } = 10;

    /// <summary>
    /// Session title template. <c>{app}</c> expands to the triggering process name and
    /// <c>{start:format}</c> expands to the trigger observation time using a .NET date
    /// format string, e.g. <c>{start:yyyy-MM-dd HH-mm-ss}</c>.
    /// </summary>
    public string TitleTemplate { get; set; } = "{app} {start:yyyy-MM-dd HH-mm-ss}";
}
