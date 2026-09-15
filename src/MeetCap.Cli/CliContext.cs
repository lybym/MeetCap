namespace MeetCap.Cli;

using System.CommandLine;
using MeetCap.AudioPipeline;
using MeetCap.Core.Capture;
using MeetCap.Core.Configuration;
using MeetCap.Core.Diagnostics;
using MeetCap.Core.Secrets;
using MeetCap.Persistence.Storage;
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
    private readonly Func<string, IConfigurationStore> _storeFactory;
    private readonly CliEnvironment _environment;
    private readonly ICapturePlatformFactory _platformFactory;

    public CliContext(
        ParseResult parseResult,
        TextWriter output,
        TextWriter error,
        Func<string, IConfigurationStore> storeFactory,
        SecretRegistry secrets,
        ILoggerFactory loggerFactory,
        CliEnvironment? environment = null,
        ICapturePlatformFactory? platformFactory = null)
    {
        _parseResult = parseResult ?? throw new ArgumentNullException(nameof(parseResult));
        Out = output ?? throw new ArgumentNullException(nameof(output));
        Error = error ?? throw new ArgumentNullException(nameof(error));
        _storeFactory = storeFactory ?? throw new ArgumentNullException(nameof(storeFactory));
        Secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        LoggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _environment = environment ?? CliEnvironment.Instance;
        _platformFactory = platformFactory ?? NAudioCapturePlatformFactory.Instance;
    }

    /// <summary>Logger category used by every command handler.</summary>
    public const string LoggerCategory = "MeetCap";

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

    /// <summary>
    /// The configuration store for the effective <see cref="ConfigDirectory"/>.
    /// Resolved per command so a one-shot <c>--config-dir</c> override is honored.
    /// </summary>
    public IConfigurationStore ConfigurationStore => _storeFactory(ConfigDirectory);

    /// <summary>
    /// The data root a capture command will use: the one-shot <c>--data-root</c>
    /// override when supplied, otherwise the expanded <c>storage.data_root</c>
    /// (docs/CONFIGURATION.md section 2).
    /// </summary>
    /// <exception cref="MeetCapException">The configured data root is empty.</exception>
    public string ResolveDataRoot(MeetCapConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = DataRootOverride is { Length: > 0 } over
            ? over
            : configuration.Storage.DataRoot;

        var expanded = Environment.ExpandEnvironmentVariables(configured ?? string.Empty);
        if (string.IsNullOrWhiteSpace(expanded))
        {
            throw new MeetCapException(
                "storage.data_root is empty, so there is nowhere to write recordings. " +
                "Set it in config.toml or pass --data-root.");
        }

        try
        {
            return Path.GetFullPath(expanded);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new MeetCapException($"storage.data_root '{expanded}' is not a usable path: {ex.Message}", ex);
        }
    }

    /// <summary>The capture platform for this invocation.</summary>
    public CapturePlatform CreateCapturePlatform() => _platformFactory.Create();

    /// <summary>
    /// Creates the capture service for a session: it migrates the database first, so
    /// every capture command starts from a known schema.
    /// </summary>
    public CaptureService CreateCaptureService(CaptureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var database = new MeetCapDatabase(Path.Combine(settings.DataRoot, "meetcap.db"));
        database.EnsureMigrated();
        return new CaptureService(CreateCapturePlatform(), database, settings);
    }
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
