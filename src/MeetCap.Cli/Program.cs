namespace MeetCap.Cli;

using System.CommandLine;
using System.CommandLine.Parsing;
using MeetCap.Cli.Logging;
using MeetCap.Core.Configuration;
using MeetCap.Core.Secrets;
using MeetCap.Persistence.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;

/// <summary>
/// MeetCap CLI entry point and composition root. Builds the System.CommandLine
/// hierarchy, wires the configuration store, secret registry, and the Serilog
/// logging pipeline, then invokes the parsed command. Commands never duplicate
/// business rules (docs/DEVELOPMENT.md section 8).
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"meetcap: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Runs one CLI invocation. The optional parameters exist so integration tests
    /// can drive the real command tree with a temporary configuration store and
    /// captured output writers without mutating process state.
    /// </summary>
    internal static int Run(
        string[] args,
        CliEnvironment? environment = null,
        IConfigurationStore? configurationStore = null,
        InvocationConfiguration? invocationConfiguration = null,
        ParserConfiguration? parserConfiguration = null,
        bool disposeLogger = true)
    {
        ArgumentNullException.ThrowIfNull(args);
        environment ??= CliEnvironment.Instance;

        var output = invocationConfiguration?.Output ?? Console.Out;
        var error = invocationConfiguration?.Error ?? Console.Error;

        var secrets = new SecretRegistry();
        var levelSwitch = new Serilog.Core.LoggingLevelSwitch();
        var logger = CliLoggingFactory.Create(secrets, error, levelSwitch: levelSwitch);

        // The factory forwards Microsoft.Extensions.Logging calls (used by the
        // command handlers) into the Serilog pipeline configured above.
        using var loggerFactory = new Serilog.Extensions.Logging.SerilogLoggerFactory(logger, dispose: true);

        var store = configurationStore ?? new TomlConfigurationStore(environment.DefaultConfigDirectory);
        var root = CommandTree.Build(store, secrets, loggerFactory, output, error, environment);
        var parseResult = CommandLineParser.Parse(root, args, parserConfiguration);

        if (parseResult.Errors.Count > 0)
        {
            WriteParseErrors(parseResult, error);
            return 1;
        }

        ApplyConfiguredLogLevel(store, levelSwitch);

        var exitCode = parseResult.Invoke(invocationConfiguration);

        if (disposeLogger)
        {
            // Serilog's ILogger has no Dispose; CloseAndFlush flushes and closes only
            // this logger instance (unlike the static Log.CloseAndFlush).
            Log.CloseAndFlush(logger);
        }

        return exitCode;
    }

    private static void WriteParseErrors(ParseResult parseResult, TextWriter error)
    {
        foreach (var parseError in parseResult.Errors)
        {
            error.WriteLine($"meetcap: {parseError.Message}");
        }
    }

    /// <summary>
    /// Applies <c>[logging] level</c> from the configuration file to the live
    /// Serilog level switch. Configuration problems are reported later by the
    /// commands themselves; a missing or unreadable file must not block the CLI.
    /// </summary>
    private static void ApplyConfiguredLogLevel(IConfigurationStore store, Serilog.Core.LoggingLevelSwitch levelSwitch)
    {
        try
        {
            var load = store.Load();
            if (load.LoadError is not null)
            {
                return;
            }

            if (Enum.TryParse<LogLevel>(load.Configuration.Logging.Level, ignoreCase: true, out var level))
            {
                levelSwitch.MinimumLevel = MapLevel(level);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging configuration is best-effort at startup; command execution
            // still reports configuration problems with actionable errors.
        }
    }

    private static Serilog.Events.LogEventLevel MapLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => Serilog.Events.LogEventLevel.Verbose,
        LogLevel.Debug => Serilog.Events.LogEventLevel.Debug,
        LogLevel.Information => Serilog.Events.LogEventLevel.Information,
        LogLevel.Warning => Serilog.Events.LogEventLevel.Warning,
        LogLevel.Error => Serilog.Events.LogEventLevel.Error,
        LogLevel.Critical => Serilog.Events.LogEventLevel.Fatal,
        _ => Serilog.Events.LogEventLevel.Fatal,
    };
}
