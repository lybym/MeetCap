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
        "storage.data_root",
        "storage.minimum_free_space_gb",
        "asr.enabled",
        "asr.strategy",
        "asr.file_batch_seconds",
        "asr.service_tier",
        "asr.streaming_enabled",
        "asr.retry_max_attempts",
        "asr.retry_initial_seconds",
        "asr.retry_max_seconds",
        "asr.final_full_session_pass",
        "asr.volcengine.app_id",
        "asr.volcengine.credential",
        "asr.volcengine.resource_id",
        "asr.volcengine.hotword_table_id",
        "speakers.enabled",
        "speakers.provider",
        "speakers.owner_name",
        "speakers.auto_suggest",
        "speakers.match_threshold",
        "speakers.match_margin",
        "speakers.manual_assignment_locked",
        "transcript.write_jsonl",
        "transcript.write_markdown",
        "transcript.live_markdown",
        "transcript.include_source",
        "transcript.include_timestamps",
        "logging.level",
        "logging.session_log",
        "retention.automatic_delete",
    };

    /// <summary>Whether the given dotted leaf key is part of the schema.</summary>
    public static bool IsValidLeafKey(string dottedKey) => s_validLeafKeys.Contains(dottedKey);

    /// <summary>The full set of valid leaf keys (for diagnostics/tests).</summary>
    public static IReadOnlyCollection<string> ValidLeafKeys => s_validLeafKeys;
}
