namespace MeetCap.Core.Secrets;

/// <summary>
/// Resolves a configured credential reference to its secret value.
/// </summary>
/// <remarks>
/// <para>
/// This is the only place a credential reference is interpreted, and it never logs
/// or returns the reference scheme in diagnostics. The environment lookup is
/// injected so the resolver is pure and testable, and so the rest of the codebase
/// never reads ad-hoc environment variables
/// (<c>docs/ARCHITECTURE.md</c> section 18).
/// </para>
/// <para>
/// Supported schemes: <c>env:NAME</c> and a literal value. <c>credman:</c> is a
/// documented scheme that this milestone does not implement; it fails with an
/// actionable message rather than silently sending an empty credential.
/// </para>
/// </remarks>
public static class CredentialResolver
{
    public const string EnvironmentScheme = "env:";
    public const string CredentialManagerScheme = "credman:";

    public static string Resolve(string? reference, Func<string, string?> environmentLookup)
    {
        ArgumentNullException.ThrowIfNull(environmentLookup);

        if (string.IsNullOrWhiteSpace(reference))
        {
            throw new CredentialResolutionException(
                "No credential is configured. Set asr.volcengine.api_key, for example " +
                "'env:MEETCAP_VOLCENGINE_API_KEY', and make sure the environment variable is set " +
                "for the account that runs meetcap.");
        }

        if (reference.StartsWith(EnvironmentScheme, StringComparison.OrdinalIgnoreCase))
        {
            var name = reference[EnvironmentScheme.Length..].Trim();
            if (name.Length == 0)
            {
                throw new CredentialResolutionException(
                    "Credential reference 'env:' does not name an environment variable. " +
                    "Use 'env:MEETCAP_VOLCENGINE_API_KEY'.");
            }

            var value = environmentLookup(name);
            if (string.IsNullOrEmpty(value))
            {
                throw new CredentialResolutionException(
                    $"Environment variable '{name}' referenced by asr.volcengine.api_key is not set. " +
                    "Set it for the account that runs meetcap; MeetCap never reads credentials from the config file's neighbors.");
            }

            return value;
        }

        if (reference.StartsWith(CredentialManagerScheme, StringComparison.OrdinalIgnoreCase))
        {
            throw new CredentialResolutionException(
                "Credential reference scheme 'credman:' is not implemented yet. " +
                "Use 'env:NAME' and set that environment variable instead.");
        }

        var literal = reference.Trim();
        if (literal.Length == 0)
        {
            throw new CredentialResolutionException("Credential reference resolves to an empty value.");
        }

        return literal;
    }
}

/// <summary>A configured credential reference could not be resolved to a secret value.</summary>
public sealed class CredentialResolutionException : InvalidOperationException
{
    public CredentialResolutionException(string message)
        : base(message)
    {
    }
}
