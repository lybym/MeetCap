namespace MeetCap.Core.Secrets;

using MeetCap.Core.Configuration;

/// <summary>
/// Mutable registry of the currently-known secret values, updated whenever a
/// configuration is loaded. The logging layer reads <see cref="Values"/> so that
/// secret strings never reach the console/log file.
/// </summary>
public sealed class SecretRegistry
{
    private volatile IReadOnlySet<string> _secrets = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Updates the registry from a freshly loaded configuration.</summary>
    public void UpdateFrom(MeetCapConfiguration config)
    {
        _secrets = SecretRedactor.GetSecretValues(config);
    }

    /// <summary>Currently-known secret values (snapshot for redaction).</summary>
    public IReadOnlySet<string> Values => _secrets;
}
