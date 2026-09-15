namespace MeetCap.Cli;

using System.Linq;
using MeetCap.Cli.Commands;
using MeetCap.Cli.Logging;
using MeetCap.Core.Secrets;
using MeetCap.Persistence.Configuration;
using Microsoft.Extensions.Logging;

/// <summary>
/// MeetCap CLI entry point and composition root. Parses global options, wires the
/// configuration store, secret registry and redacting logger, then dispatches to
/// thin command handlers. Commands never duplicate business rules (docs/DEVELOPMENT.md §8).
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

    private static int Run(string[] args)
    {
        string? configDir = null;
        string? dataRoot = null;
        var rest = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config-dir":
                    configDir = TakeValue(args, ref i, "--config-dir");
                    break;
                case "--data-root":
                    dataRoot = TakeValue(args, ref i, "--data-root");
                    break;
                case "-h":
                case "--help":
                    PrintUsage(Console.Error);
                    return 0;
                default:
                    rest.Add(args[i]);
                    break;
            }
        }

        var effectiveConfigDir = configDir
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeetCap");

        var secrets = new SecretRegistry();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new RedactingConsoleLoggerProvider(secrets, LogLevel.Information, Console.Error));
            builder.SetMinimumLevel(LogLevel.Information);
        });
        var logger = loggerFactory.CreateLogger("MeetCap");

        var store = new TomlConfigurationStore(effectiveConfigDir);

        if (rest.Count == 0)
        {
            PrintUsage(Console.Error);
            return 2;
        }

        var verb = rest[0];
        var sub = rest.Count > 1 ? rest[1] : null;
        var subArgs = rest.Count > 2 ? rest.Skip(2).ToArray() : Array.Empty<string>();

        logger.LogInformation("MeetCap CLI starting (config-dir={ConfigDir})", effectiveConfigDir);

        return verb switch
        {
            "config" => ConfigCommand.Run(sub, subArgs, store, secrets, logger),
            "status" => StatusCommand.Run(store, secrets, logger, dataRoot),
            _ => UnknownVerb(verb),
        };
    }

    private static int UnknownVerb(string verb)
    {
        Console.Error.WriteLine($"meetcap: unknown command '{verb}'.");
        PrintUsage(Console.Error);
        return 2;
    }

    private static string TakeValue(string[] args, ref int i, string name)
    {
        if (i + 1 >= args.Length)
        {
            throw new InvalidOperationException($"Option {name} requires a value.");
        }

        return args[++i];
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Usage: meetcap [--config-dir <path>] [--data-root <path>] <command> [args]");
        writer.WriteLine();
        writer.WriteLine("Commands:");
        writer.WriteLine("  config init [--force]      Write a default config.toml");
        writer.WriteLine("  config path                Print the config.toml path");
        writer.WriteLine("  config validate            Validate config.toml (keys and values)");
        writer.WriteLine("  config show               Print effective configuration (secrets redacted)");
        writer.WriteLine("  status                     Show recording/database/session status");
    }
}
