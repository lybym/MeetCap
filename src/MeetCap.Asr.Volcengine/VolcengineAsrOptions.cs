namespace MeetCap.Asr.Volcengine;

using MeetCap.Core.Asr;
using MeetCap.Core.Configuration;
using MeetCap.Core.Secrets;

/// <summary>
/// Adapter-owned provider configuration. Every provider-specific value -- endpoint
/// root, resource id, protocol limits, transient-retry budget -- lives here and never
/// reaches domain code (<c>docs/ARCHITECTURE.md</c> section 11).
/// </summary>
/// <remarks>
/// The wire contract is deliberately fixed: one model generation (Seed-ASR 2.0), one
/// authentication scheme (the new-console <c>X-Api-Key</c>), one file-ASR service
/// profile (recording-file Standard), and one resource id
/// (<see cref="ResourceId"/>). None of those is a user-tunable setting, so there is no
/// compatibility switch to get wrong (<c>docs/ASR_STRATEGY.md</c> sections 2 and 5).
/// </remarks>
public sealed record VolcengineAsrOptions
{
    /// <summary>
    /// Provider endpoint root for the recording-file Standard HTTP interface. Overridable
    /// so tests never touch the real service.
    /// </summary>
    public const string DefaultBaseUrl = "https://openspeech.bytedance.com/api/v3/auc/bigmodel";

    /// <summary>
    /// Fixed <c>X-Api-Resource-Id</c> for Seed-ASR 2.0 recording-file Standard.
    /// Provider protocol, not a user setting.
    /// </summary>
    public const string ResourceId = "volc.seedasr.auc";

    /// <summary>Fixed <c>request.model_name</c> for the Seed-ASR 2.0 endpoint family.</summary>
    public const string ModelName = "bigmodel";

    /// <summary>
    /// Non-secret MeetCap-owned value sent as <c>user.uid</c>.
    /// </summary>
    /// <remarks>
    /// The API key must never be copied here: <c>request.json</c> is persisted locally, so a
    /// secret in the body would end up in a session artifact
    /// (<c>docs/CONFIGURATION.md</c> section 8).
    /// </remarks>
    public const string UserId = "meetcap";

    /// <summary>Submit path under <see cref="BaseUrl"/>.</summary>
    public const string SubmitPath = "/submit";

    /// <summary>Result-query path under <see cref="BaseUrl"/>.</summary>
    public const string QueryPath = "/query";

    /// <summary>
    /// New-console API key, sent as <c>X-Api-Key</c>. Never logged, never persisted,
    /// never written to artifacts.
    /// </summary>
    public required string ApiKey { get; init; }

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
    public long MaxInlineAudioBytes { get; init; } = 20L * 1024 * 1024;

    /// <summary>The one submit endpoint this adapter speaks.</summary>
    public string SubmitEndpoint => BaseUrl + SubmitPath;

    /// <summary>The one query endpoint this adapter speaks.</summary>
    public string QueryEndpoint => BaseUrl + QueryPath;
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

    public static VolcengineAsrProvider Create(
        MeetCapConfiguration configuration,
        HttpMessageHandler? handler = null,
        Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = BuildOptions(configuration, environment);
        return new VolcengineAsrProvider(options, handler, BuildAudioPublisher(configuration, environment));
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
        string apiKey;
        try
        {
            apiKey = CredentialResolver.Resolve(
                volcengine.ApiKey,
                environment ?? Environment.GetEnvironmentVariable);
        }
        catch (CredentialResolutionException ex)
        {
            throw new AsrConfigurationException(ex.Message);
        }

        return new VolcengineAsrOptions
        {
            ApiKey = apiKey,
            HotwordTableId = volcengine.HotwordTableId,
            HttpTimeout = TimeSpan.FromSeconds(volcengine.HttpTimeoutSeconds),
        };
    }

    private static IAsrAudioPublisher BuildAudioPublisher(MeetCapConfiguration configuration, Func<string, string?>? environment)
    {
        var tos = configuration.Asr.Tos;
        if (string.IsNullOrWhiteSpace(tos.Bucket) && string.IsNullOrWhiteSpace(tos.Region) && string.IsNullOrWhiteSpace(tos.Endpoint)
            && string.IsNullOrWhiteSpace(tos.AccessKey) && string.IsNullOrWhiteSpace(tos.SecretKey))
            return new VolcengineTosAudioPublisher(null);
        try
        {
            var env = environment ?? Environment.GetEnvironmentVariable;
            return new VolcengineTosAudioPublisher(new VolcengineTosOptions(
                Required("asr.tos.bucket", tos.Bucket), Required("asr.tos.region", tos.Region), Required("asr.tos.endpoint", tos.Endpoint),
                CredentialResolver.Resolve(tos.AccessKey, env), CredentialResolver.Resolve(tos.SecretKey, env)));
        }
        catch (CredentialResolutionException ex) { throw new AsrConfigurationException(ex.Message); }
    }

    private static string Required(string name, string value) => string.IsNullOrWhiteSpace(value)
        ? throw new AsrConfigurationException($"{name} is required when [asr.tos] is configured.") : value;
}
