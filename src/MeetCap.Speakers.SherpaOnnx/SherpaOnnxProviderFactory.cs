namespace MeetCap.Speakers.SherpaOnnx;

using System.Runtime.Versioning;
using MeetCap.Core.Configuration;
using MeetCap.Core.Speakers;

/// <summary>
/// Builds the <see cref="SherpaOnnxSpeakerIdentityProvider"/> from configuration
/// (<c>docs/CONFIGURATION.md</c> section 9.3). This is the composition point where
/// the model file is resolved and the native runtime is loaded. It lives in the CLI
/// composition root, never in domain code (<c>docs/ARCHITECTURE.md</c> section 3).
/// </summary>
[SupportedOSPlatform("windows")]
public static class SherpaOnnxProviderFactory
{
    /// <summary>
    /// The sherpa-onnx package version recorded as the embedding model version, so
    /// embeddings from different runtime versions can be audited.
    /// </summary>
    public const string RuntimeVersion = "1.13.8";

    /// <summary>
    /// Creates the provider from configuration, or throws
    /// <see cref="SpeakerProviderConfigurationException"/> when the model is missing
    /// so the caller can fail visibly before touching session state.
    /// </summary>
    /// <param name="configuration">The effective runtime configuration.</param>
    /// <param name="dataRoot">The data root, used to resolve an unconfigured model_path.</param>
    public static SherpaOnnxSpeakerIdentityProvider Create(
        MeetCapConfiguration configuration,
        string dataRoot)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        var sherpa = configuration.Speakers.SherpaOnnx;
        var modelPath = ResolveModelPath(sherpa.ModelPath, sherpa.Model, dataRoot);
        var modelName = Path.GetFileNameWithoutExtension(sherpa.Model);

        return new SherpaOnnxSpeakerIdentityProvider(
            modelPath,
            modelName,
            RuntimeVersion);
    }

    /// <summary>
    /// Resolves the model file path. An explicit <c>model_path</c> wins; otherwise the
    /// model is resolved from <c>&lt;dataRoot&gt;/models/&lt;model&gt;</c>
    /// (<c>docs/CONFIGURATION.md</c> section 9.3: "empty means the model is resolved
    /// from the configured data root").
    /// </summary>
    private static string ResolveModelPath(string configuredPath, string modelName, string dataRoot)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Environment.ExpandEnvironmentVariables(configuredPath);
        }

        return Path.Combine(dataRoot, "models", modelName);
    }
}
