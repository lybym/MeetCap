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
    /// The secret-bearing string values present in the configuration: the Volcengine API key
    /// reference and the TOS credential references. Empty values are not treated as secrets.
    /// </summary>
    /// <remarks>
    /// Credential <em>references</em> are registered, not the resolved secret. Both an
    /// <c>env:NAME</c> reference as written and the value it resolves to are the same string
    /// whenever the configuration carries a literal credential, so a literal is redacted either
    /// way; a reference is redacted because printing <c>env:TOS_SECRET_KEY</c> at least names
    /// the variable, while printing the resolved value would leak it. The secret resolver hands
    /// the same references to <c>config show</c>, the log formatter and
    /// <c>session.import.json</c>, so one registration covers all three
    /// (<c>docs/CONFIGURATION.md</c> rules 7 and 8).
    /// </remarks>
    public static IReadOnlySet<string> GetSecretValues(MeetCapConfiguration config)
    {
        var secrets = new HashSet<string>(StringComparer.Ordinal);
        if (config is null)
        {
            return secrets;
        }

        AddIfNonEmpty(secrets, config.Asr.Volcengine.ApiKey);
        AddIfNonEmpty(secrets, config.Asr.Tos.AccessKey);
        AddIfNonEmpty(secrets, config.Asr.Tos.SecretKey);
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
