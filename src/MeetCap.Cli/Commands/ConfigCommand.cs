namespace MeetCap.Cli.Commands;

using MeetCap.Core.Configuration;
using MeetCap.Core.Secrets;
using Microsoft.Extensions.Logging;

/// <summary>
/// Thin adapter over <see cref="IConfigurationStore"/> and
/// <see cref="ConfigurationValidator"/> for the <c>meetcap config</c> family.
/// Parsing and output only; no business rules live here.
/// </summary>
internal static class ConfigCommand
{
    public static int Init(CliContext context, IConfigurationStore store, bool force)
    {
        try
        {
            store.WriteDefault(force);
        }
        catch (InvalidOperationException ex)
        {
            context.Error.WriteLine($"meetcap config init: {ex.Message}");
            return 1;
        }

        Logger(context).LogInformation("Configuration initialized at {Path}", store.ConfigFilePath);
        context.Out.WriteLine($"Wrote default configuration to {store.ConfigFilePath}");
        return 0;
    }

    public static int PrintPath(CliContext context, IConfigurationStore store)
    {
        context.Out.WriteLine(store.ConfigFilePath);
        return 0;
    }

    public static int Validate(CliContext context, IConfigurationStore store)
    {
        var load = store.Load();
        context.Secrets.UpdateFrom(load.Configuration);

        if (load.LoadError is not null)
        {
            context.Error.WriteLine($"meetcap config validate: {load.LoadError}");
            return 1;
        }

        var result = ConfigurationValidator.Validate(load.Configuration, load.UnknownKeys);
        foreach (var warning in result.Warnings)
        {
            context.Error.WriteLine($"warning: {warning}");
        }

        foreach (var error in result.Errors)
        {
            context.Error.WriteLine($"error: {error}");
        }

        if (result.IsValid)
        {
            context.Out.WriteLine($"Configuration valid: {store.ConfigFilePath}");
            Logger(context).LogInformation("Configuration validated successfully");
            return 0;
        }

        context.Error.WriteLine($"Configuration invalid: {result.Errors.Count} error(s), {result.Warnings.Count} warning(s).");
        return 1;
    }

    public static int Show(CliContext context)
    {
        // Show the *effective* configuration for this invocation: one-shot CLI
        // arguments outrank config.toml (docs/CONFIGURATION.md section 2) and rule 7
        // requires the effective configuration to be printable. Applying them here
        // mutates only the in-memory copy, so config.toml is never rewritten (rule 4).
        var effective = context.LoadEffectiveConfiguration();
        var load = effective.Load;

        if (load.LoadError is not null)
        {
            context.Error.WriteLine($"meetcap config show: {load.LoadError} (showing defaults)");
        }

        var toml = effective.Store.ToToml(effective.Configuration);
        var redacted = SecretRedactor.Redact(toml, context.Secrets.Values);
        context.Out.WriteLine($"# {effective.Store.ConfigFilePath}");
        context.Out.WriteLine(redacted);
        Logger(context).LogInformation("Configuration shown with secrets redacted");
        return 0;
    }

    private static ILogger Logger(CliContext context)
        => context.LoggerFactory.CreateLogger(CliContext.LoggerCategory);
}
