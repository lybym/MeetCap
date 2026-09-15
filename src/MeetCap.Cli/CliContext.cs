namespace MeetCap.Cli;

using System.CommandLine;
using MeetCap.Core.Configuration;
using MeetCap.Core.Secrets;
using Microsoft.Extensions.Logging;

/// <summary>
/// Ambient command-line environment (the <c>%APPDATA%</c> root). Extracted so the
/// environment lookup is overridable in tests without mutating process state.
/// </summary>
internal class CliEnvironment
{
    public static readonly CliEnvironment Instance = new();

    /// <summary>The default configuration directory, <c>%APPDATA%\MeetCap</c>.</summary>
    public virtual string DefaultConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeetCap");
}

/// <summary>
/// Everything a command handler needs to run. Global options are resolved here so
/// no handler reads process-wide state directly and every command is testable.
/// </summary>
internal sealed class CliContext
{
    private readonly ParseResult _parseResult;
    private readonly CliEnvironment _environment;

    public CliContext(
        ParseResult parseResult,
        TextWriter output,
        TextWriter error,
        IConfigurationStore configurationStore,
        SecretRegistry secrets,
        ILoggerFactory loggerFactory,
        CliEnvironment? environment = null)
    {
        _parseResult = parseResult ?? throw new ArgumentNullException(nameof(parseResult));
        Out = output ?? throw new ArgumentNullException(nameof(output));
        Error = error ?? throw new ArgumentNullException(nameof(error));
        ConfigurationStore = configurationStore ?? throw new ArgumentNullException(nameof(configurationStore));
        Secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        LoggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _environment = environment ?? CliEnvironment.Instance;
    }

    /// <summary>Logger category used by every command handler.</summary>
    public const string LoggerCategory = "MeetCap";

    public IConfigurationStore ConfigurationStore { get; }

    public SecretRegistry Secrets { get; }

    public ILoggerFactory LoggerFactory { get; }

    /// <summary>Standard output for command results (never contains secrets).</summary>
    public TextWriter Out { get; }

    /// <summary>Standard error for diagnostics; the logger writes here too.</summary>
    public TextWriter Error { get; }

    /// <summary>
    /// The effective configuration directory: the one-shot <c>--config-dir</c>
    /// override when supplied, otherwise <c>%APPDATA%\MeetCap</c>.
    /// </summary>
    public string ConfigDirectory =>
        GlobalOptions.GetConfigDir(_parseResult) ?? _environment.DefaultConfigDirectory;

    /// <summary>
    /// The one-shot <c>--data-root</c> override, or <c>null</c> to fall back to the
    /// configured <c>storage.data_root</c>. One-shot overrides never rewrite config.toml.
    /// </summary>
    public string? DataRootOverride => GlobalOptions.GetDataRoot(_parseResult);
}

/// <summary>
/// Global options shared by every command. Declared once so help output and
/// validation are uniform across the command hierarchy.
/// </summary>
internal static class GlobalOptions
{
    public static readonly Option<string?> ConfigDir = new("--config-dir")
    {
        Description = "Configuration directory containing config.toml (default: %APPDATA%\\MeetCap).",
    };

    public static readonly Option<string?> DataRoot = new("--data-root")
    {
        Description = "One-shot override for the data root (does not rewrite config.toml).",
    };

    public static string? GetConfigDir(ParseResult parseResult) => GetValue(parseResult, ConfigDir);

    public static string? GetDataRoot(ParseResult parseResult) => GetValue(parseResult, DataRoot);

    private static T? GetValue<T>(ParseResult parseResult, Option<T> option)
        => parseResult.GetResult(option) is null ? default : parseResult.GetValue(option);
}
