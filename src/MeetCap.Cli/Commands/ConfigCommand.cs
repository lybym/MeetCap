namespace MeetCap.Cli.Commands;

using System.Linq;
using MeetCap.Core.Configuration;
using MeetCap.Core.Secrets;
using Microsoft.Extensions.Logging;

/// <summary>
/// Thin adapter over <see cref="IConfigurationStore"/> and
/// <see cref="ConfigurationValidator"/> for the <c>meetcap config</c> family.
/// Parsing/output only; no business rules live here.
/// </summary>
public static class ConfigCommand
{
    public static int Run(string? sub, string[] args, IConfigurationStore store, SecretRegistry secrets, ILogger logger)
    {
        return sub switch
        {
            "init" => Init(args, store, logger),
            "path" => PrintPath(store),
            "validate" => Validate(store, secrets, logger),
            "show" => Show(store, secrets, logger),
            null => MissingSubcommand(),
            _ => UnknownSubcommand(sub),
        };
    }

    private static int Init(string[] args, IConfigurationStore store, ILogger logger)
    {
        var overwrite = args.Contains("--force") || args.Contains("-f");
        try
        {
            store.WriteDefault(overwrite);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"meetcap config init: {ex.Message}");
            return 1;
        }

        logger.LogInformation("Configuration initialized at {Path}", store.ConfigFilePath);
        Console.Out.WriteLine($"Wrote default configuration to {store.ConfigFilePath}");
        return 0;
    }

    private static int PrintPath(IConfigurationStore store)
    {
        Console.Out.WriteLine(store.ConfigFilePath);
        return 0;
    }

    private static int Validate(IConfigurationStore store, SecretRegistry secrets, ILogger logger)
    {
        var load = store.Load();
        secrets.UpdateFrom(load.Configuration);

        if (load.LoadError is not null)
        {
            Console.Error.WriteLine($"meetcap config validate: {load.LoadError}");
            return 1;
        }

        var result = ConfigurationValidator.Validate(load.Configuration, load.UnknownKeys);
        foreach (var warning in result.Warnings)
        {
            Console.Error.WriteLine($"warning: {warning}");
        }

        foreach (var error in result.Errors)
        {
            Console.Error.WriteLine($"error: {error}");
        }

        if (result.IsValid)
        {
            Console.Out.WriteLine($"Configuration valid: {store.ConfigFilePath}");
            logger.LogInformation("Configuration validated successfully");
            return 0;
        }

        Console.Error.WriteLine($"Configuration invalid: {result.Errors.Count} error(s), {result.Warnings.Count} warning(s).");
        return 1;
    }

    private static int Show(IConfigurationStore store, SecretRegistry secrets, ILogger logger)
    {
        var load = store.Load();
        secrets.UpdateFrom(load.Configuration);

        if (load.LoadError is not null)
        {
            Console.Error.WriteLine($"meetcap config show: {load.LoadError} (showing defaults)");
        }

        var toml = store.ToToml(load.Configuration);
        var redacted = SecretRedactor.Redact(toml, secrets.Values);
        Console.Out.WriteLine($"# {store.ConfigFilePath}");
        Console.Out.WriteLine(redacted);
        logger.LogInformation("Configuration shown with secrets redacted");
        return 0;
    }

    private static int MissingSubcommand()
    {
        Console.Error.WriteLine("meetcap config: missing subcommand. Expected: init, path, validate, show.");
        return 2;
    }

    private static int UnknownSubcommand(string sub)
    {
        Console.Error.WriteLine($"meetcap config: unknown subcommand '{sub}'. Expected: init, path, validate, show.");
        return 2;
    }
}
