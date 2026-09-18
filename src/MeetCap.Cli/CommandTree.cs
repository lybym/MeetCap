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
        SecretRegistry secrets,
        ILoggerFactory loggerFactory,
        Func<string, IConfigurationStore> storeFactory,
        TextWriter output,
        TextWriter error,
        CliEnvironment? environment = null,
        ICapturePlatformFactory? platformFactory = null,
        HttpMessageHandler? asrHttpHandler = null)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(storeFactory);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var root = new RootCommand("MeetCap local-first meeting capture CLI.");
        root.Options.Add(GlobalOptions.ConfigDir);
        root.Options.Add(GlobalOptions.DataRoot);

        var configCommand = new Command("config", "Inspect and initialize MeetCap configuration.");
        configCommand.Subcommands.Add(BuildConfigInit(secrets, loggerFactory, storeFactory, output, error, environment, platformFactory, asrHttpHandler));
        configCommand.Subcommands.Add(BuildConfigPath(secrets, loggerFactory, storeFactory, output, error, environment, platformFactory, asrHttpHandler));
        configCommand.Subcommands.Add(BuildConfigValidate(secrets, loggerFactory, storeFactory, output, error, environment, platformFactory, asrHttpHandler));
        configCommand.Subcommands.Add(BuildConfigShow(secrets, loggerFactory, storeFactory, output, error, environment, platformFactory, asrHttpHandler));

        var statusCommand = new Command(
            "status",
            "Show configuration, data root, database, and any session that was not cleanly stopped.");

        var devicesCommand = new Command("devices", "List the capture devices Windows currently offers.");

        var titleArgument = new Argument<string?>("title")
        {
            Description = "Session title (defaults to app.default_title).",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var modeOption = new Option<string?>("--mode", "-m")
        {
            Description = "Session mode. M1 supports offline; online arrives with M5.",
        };

        var startCommand = new Command("start", "Start an offline recording session.");
        startCommand.Arguments.Add(titleArgument);
        startCommand.Options.Add(modeOption);

        var stopCommand = new Command("stop", "Stop the recording session that is currently running.");

        var importCommand = BuildImport(secrets, loggerFactory, storeFactory, output, error, environment, asrHttpHandler);
        var asrCommand = BuildAsr(secrets, loggerFactory, storeFactory, output, error, environment, asrHttpHandler);
        var sessionCommand = BuildSession(secrets, loggerFactory, storeFactory, output, error, environment, platformFactory, asrHttpHandler);
        var speakersCommand = BuildSpeakers(secrets, loggerFactory, storeFactory, output, error, environment, platformFactory, asrHttpHandler);

        root.Subcommands.Add(configCommand);
        root.Subcommands.Add(statusCommand);
        root.Subcommands.Add(devicesCommand);
        root.Subcommands.Add(startCommand);
        root.Subcommands.Add(stopCommand);
        root.Subcommands.Add(importCommand);
        root.Subcommands.Add(asrCommand);
        root.Subcommands.Add(sessionCommand);
        root.Subcommands.Add(speakersCommand);

        root.SetAction(parseResult => RequireVerb(parseResult, root, error));
        configCommand.SetAction(parseResult => RequireSubcommand(parseResult, configCommand, error));
        asrCommand.SetAction(parseResult => RequireSubcommand(parseResult, asrCommand, error));
        sessionCommand.SetAction(parseResult => RequireSubcommand(parseResult, sessionCommand, error));
        speakersCommand.SetAction(parseResult => RequireSubcommand(parseResult, speakersCommand, error));
        statusCommand.SetAction((parseResult, _) =>
            StatusCommand.Run(CreateContext(parseResult, secrets, loggerFactory, storeFactory, output, error, environment, platformFactory, asrHttpHandler)));
        devicesCommand.SetAction((parseResult, _) =>
            DevicesCommand.Run(CreateContext(parseResult, secrets, loggerFactory, storeFactory, output, error, environment, platformFactory, asrHttpHandler)));
        startCommand.SetAction((parseResult, _) =>
            StartCommand.Run(
                CreateContext(parseResult, secrets, loggerFactory, storeFactory, output, error, environment, platformFactory, asrHttpHandler),
                parseResult.GetValue(titleArgument),
                parseResult.GetValue(modeOption)));
        stopCommand.SetAction((parseResult, _) =>
            StopCommand.Run(CreateContext(parseResult, secrets, loggerFactory, storeFactory, output, error, environment, platformFactory, asrHttpHandler)));

        return root;
    }

    /// <summary>
    /// Builds <c>meetcap import &lt;file&gt; [--title &lt;title&gt;]</c>.
    /// </summary>
    /// <remarks>
    /// There is no service-tier option: issue #26 removed the tier selector along with the
    /// idle and flash/turbo services, so there is nothing to override.
    /// </remarks>
    private static Command BuildImport(
        SecretRegistry secrets,
        ILoggerFactory loggerFactory,
        Func<string, IConfigurationStore> storeFactory,
        TextWriter output,
        TextWriter error,
        CliEnvironment? environment,
        HttpMessageHandler? asrHttpHandler)
    {
        var file = new Argument<string>("file")
        {
            Description = "Path to the recording to import (mp3, m4a, mp4, wav, ...).",
        };

        var title = new Option<string?>("--title", "-t")
        {
            Description = "Session title. Defaults to the file name without its extension.",
        };

        var command = new Command("import", "Import an existing recording and transcribe it with file ASR.");
        command.Arguments.Add(file);
        command.Options.Add(title);
        command.SetAction((parseResult, cancellationToken) =>
        {
            var context = CreateContext(
                parseResult,
                secrets,
                loggerFactory,
                storeFactory,
                output,
                error,
                environment,
                platformFactory: null,
                asrHttpHandler);
            return ImportCommand.RunAsync(
                context,
                parseResult.GetValue(file) ?? string.Empty,
                parseResult.GetValue(title),
                cancellationToken);
        });

        return command;
    }

    /// <summary>Builds the <c>meetcap asr</c> command family.</summary>
    private static Command BuildAsr(
        SecretRegistry secrets,
        ILoggerFactory loggerFactory,
        Func<string, IConfigurationStore> storeFactory,
        TextWriter output,
        TextWriter error,
        CliEnvironment? environment,
        HttpMessageHandler? asrHttpHandler)
    {
        var session = new Option<string?>("--session", "-s")
        {
            Description = "Only resume ASR jobs belonging to this session id.",
        };

        var maxJobs = new Option<int?>("--max-jobs")
        {
            Description = "Maximum number of jobs to process in this invocation (default 100).",
        };

        var force = new Option<bool>("--force", "-f")
        {
            Description =
                "Process jobs waiting out their durable retry backoff now, for when the reason for the " +
                "backoff (for example a lost network) is known to be over.",
        };

        var resume = new Command(
            "resume",
            "Resume pending/retry-wait ASR jobs, including work left behind by a killed process.");
        resume.Options.Add(session);
        resume.Options.Add(maxJobs);
        resume.Options.Add(force);
        resume.SetAction((parseResult, cancellationToken) =>
        {
            var context = CreateContext(
                parseResult,
                secrets,
                loggerFactory,
                storeFactory,
                output,
                error,
                environment,
                platformFactory: null,
                asrHttpHandler);
            return AsrCommand.ResumeAsync(
                context,
                parseResult.GetValue(session),
                parseResult.GetValue(maxJobs) ?? 100,
                parseResult.GetValue(force),
                cancellationToken);
        });

        var command = new Command("asr", "Inspect and resume the persistent ASR job queue.");
        command.Subcommands.Add(resume);
        return command;
    }

    /// <summary>Builds the <c>meetcap session</c> command family.</summary>
    private static Command BuildSession(
        SecretRegistry secrets,
        ILoggerFactory loggerFactory,
        Func<string, IConfigurationStore> storeFactory,
        TextWriter output,
        TextWriter error,
        CliEnvironment? environment,
        ICapturePlatformFactory? platformFactory,
        HttpMessageHandler? asrHttpHandler)
    {
        var session = new Option<string?>("--session", "-s")
        {
            Description = "Only repair this session id. Defaults to every session under the data root.",
        };

        var repair = new Command(
            "repair",
            "Repair and audit recording sessions left behind by a killed process.");
        repair.Options.Add(session);
        repair.SetAction((parseResult, _) => SessionCommand.RepairAsync(
            CreateContext(parseResult, secrets, loggerFactory, storeFactory, output, error, environment, platformFactory, asrHttpHandler),
            parseResult.GetValue(session)));

        var command = new Command("session", "Repair and audit recorded sessions.");
        command.Subcommands.Add(repair);
        return command;
    }

    /// <summary>Builds the <c>meetcap speakers</c> command family (M6/#8).</summary>
    private static Command BuildSpeakers(
        SecretRegistry secrets,
        ILoggerFactory loggerFactory,
        Func<string, IConfigurationStore> storeFactory,
        TextWriter output,
        TextWriter error,
        CliEnvironment? environment,
        ICapturePlatformFactory? platformFactory,
        HttpMessageHandler? asrHttpHandler)
    {
        var list = new Command("list", "List enrolled speakers.");
        list.SetAction(parseResult =>
        {
            var context = CreateContext(parseResult, secrets, loggerFactory, storeFactory, output, error, environment, platformFactory, asrHttpHandler);
            return SpeakersCommand.List(context);
        });

        var nameArg = new Argument<string>("name")
        {
            Description = "Display name for the speaker (or an existing name to add another sample).",
        };

        var fileOpt = new Option<string?>("--file", "-f")
        {
            Description = "Path to a WAV file (16-bit PCM or 32-bit float) to enroll from.",
        };

        var enroll = new Command("enroll", "Enroll a named speaker from a WAV file.");
        enroll.Arguments.Add(nameArg);
        enroll.Options.Add(fileOpt);
        enroll.SetAction((parseResult, cancellationToken) =>
        {
            var context = CreateContext(parseResult, secrets, loggerFactory, storeFactory, output, error, environment, platformFactory, asrHttpHandler);
            return SpeakersCommand.EnrollAsync(
                context,
                parseResult.GetValue(nameArg) ?? string.Empty,
                parseResult.GetValue(fileOpt) ?? string.Empty,
                cancellationToken);
        });

        var assignSession = new Option<string>("--session", "-s")
        {
            Description = "The session whose speaker label is being assigned.",
        };

        var assignLabel = new Option<string>("--label", "-l")
        {
            Description = "The anonymous speaker label to bind (e.g. speaker_0).",
        };

        var assignName = new Option<string>("--name", "-n")
        {
            Description = "The enrolled speaker's display name to bind to this label.",
        };

        var assign = new Command("assign", "Manually bind an anonymous speaker label to an enrolled person (locked).");
        assign.Options.Add(assignSession);
        assign.Options.Add(assignLabel);
        assign.Options.Add(assignName);
        assign.SetAction(parseResult =>
        {
            var context = CreateContext(parseResult, secrets, loggerFactory, storeFactory, output, error, environment, platformFactory, asrHttpHandler);
            return SpeakersCommand.Assign(
                context,
                parseResult.GetValue(assignSession) ?? string.Empty,
                parseResult.GetValue(assignLabel) ?? string.Empty,
                parseResult.GetValue(assignName) ?? string.Empty);
        });

        var attrSession = new Option<string>("--session", "-s")
        {
            Description = "The session to attribute speakers for.",
        };

        var attribute = new Command(
            "attribute",
            "Run the speaker attribution pipeline and write final.jsonl, final.md, and attribution.json.");
        attribute.Options.Add(attrSession);
        attribute.SetAction((parseResult, cancellationToken) =>
        {
            var context = CreateContext(parseResult, secrets, loggerFactory, storeFactory, output, error, environment, platformFactory, asrHttpHandler);
            return SpeakersCommand.AttributeAsync(
                context,
                parseResult.GetValue(attrSession) ?? string.Empty,
                cancellationToken);
        });

        var command = new Command("speakers", "Enroll, assign, and attribute speakers.");
        command.Subcommands.Add(list);
        command.Subcommands.Add(enroll);
        command.Subcommands.Add(assign);
        command.Subcommands.Add(attribute);
        return command;
    }

    private static Command BuildConfigInit(
        SecretRegistry secrets,
        ILoggerFactory loggerFactory,
        Func<string, IConfigurationStore> storeFactory,
        TextWriter output,
        TextWriter error,
        CliEnvironment? environment,
        ICapturePlatformFactory? platformFactory,
        HttpMessageHandler? asrHttpHandler)
    {
        var force = new Option<bool>("--force", "-f")
        {
            Description = "Overwrite an existing config.toml.",
        };

        var command = new Command("init", "Write a default config.toml.");
        command.Options.Add(force);
        command.SetAction(parseResult =>
        {
            var context = CreateContext(parseResult, secrets, loggerFactory, storeFactory, output, error, environment, platformFactory, asrHttpHandler);
            return ConfigCommand.Init(context, context.ConfigurationStore, parseResult.GetValue(force));
        });
        return command;
    }

    private static Command BuildConfigPath(
        SecretRegistry secrets,
        ILoggerFactory loggerFactory,
        Func<string, IConfigurationStore> storeFactory,
        TextWriter output,
        TextWriter error,
        CliEnvironment? environment,
        ICapturePlatformFactory? platformFactory,
        HttpMessageHandler? asrHttpHandler)
    {
        var command = new Command("path", "Print the config.toml path.");
        command.SetAction(parseResult =>
        {
            var context = CreateContext(parseResult, secrets, loggerFactory, storeFactory, output, error, environment, platformFactory, asrHttpHandler);
            return ConfigCommand.PrintPath(context, context.ConfigurationStore);
        });
        return command;
    }

    private static Command BuildConfigValidate(
        SecretRegistry secrets,
        ILoggerFactory loggerFactory,
        Func<string, IConfigurationStore> storeFactory,
        TextWriter output,
        TextWriter error,
        CliEnvironment? environment,
        ICapturePlatformFactory? platformFactory,
        HttpMessageHandler? asrHttpHandler)
    {
        var command = new Command("validate", "Validate config.toml keys and values.");
        command.SetAction(parseResult =>
        {
            var context = CreateContext(parseResult, secrets, loggerFactory, storeFactory, output, error, environment, platformFactory, asrHttpHandler);
            return ConfigCommand.Validate(context, context.ConfigurationStore);
        });
        return command;
    }

    private static Command BuildConfigShow(
        SecretRegistry secrets,
        ILoggerFactory loggerFactory,
        Func<string, IConfigurationStore> storeFactory,
        TextWriter output,
        TextWriter error,
        CliEnvironment? environment,
        ICapturePlatformFactory? platformFactory,
        HttpMessageHandler? asrHttpHandler)
    {
        var command = new Command("show", "Print the effective configuration with secrets redacted.");
        command.SetAction(parseResult =>
        {
            var context = CreateContext(parseResult, secrets, loggerFactory, storeFactory, output, error, environment, platformFactory, asrHttpHandler);
            return ConfigCommand.Show(context, context.ConfigurationStore);
        });
        return command;
    }

    private static CliContext CreateContext(
        ParseResult parseResult,
        SecretRegistry secrets,
        ILoggerFactory loggerFactory,
        Func<string, IConfigurationStore> storeFactory,
        TextWriter output,
        TextWriter error,
        CliEnvironment? environment = null,
        ICapturePlatformFactory? platformFactory = null,
        HttpMessageHandler? asrHttpHandler = null)
        => new(parseResult, output, error, storeFactory, secrets, loggerFactory, environment, platformFactory, asrHttpHandler);

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

    /// <summary>Error action for a verb that requires a subcommand.</summary>
    private static int RequireSubcommand(ParseResult parseResult, Command command, TextWriter error)
    {
        WriteUnmatchedTokenError(error, parseResult);
        var expected = string.Join(", ", command.Subcommands.Select(child => child.Name));
        error.WriteLine($"meetcap {command.Name}: missing subcommand. Expected: {expected}.");
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
