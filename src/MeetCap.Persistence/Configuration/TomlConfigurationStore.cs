namespace MeetCap.Persistence.Configuration;

using System.Text.Json;
using MeetCap.Core.Configuration;
using Tomlyn;
using Tomlyn.Model;

/// <summary>
/// Loads/writes <c>%APPDATA%\MeetCap\config.toml</c> using Tomlyn. Core owns the
/// configuration model and validation; this class owns only TOML (de)serialization
/// and the file location. Unknown keys are detected by walking the parsed dynamic
/// table so that Core stays free of any parser dependency.
/// </summary>
public sealed class TomlConfigurationStore : IConfigurationStore
{
    private static readonly TomlSerializerOptions s_options = new()
    {
        // C# PascalCase properties map to snake_case TOML keys in both directions.
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        // Duplicate keys in a single file are a user mistake, not a silent override.
        DuplicateKeyHandling = TomlDuplicateKeyHandling.Error,
    };

    public string ConfigFilePath { get; }

    public TomlConfigurationStore(string configDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        ConfigFilePath = Path.Combine(configDirectory, "config.toml");
    }

    public bool Exists() => File.Exists(ConfigFilePath);

    public ConfigLoadResult Load()
    {
        if (!Exists())
        {
            return new ConfigLoadResult(
                ConfigurationDefaults.Default(),
                Array.Empty<string>(),
                $"Configuration file not found: {ConfigFilePath}. Run 'meetcap config init'.");
        }

        string text;
        try
        {
            text = File.ReadAllText(ConfigFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ConfigLoadResult(
                ConfigurationDefaults.Default(),
                Array.Empty<string>(),
                $"Cannot read configuration file {ConfigFilePath}: {ex.Message}");
        }

        MeetCapConfiguration config;
        try
        {
            config = TomlSerializer.Deserialize<MeetCapConfiguration>(text, s_options)
                ?? throw new InvalidOperationException("Configuration parser returned null for a non-null document.");
        }
        catch (TomlException ex)
        {
            return new ConfigLoadResult(
                ConfigurationDefaults.Default(),
                Array.Empty<string>(),
                $"Invalid TOML in {ConfigFilePath}: {ex.Message}");
        }

        var unknownKeys = CollectUnknownKeysBestEffort(text);
        return new ConfigLoadResult(config, unknownKeys, null);
    }

    public void WriteDefault(bool overwrite)
    {
        if (Exists() && !overwrite)
        {
            throw new InvalidOperationException(
                $"Configuration file already exists at {ConfigFilePath}. Use --force to overwrite.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ConfigFilePath)!);
        var toml = TomlSerializer.Serialize(ConfigurationDefaults.Default(), s_options);
        File.WriteAllText(ConfigFilePath, toml);
    }

    public string ToToml(MeetCapConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return TomlSerializer.Serialize(config, s_options);
    }

    /// <summary>
    /// Best-effort unknown-key detection: parse the same text into a dynamic
    /// <see cref="TomlTable"/> and walk it, reporting leaf paths not in the schema.
    /// Never throws; on any failure no unknown keys are reported.
    /// </summary>
    private IReadOnlyList<string> CollectUnknownKeysBestEffort(string text)
    {
        try
        {
            if (TomlSerializer.Deserialize(text, typeof(TomlTable), s_options) is not TomlTable root)
            {
                return Array.Empty<string>();
            }

            var unknown = new List<string>();
            Walk(root, string.Empty, unknown);
            return unknown;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static void Walk(TomlTable table, string prefix, List<string> unknown)
    {
        foreach (var pair in table)
        {
            var path = prefix.Length == 0 ? pair.Key : prefix + "." + pair.Key;
            if (pair.Value is TomlTable sub)
            {
                Walk(sub, path, unknown);
            }
            else if (!ConfigSchema.IsValidLeafKey(path))
            {
                unknown.Add(path);
            }
        }
    }
}
