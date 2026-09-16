namespace MeetCap.Cli.Commands;

using MeetCap.Core.Configuration;
using MeetCap.Core.Secrets;

/// <summary>
/// Shared command preconditions. Configuration is the single source of truth for
/// every command (<c>docs/CONFIGURATION.md</c> section 1), so loading, validating,
/// and resolving the data root happen in one place instead of per command.
/// </summary>
internal static class CommandSupport
{
    /// <summary>
    /// Loads and validates the effective configuration.
    /// </summary>
    /// <remarks>
    /// Loading problems and validation errors are printed and turned into a non-zero
    /// exit code before the caller touches session state, which is what makes an
    /// invalid configuration fail visibly without corrupting a session
    /// (<c>docs/DEVELOPMENT.md</c> section 8).
    /// </remarks>
    public static bool TryLoadConfiguration(
        CliContext context,
        out MeetCapConfiguration configuration,
        out string dataRoot,
        out int exitCode)
    {
        configuration = ConfigurationDefaults.Default();
        dataRoot = string.Empty;
        exitCode = 1;

        var store = context.ConfigurationStore;
        var load = store.Load();
        context.Secrets.UpdateFrom(load.Configuration);

        if (load.LoadError is not null)
        {
            context.Error.WriteLine($"meetcap: {load.LoadError}");
            return false;
        }

        var validation = ConfigurationValidator.Validate(load.Configuration, load.UnknownKeys);
        foreach (var warning in validation.Warnings)
        {
            context.Error.WriteLine($"warning: {warning}");
        }

        if (!validation.IsValid)
        {
            foreach (var error in validation.Errors)
            {
                context.Error.WriteLine($"error: {error}");
            }

            context.Error.WriteLine(
                $"meetcap: configuration is invalid ({validation.Errors.Count} error(s)). " +
                $"Run 'meetcap config validate' for details.");
            return false;
        }

        configuration = load.Configuration;
        dataRoot = context.DataRootOverride
            ?? Environment.ExpandEnvironmentVariables(load.Configuration.Storage.DataRoot);
        exitCode = 0;
        return true;
    }

    /// <summary>Path of the MeetCap SQLite database under the effective data root.</summary>
    public static string DatabasePath(string dataRoot) => Path.Combine(dataRoot, "meetcap.db");
}
