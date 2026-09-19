namespace MeetCap.Core.Configuration;

/// <summary>
/// The set of valid dotted configuration keys (leaf values only). Used to detect
/// unknown keys in a parsed config file (docs/CONFIGURATION.md rule 5: unknown
/// keys should produce a warning or validation error).
/// </summary>
/// <remarks>
/// This is pure domain knowledge of the configuration contract and does not
/// depend on any parser. Section/table keys are not listed here; only leaves.
/// </remarks>
public static class ConfigSchema
{
    private static readonly HashSet<string> s_validLeafKeys = new(StringComparer.Ordinal)
    {
        "config_version",
        "app.default_title",
        "capture.default_mode",
        "capture.chunk_seconds",
        "capture.buffer_seconds",
        "capture.flush_interval_ms",
        "capture.offline.microphone_device_id",
        "capture.online.microphone_device_id",
        "capture.online.loopback_mode",
        "capture.online.render_device_id",
        "capture.online.process_name",
        "storage.data_root",
        "storage.minimum_free_space_gb",
        "media.ffmpeg_binary_folder",
        "media.ffmpeg_temporary_folder",
        "asr.enabled",
        "asr.strategy",
        "asr.file_batch_seconds",
        "asr.streaming_enabled",
        "asr.retry_max_attempts",
        "asr.retry_initial_seconds",
        "asr.retry_max_seconds",
        "asr.final_full_session_pass",
        // Issue #26: the new-console API key is the only provider authentication key.
        // asr.service_tier, asr.volcengine.app_id, asr.volcengine.credential and
        // asr.volcengine.resource_id were removed and are now reported as legacy keys.
        "asr.volcengine.api_key",
        "asr.volcengine.hotword_table_id",
        "asr.volcengine.request_speaker_info",
        "asr.volcengine.cost_per_hour_cny",
        "asr.volcengine.poll_interval_seconds",
        "asr.volcengine.poll_timeout_seconds",
        "asr.volcengine.http_timeout_seconds",
        "speakers.enabled",
        "speakers.owner_name",
        "speakers.auto_suggest",
        "speakers.manual_assignment_locked",
        "speakers.identity.provider",
        "speakers.identity.match_threshold",
        "speakers.identity.match_margin",
        "speakers.identity.sample_min_seconds",
        "speakers.identity.sample_max_seconds",
        "speakers.sherpa_onnx.model",
        "speakers.sherpa_onnx.model_path",
        "transcript.write_jsonl",
        "transcript.write_markdown",
        "transcript.live_markdown",
        "transcript.include_source",
        "transcript.include_timestamps",
        "transcript.include_speaker_labels",
        "logging.level",
        "logging.session_log",
        "retention.automatic_delete",
    };

    /// <summary>Whether the given dotted leaf key is part of the schema.</summary>
    public static bool IsValidLeafKey(string dottedKey) => s_validLeafKeys.Contains(dottedKey);

    /// <summary>The full set of valid leaf keys (for diagnostics/tests).</summary>
    public static IReadOnlyCollection<string> ValidLeafKeys => s_validLeafKeys;
}
