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
    private static readonly HashSet<string> s_speakerIdentityProviders =
        new(StringComparer.Ordinal) { "sherpa_onnx_3dspeaker" };

    /// <summary>
    /// Keys that were part of an earlier configuration contract and must never be
    /// reinterpreted under their old semantics. Each one is promoted from "unknown key"
    /// to a blocking validation error with the migration the user has to perform.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> s_legacyKeyInstructions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["speakers.provider"] =
                "Remove it and set [speakers.identity] provider = \"sherpa_onnx_3dspeaker\" " +
                "after confirming that identity provider is appropriate for your deployment.",
            ["speakers.match_threshold"] =
                "Move its value to [speakers.identity] match_threshold.",
            ["speakers.match_margin"] =
                "Move its value to [speakers.identity] match_margin.",
            // Issue #26 removed the Volcengine service-tier selector, the legacy-console
            // AppID/Access Token pair, and the configurable resource id.
            ["asr.service_tier"] =
                "Remove it. MeetCap supports exactly one recording-file profile " +
                "(Seed-ASR 2.0 Standard HTTP), so there is no service tier to select.",
            ["asr.volcengine.app_id"] =
                "Remove it. The new Volcengine console authenticates with an API key, so set " +
                "[asr.volcengine] api_key instead; X-Api-App-Key/X-Api-Access-Key are not sent.",
            ["asr.volcengine.credential"] =
                "Rename it to [asr.volcengine] api_key and set the new-console API key, for example " +
                "'env:MEETCAP_VOLCENGINE_API_KEY'.",
            ["asr.volcengine.resource_id"] =
                "Remove it. MeetCap fixes X-Api-Resource-Id to 'volc.seedasr.auc' " +
                "(Seed-ASR 2.0 recording-file Standard).",
        };
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
                if (s_legacyKeyInstructions.TryGetValue(key, out var migrationInstruction))
                {
                    result.AddError(
                        $"Legacy configuration key '{key}' is not supported by this config layout. " +
                        migrationInstruction);
                    continue;
                }

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
        ValidateMedia(config, result);
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

        // Zero is a supported value and means "do not attempt device recovery at all", which
        // is a deliberate choice rather than a typo (docs/CONFIGURATION.md section 6). A
        // negative window has no meaning: it would give a track a retry budget smaller than
        // no retries at all.
        RequireNonNegative(result, "capture.device_recovery_seconds", capture.DeviceRecoverySeconds);

        if (!s_loopbackModes.Contains(capture.Online.LoopbackMode))
        {
            result.AddError(
                $"capture.online.loopback_mode='{capture.Online.LoopbackMode}' is invalid. Allowed: system, process.");
        }
        else if (capture.Online.LoopbackMode == "process" && string.IsNullOrWhiteSpace(capture.Online.ProcessName))
        {
            // System loopback is the baseline and needs no target; process loopback is
            // additive and cannot be resolved without a process name.
            result.AddError(
                "capture.online.loopback_mode is 'process' but capture.online.process_name is empty. " +
                "Set the target process name, or use the baseline 'system' loopback.");
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

    private static void ValidateMedia(MeetCapConfiguration config, ValidationResult result)
    {
        // Both keys are optional overrides; empty means "auto-detect". An absolute
        // path is required when set so a relative value cannot silently resolve
        // against whatever working directory the CLI happened to start in.
        RequireAbsoluteOrEmpty(result, "media.ffmpeg_binary_folder", config.Media.FfmpegBinaryFolder);
        RequireAbsoluteOrEmpty(result, "media.ffmpeg_temporary_folder", config.Media.FfmpegTemporaryFolder);
    }

    private static void RequireAbsoluteOrEmpty(ValidationResult result, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (value.StartsWith(CredentialResolverPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // env: references are resolved by the consumer, not by validation.
            return;
        }

        if (!Path.IsPathFullyQualified(value))
        {
            result.AddError(
                $"{key}='{value}' must be an absolute path (or empty for auto-detection).");
        }
    }

    private const string CredentialResolverPrefix = "env:";

    private static void ValidateAsr(MeetCapConfiguration config, ValidationResult result)
    {
        var asr = config.Asr;
        if (!s_asrStrategies.Contains(asr.Strategy))
        {
            result.AddError($"asr.strategy='{asr.Strategy}' is invalid. Allowed: file, streaming.");
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

        var volcengine = asr.Volcengine;
        if (volcengine.CostPerHourCny < 0)
        {
            result.AddError(
                $"asr.volcengine.cost_per_hour_cny={volcengine.CostPerHourCny} must not be negative.");
        }

        RequirePositive(result, "asr.volcengine.poll_interval_seconds", volcengine.PollIntervalSeconds);
        RequirePositive(result, "asr.volcengine.poll_timeout_seconds", volcengine.PollTimeoutSeconds);
        RequirePositive(result, "asr.volcengine.http_timeout_seconds", volcengine.HttpTimeoutSeconds);
    }

    private static void ValidateSpeakers(MeetCapConfiguration config, ValidationResult result)
    {
        var speakers = config.Speakers;
        var identity = speakers.Identity;

        if (!s_speakerIdentityProviders.Contains(identity.Provider))
        {
            result.AddError(
                $"speakers.identity.provider='{identity.Provider}' is invalid. " +
                "Allowed: sherpa_onnx_3dspeaker.");
        }

        if (!InRange01(identity.MatchThreshold))
        {
            result.AddError(
                $"speakers.identity.match_threshold={identity.MatchThreshold} must be in [0, 1].");
        }

        if (!InRange01(identity.MatchMargin))
        {
            result.AddError($"speakers.identity.match_margin={identity.MatchMargin} must be in [0, 1].");
        }

        RequirePositive(result, "speakers.identity.sample_min_seconds", identity.SampleMinSeconds);

        if (identity.SampleMaxSeconds < identity.SampleMinSeconds)
        {
            result.AddError(
                $"speakers.identity.sample_max_seconds={identity.SampleMaxSeconds} must be greater than or " +
                $"equal to speakers.identity.sample_min_seconds={identity.SampleMinSeconds}.");
        }

        // Provider anonymous speaker labels are the default MVP diarization source
        // (docs/ARCHITECTURE.md section 17.2). Without them the identity pipeline has
        // no anonymous clusters to match, so recording still works but nobody is named.
        if (speakers.Enabled && !config.Asr.Volcengine.RequestSpeakerInfo)
        {
            result.AddWarning(
                "speakers.enabled is true while asr.volcengine.request_speaker_info is false; " +
                "provider anonymous speaker labels are the default diarization source, so no speaker " +
                "clusters will be available unless a local diarization fallback is configured.");
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
