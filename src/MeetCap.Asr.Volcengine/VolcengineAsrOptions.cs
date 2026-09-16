namespace MeetCap.Asr.Volcengine;

using MeetCap.Core.Asr;
using MeetCap.Core.Configuration;
using MeetCap.Core.Secrets;

/// <summary>
/// Adapter-owned provider configuration. Every provider-specific value -- endpoint
/// root, resource id, protocol limits, transient-retry budget -- lives here and never
/// reaches domain code (<c>docs/ARCHITECTURE.md</c> section 11).
/// </summary>
public sealed record VolcengineAsrOptions
{
    /// <summary>Provider endpoint root. Overridable so tests never touch the real service.</summary>
    public const string DefaultBaseUrl = "https://openspeech.bytedance.com/api/v3/auc/bigmodel";

    public required string AppId { get; init; }

    /// <summary>Access token. Never logged, never persisted, never written to artifacts.</summary>
    public required string AccessToken { get; init; }

    public required string ResourceId { get; init; }

    public string BaseUrl { get; init; } = DefaultBaseUrl;

    public string HotwordTableId { get; init; } = string.Empty;

    /// <summary>Per-request HTTP timeout enforced by the Polly timeout strategy.</summary>
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// In-process transient retry attempts for a single HTTP exchange. This is the
    /// Polly layer only; the durable job state machine has its own, larger budget.
    /// </summary>
    public int MaxTransientAttempts { get; init; } = 3;

    public TimeSpan InitialBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Maximum size of audio sent inline as base64. Larger imports are rejected with an
    /// actionable message rather than silently truncated; splitting is a later milestone.
    /// </summary>
    public long MaxInlineAudioBytes { get; init; } = 100L * 1024 * 1024;

    /// <summary>Allowed: <c>standard</c>, <c>idle</c>. See <see cref="VolcengineTiers"/>.</summary>
    public string ToSubmitPath(string tier) => VolcengineTiers.SubmitPath(BaseUrl, tier);

    public string ToQueryPath(string tier) => VolcengineTiers.QueryPath(BaseUrl, tier);
}

/// <summary>
/// Volcengine file-ASR tier routing.
/// </summary>
/// <remarks>
/// <para>
/// This mapping is provider knowledge and therefore lives inside the adapter. The two
/// supported tiers share the submit/query lifecycle.
/// </para>
/// <para>
/// The <c>turbo</c> tier is a different, single-shot protocol
/// (<c>recognize/flash</c>) that this milestone does not implement. It is rejected
/// with an actionable error instead of being silently mapped onto the submit/query
/// flow and returning a wrong result.
/// </para>
/// </remarks>
public static class VolcengineTiers
{
    public const string Standard = "standard";
    public const string Idle = "idle";
    public const string Turbo = "turbo";

    public static bool IsSupported(string tier) =>
        string.Equals(tier, Standard, StringComparison.Ordinal)
        || string.Equals(tier, Idle, StringComparison.Ordinal);

    public static string SubmitPath(string baseUrl, string tier) => tier switch
    {
        Standard => $"{baseUrl}/submit",
        Idle => $"{baseUrl}/idle/submit",
        _ => throw Unsupported(tier),
    };

    public static string QueryPath(string baseUrl, string tier) => tier switch
    {
        Standard => $"{baseUrl}/query",
        Idle => $"{baseUrl}/idle/query",
        _ => throw Unsupported(tier),
    };

    private static AsrConfigurationException Unsupported(string tier) =>
        new(
            $"asr service tier '{tier}' is not supported. This milestone implements the '{Standard}' " +
            $"and '{Idle}' submit/query tiers. The '{Turbo}' (flash, single-shot) endpoint is a different " +
            $"protocol and is not implemented; set asr.service_tier to '{Standard}' or '{Idle}'.");
}

/// <summary>
/// Builds the Volcengine adapter from the effective configuration.
/// </summary>
/// <remarks>
/// Construction is where missing or invalid credentials surface. Callers build the
/// provider before creating any session or session artifact, so a misconfiguration
/// fails visibly and leaves no half-written session behind
/// (<c>docs/DEVELOPMENT.md</c> section 7).
/// </remarks>
public static class VolcengineAsrProviderFactory
{
    public const string ProviderName = "volcengine";

    public static void EnsureTierSupported(string tier)
    {
        if (!VolcengineTiers.IsSupported(tier))
        {
            // Throws with the same actionable message the provider would produce.
            _ = VolcengineTiers.SubmitPath(VolcengineAsrOptions.DefaultBaseUrl, tier);
        }
    }

    public static VolcengineAsrProvider Create(
        MeetCapConfiguration configuration,
        HttpMessageHandler? handler = null,
        Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = BuildOptions(configuration, environment);
        return new VolcengineAsrProvider(options, handler);
    }

    public static VolcengineAsrOptions BuildOptions(
        MeetCapConfiguration configuration,
        Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!configuration.Asr.Enabled)
        {
            throw new AsrConfigurationException(
                "asr.enabled is false, so file ASR cannot run. Enable it (or set asr.enabled = true) " +
                "before importing a recording.");
        }

        var volcengine = configuration.Asr.Volcengine;
        if (string.IsNullOrWhiteSpace(volcengine.AppId))
        {
            throw new AsrConfigurationException(
                "asr.volcengine.app_id is empty. Set it to the application id issued by the Volcengine " +
                "speech console.");
        }

        string accessToken;
        try
        {
            accessToken = CredentialResolver.Resolve(
                volcengine.Credential,
                environment ?? Environment.GetEnvironmentVariable);
        }
        catch (CredentialResolutionException ex)
        {
            throw new AsrConfigurationException(ex.Message);
        }

        if (string.IsNullOrWhiteSpace(volcengine.ResourceId))
        {
            throw new AsrConfigurationException(
                "asr.volcengine.resource_id is empty. Set it to the resource id for the selected service " +
                "tier (for example 'volc.bigasr.auc').");
        }

        return new VolcengineAsrOptions
        {
            AppId = volcengine.AppId.Trim(),
            AccessToken = accessToken,
            ResourceId = volcengine.ResourceId.Trim(),
            HotwordTableId = volcengine.HotwordTableId,
            HttpTimeout = TimeSpan.FromSeconds(volcengine.HttpTimeoutSeconds),
        };
    }
}
