namespace MeetCap.Core.Secrets;

using MeetCap.Core.Configuration;

/// <summary>
/// Pure secret-redaction primitives. The set of secret-bearing values is derived
/// from a loaded configuration so that logs and `config show` can mask them
/// (docs/ARCHITECTURE.md section 22, CONFIGURATION.md rule 7).
/// </summary>
public static class SecretRedactor
{
    private const string Redacted = "***";

    /// <summary>
    /// The secret-bearing string values present in the configuration (e.g. the
    /// Volcengine credential). Empty values are not treated as secrets.
    /// </summary>
    public static IReadOnlySet<string> GetSecretValues(MeetCapConfiguration config)
    {
        var secrets = new HashSet<string>(StringComparer.Ordinal);
        if (config is null)
        {
            return secrets;
        }

        AddIfNonEmpty(secrets, config.Asr.Volcengine.Credential);
        AddIfNonEmpty(secrets, config.Asr.Volcengine.AppId);
        return secrets;
    }

    /// <summary>
    /// Returns <paramref name="text"/> with every occurrence of any secret value
    /// replaced by <c>***</c>. Longer secrets are redacted before shorter ones so
    /// that a secret which contains another is not partially exposed.
    /// </summary>
    public static string Redact(string text, IReadOnlySet<string> secrets)
    {
        if (string.IsNullOrEmpty(text) || secrets is null || secrets.Count == 0)
        {
            return text ?? string.Empty;
        }

        var ordered = secrets.Where(s => !string.IsNullOrEmpty(s))
            .OrderByDescending(s => s.Length)
            .ThenBy(s => s, StringComparer.Ordinal);
        foreach (var secret in ordered)
        {
            text = text.Replace(secret, Redacted);
        }

        return text;
    }

    private static void AddIfNonEmpty(ISet<string> secrets, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            secrets.Add(value);
        }
    }
}
