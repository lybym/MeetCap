namespace MeetCap.Core.Configuration;

/// <summary>
/// Abstraction over loading/writing the canonical configuration file.
/// Implemented by the persistence layer; the CLI depends on this, not on TOML.
/// </summary>
public interface IConfigurationStore
{
    /// <summary>Canonical path to <c>config.toml</c>.</summary>
    string ConfigFilePath { get; }

    /// <summary>Whether a configuration file exists at <see cref="ConfigFilePath"/>.</summary>
    bool Exists();

    /// <summary>
    /// Loads the configuration. Never throws for missing-file or parse errors;
    /// those are reported via <see cref="ConfigLoadResult.LoadError"/>. Missing
    /// keys fall back to documented defaults.
    /// </summary>
    ConfigLoadResult Load();

    /// <summary>
    /// Writes a fresh, fully-defaulted configuration file. Refuses to overwrite an
    /// existing file unless <paramref name="overwrite"/> is true.
    /// </summary>
    void WriteDefault(bool overwrite);

    /// <summary>
    /// Serializes a configuration to TOML text. Used by <c>config show</c> to print
    /// the effective configuration; callers must redact secret material afterward.
    /// </summary>
    string ToToml(MeetCapConfiguration config);
}

/// <summary>Result of loading a configuration file.</summary>
public sealed class ConfigLoadResult
{
    /// <summary>The effective configuration (defaults applied for any missing keys).</summary>
    public MeetCapConfiguration Configuration { get; }

    /// <summary>Dotted paths of keys present in the file but not in the schema.</summary>
    public IReadOnlyList<string> UnknownKeys { get; }

    /// <summary>
    /// Non-null when the file is missing or could not be parsed. When set,
    /// <see cref="Configuration"/> still contains documented defaults.
    /// </summary>
    public string? LoadError { get; }

    public ConfigLoadResult(MeetCapConfiguration configuration, IReadOnlyList<string> unknownKeys, string? loadError)
    {
        Configuration = configuration;
        UnknownKeys = unknownKeys;
        LoadError = loadError;
    }
}
