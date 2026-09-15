namespace MeetCap.Core.Configuration;

/// <summary>
/// Validates a loaded configuration against docs/CONFIGURATION.md.
/// Pure domain logic: no I/O, no parser. Unknown keys (detected by the
/// persistence layer walking the parsed table) are reported as warnings.
/// </summary>
public static class ConfigurationValidator
{
    private static readonly HashSet<string> s_captureModes = new(StringComparer.Ordinal) { "offline", "online" };
    private static readonly HashSet<string> s_loopbackModes = new(StringComparer.Ordinal) { "system", "process" };
    private static readonly HashSet<string> s_asrStrategies = new(StringComparer.Ordinal) { "file", "streaming" };
    private static readonly HashSet<string> s_asrTiers = new(StringComparer.Ordinal) { "standard", "idle", "turbo" };
    private static readonly HashSet<string> s_logLevels = new(StringComparer.Ordinal)
        { "Trace", "Debug", "Information", "Warning", "Error", "Critical" };

    /// <summary>
    /// Validates values and reports unknown keys. Returns a result whose
    /// <see cref="ValidationResult.Errors"/> are actionable, user-facing strings.
    /// </summary>
    public static ValidationResult Validate(
        MeetCapConfiguration config,
        IReadOnlyList<string>? unknownKeys = null)
    {
        var result = ValidationResult.Success();

        if (config is null)
        {
            result.AddError("Configuration is null.");
            return result;
        }

        if (unknownKeys is not null)
        {
            foreach (var key in unknownKeys)
            {
                result.AddWarning($"Unknown configuration key '{key}' will be ignored.");
            }
        }

        if (config.ConfigVersion != SchemaVersion.Current)
        {
            result.AddError(
                $"Unsupported config_version {config.ConfigVersion}. " +
                $"Current supported version is {SchemaVersion.Current}; migrate or recreate config.toml.");
        }

        ValidateCapture(config, result);
        ValidateStorage(config, result);
        ValidateAsr(config, result);
        ValidateSpeakers(config, result);
        ValidateLogging(config, result);

        return result;
    }

    private static void ValidateCapture(MeetCapConfiguration config, ValidationResult result)
    {
        var capture = config.Capture;
        if (!s_captureModes.Contains(capture.DefaultMode))
        {
            result.AddError(
                $"capture.default_mode='{capture.DefaultMode}' is invalid. Allowed: offline, online.");
        }

        RequirePositive(result, "capture.chunk_seconds", capture.ChunkSeconds);
        RequireNonNegative(result, "capture.buffer_seconds", capture.BufferSeconds);
        RequirePositive(result, "capture.flush_interval_ms", capture.FlushIntervalMs);

        if (!s_loopbackModes.Contains(capture.Online.LoopbackMode))
        {
            result.AddError(
                $"capture.online.loopback_mode='{capture.Online.LoopbackMode}' is invalid. Allowed: system, process.");
        }
    }

    private static void ValidateStorage(MeetCapConfiguration config, ValidationResult result)
    {
        var storage = config.Storage;
        if (string.IsNullOrWhiteSpace(storage.DataRoot))
        {
            result.AddError("storage.data_root must not be empty.");
        }

        if (storage.MinimumFreeSpaceGb <= 0)
        {
            result.AddError(
                $"storage.minimum_free_space_gb={storage.MinimumFreeSpaceGb} must be greater than 0.");
        }
    }

    private static void ValidateAsr(MeetCapConfiguration config, ValidationResult result)
    {
        var asr = config.Asr;
        if (!s_asrStrategies.Contains(asr.Strategy))
        {
            result.AddError($"asr.strategy='{asr.Strategy}' is invalid. Allowed: file, streaming.");
        }

        if (!s_asrTiers.Contains(asr.ServiceTier))
        {
            result.AddError($"asr.service_tier='{asr.ServiceTier}' is invalid. Allowed: standard, idle, turbo.");
        }

        RequirePositive(result, "asr.file_batch_seconds", asr.FileBatchSeconds);
        RequireNonNegative(result, "asr.retry_max_attempts", asr.RetryMaxAttempts);
        RequirePositive(result, "asr.retry_initial_seconds", asr.RetryInitialSeconds);
        RequirePositive(result, "asr.retry_max_seconds", asr.RetryMaxSeconds);

        if (asr.StreamingEnabled && asr.Strategy == "file")
        {
            result.AddWarning(
                "asr.streaming_enabled is true while asr.strategy is 'file'; " +
                "streaming is non-default and must not auto-fallback for failed file ASR.");
        }
    }

    private static void ValidateSpeakers(MeetCapConfiguration config, ValidationResult result)
    {
        var speakers = config.Speakers;
        if (!InRange01(speakers.MatchThreshold))
        {
            result.AddError(
                $"speakers.match_threshold={speakers.MatchThreshold} must be in [0, 1].");
        }

        if (!InRange01(speakers.MatchMargin))
        {
            result.AddError($"speakers.match_margin={speakers.MatchMargin} must be in [0, 1].");
        }
    }

    private static void ValidateLogging(MeetCapConfiguration config, ValidationResult result)
    {
        if (!s_logLevels.Contains(config.Logging.Level))
        {
            result.AddError(
                $"logging.level='{config.Logging.Level}' is invalid. " +
                "Allowed: Trace, Debug, Information, Warning, Error, Critical.");
        }
    }

    private static void RequirePositive(ValidationResult result, string key, int value)
    {
        if (value <= 0)
        {
            result.AddError($"{key}={value} must be greater than 0.");
        }
    }

    private static void RequireNonNegative(ValidationResult result, string key, int value)
    {
        if (value < 0)
        {
            result.AddError($"{key}={value} must not be negative.");
        }
    }

    private static bool InRange01(double value) => value >= 0.0 && value <= 1.0;
}
