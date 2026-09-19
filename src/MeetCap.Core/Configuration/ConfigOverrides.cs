namespace MeetCap.Core.Configuration;

/// <summary>
/// Applies one-shot CLI overrides on top of a loaded configuration.
/// </summary>
/// <remarks>
/// Precedence (docs/CONFIGURATION.md section 2): built-in defaults &lt; config.toml
/// &lt; explicit one-shot CLI arguments. Overrides apply to the current command
/// only and MUST NOT rewrite <c>config.toml</c>; this method performs no I/O and
/// mutates only the in-memory effective configuration passed to it.
/// Only a documented subset of keys is overridable per command.
/// </remarks>
public static class ConfigOverrides
{
    /// <summary>
    /// Applies overrides keyed by dotted path. Throws <see cref="FormatException"/>
    /// with an actionable message if a value cannot be parsed for its key.
    /// </summary>
    public static MeetCapConfiguration Apply(
        MeetCapConfiguration config,
        IReadOnlyDictionary<string, string> overrides)
    {
        foreach (var (key, raw) in overrides)
        {
            switch (key)
            {
                case "capture.default_mode":
                    config.Capture.DefaultMode = raw;
                    break;
                case "capture.chunk_seconds":
                    config.Capture.ChunkSeconds = ParseInt(key, raw);
                    break;
                case "storage.data_root":
                    config.Storage.DataRoot = raw;
                    break;
                case "asr.enabled":
                    config.Asr.Enabled = ParseBool(key, raw);
                    break;
                case "asr.file_batch_seconds":
                    config.Asr.FileBatchSeconds = ParseInt(key, raw);
                    break;
                case "logging.level":
                    config.Logging.Level = raw;
                    break;
                // Unknown override keys are ignored: CLI flags not in the
                // documented overridable set are not silently reinterpreted.
                default:
                    break;
            }
        }

        return config;
    }

    private static int ParseInt(string key, string raw) =>
        int.TryParse(raw, out var v)
            ? v
            : throw new FormatException($"Override '{key}' requires an integer, got '{raw}'.");

    private static bool ParseBool(string key, string raw) =>
        bool.TryParse(raw, out var v)
            ? v
            : throw new FormatException($"Override '{key}' requires true|false, got '{raw}'.");
}
