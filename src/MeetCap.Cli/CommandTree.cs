namespace MeetCap.Cli;

using System.CommandLine;
using MeetCap.Cli.Commands;
using MeetCap.Core.Configuration;
using MeetCap.Core.Secrets;
using Microsoft.Extensions.Logging;

/// <summary>
/// Builds the <c>meetcap</c> command hierarchy with System.CommandLine
/// (docs/ARCHITECTURE.md section 2; Issue #2 requires the library rather than a
/// handwritten parser, so parsing, option validation, usage errors, and help all
/// come from the framework).
/// </summary>
internal static class CommandTree
{
    public static RootCommand Build(
        IConfigurationStore store,
        SecretRegistry secrets,
        ILoggerFactory loggerFactory,
        TextWriter output,
        TextWriter error,
        CliEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var root = new RootCommand("MeetCap local-first meeting capture CLI.");
        root.Options.Add(GlobalOptions.ConfigDir);
        root.Options.Add(GlobalOptions.DataRoot);

        var configCommand = new Command("config", "Inspect and initialize MeetCap configuration.");
        configCommand.Subcommands.Add(BuildConfigInit(store, secrets, loggerFactory, output, error, environment));
        configCommand.Subcommands.Add(BuildConfigPath(store, secrets, loggerFactory, output, error, environment));
        configCommand.Subcommands.Add(BuildConfigValidate(store, secrets, loggerFactory, output, error, environment));
        configCommand.Subcommands.Add(BuildConfigShow(store, secrets, loggerFactory, output, error, environment));

        var statusCommand = new Command("status", "Show database and session status (no session is active in M0).");

        root.Subcommands.Add(configCommand);
        root.Subcommands.Add(statusCommand);

        root.SetAction(parseResult => RequireVerb(parseResult, root, error));
        configCommand.SetAction(parseResult => RequireSubcommand(parseResult, configCommand, error));
        statusCommand.SetAction((parseResult, _) =>
            StatusCommand.Run(CreateContext(parseResult, store, secrets, loggerFactory, output, error, environment)));

        return root;
    }

    private static Command BuildConfigInit(
        IConfigurationStore store,
        SecretRegistry secrets,
        ILoggerFactory loggerFactory,
        TextWriter output,
        TextWriter error,
        CliEnvironment? environment)
    {
        var force = new Option<bool>("--force", "-f")
        {
            Description = "Overwrite an existing config.toml.",
        };

        var command = new Command("init", "Write a default config.toml.");
        command.Options.Add(force);
        command.SetAction(parseResult =>
            ConfigCommand.Init(
                CreateContext(parseResult, store, secrets, loggerFactory, output, error, environment),
                force: parseResult.GetValue(force)));
        return command;
    }

    private static Command BuildConfigPath(
        IConfigurationStore store,
        SecretRegistry secrets,
        ILoggerFactory loggerFactory,
        TextWriter output,
        TextWriter error,
        CliEnvironment? environment)
    {
        var command = new Command("path", "Print the config.toml path.");
        command.SetAction(parseResult =>
            ConfigCommand.PrintPath(
                CreateContext(parseResult, store, secrets, loggerFactory, output, error, environment)));
        return command;
    }

    private static Command BuildConfigValidate(
        IConfigurationStore store,
        SecretRegistry secrets,
        ILoggerFactory loggerFactory,
        TextWriter output,
        TextWriter error,
        CliEnvironment? environment)
    {
        var command = new Command("validate", "Validate config.toml keys and values.");
        command.SetAction(parseResult =>
            ConfigCommand.Validate(
                CreateContext(parseResult, store, secrets, loggerFactory, output, error, environment)));
        return command;
    }

    private static Command BuildConfigShow(
        IConfigurationStore store,
        SecretRegistry secrets,
        ILoggerFactory loggerFactory,
        TextWriter output,
        TextWriter error,
        CliEnvironment? environment)
    {
        var command = new Command("show", "Print the effective configuration with secrets redacted.");
        command.SetAction(parseResult =>
            ConfigCommand.Show(
                CreateContext(parseResult, store, secrets, loggerFactory, output, error, environment)));
        return command;
    }

    private static CliContext CreateContext(
        ParseResult parseResult,
        IConfigurationStore store,
        SecretRegistry secrets,
        ILoggerFactory loggerFactory,
        TextWriter output,
        TextWriter error,
        CliEnvironment? environment)
        => new(parseResult, output, error, store, secrets, loggerFactory, environment);

    /// <summary>
    /// Error action for <c>meetcap</c> without a verb. Mirrors the documented usage
    /// error and returns the CLI's usage exit code.
    /// </summary>
    private static int RequireVerb(ParseResult parseResult, RootCommand root, TextWriter error)
    {
        WriteUnmatchedTokenError(error, parseResult);
        error.WriteLine("meetcap: missing command.");
        WriteUsage(error, root);
        return 2;
    }

    /// <summary>Error action for <c>meetcap config</c> without a subcommand.</summary>
    private static int RequireSubcommand(ParseResult parseResult, Command command, TextWriter error)
    {
        WriteUnmatchedTokenError(error, parseResult);
        error.WriteLine($"meetcap {command.Name}: missing subcommand. Expected: init, path, validate, show.");
        WriteUsage(error, command);
        return 2;
    }

    private static void WriteUnmatchedTokenError(TextWriter error, ParseResult parseResult)
    {
        foreach (var token in parseResult.UnmatchedTokens)
        {
            error.WriteLine($"meetcap: unknown command '{token}'.");
        }
    }

    private static void WriteUsage(TextWriter writer, Command command)
    {
        if (command is RootCommand root)
        {
            writer.WriteLine($"Usage: {root.Name} [--config-dir <path>] [--data-root <path>] <command> [args]");
            writer.WriteLine();
            writer.WriteLine("Commands:");
        }
        else
        {
            writer.WriteLine($"Usage: meetcap {command.Name} [options] <subcommand>");
            writer.WriteLine();
            writer.WriteLine("Subcommands:");
        }

        foreach (var child in command.Subcommands)
        {
            writer.WriteLine($"  {child.Name,-10} {child.Description}");
        }

        writer.WriteLine();
        writer.WriteLine("Run 'meetcap --help' or 'meetcap <command> --help' for full option details.");
    }
}
