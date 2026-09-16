namespace MeetCap.Cli.Commands;

using MeetCap.Asr;
using MeetCap.Asr.Importing;
using MeetCap.Asr.Transcripts;
using MeetCap.Asr.Volcengine;
using MeetCap.AudioPipeline;
using MeetCap.Core.Asr;
using MeetCap.Core.Configuration;
using MeetCap.Core.Media;
using MeetCap.Core.Sessions;
using MeetCap.Core.Transcripts;
using MeetCap.Persistence.Storage;

/// <summary>
/// The composition root for ASR-bearing commands (<c>meetcap import</c> and
/// <c>meetcap asr resume</c>). Infrastructure is constructed here and nowhere else,
/// so the domain layers never see Volcengine, FFMpegCore, Polly, or SQLite types
/// (<c>docs/ARCHITECTURE.md</c> section 3).
/// </summary>
internal sealed class AsrStack : ILiveAsrHost, IDisposable
{
    private AsrStack(
        MeetCapDatabase database,
        ISessionStore sessions,
        IAsrJobStore jobs,
        ISessionArtifactWriter artifacts,
        ITranscriptStore transcripts,
        AsrJobProcessor processor,
        VolcengineAsrProvider provider,
        IMediaPipeline? media)
    {
        Database = database;
        Sessions = sessions;
        Jobs = jobs;
        Artifacts = artifacts;
        Transcripts = transcripts;
        Processor = processor;
        Provider = provider;
        Media = media;
    }

    public MeetCapDatabase Database { get; }

    public ISessionStore Sessions { get; }

    public IAsrJobStore Jobs { get; }

    public ISessionArtifactWriter Artifacts { get; }

    public ITranscriptStore Transcripts { get; }

    public AsrJobProcessor Processor { get; }

    public VolcengineAsrProvider Provider { get; }

    /// <summary>Media pipeline, or null for commands that do not inspect media.</summary>
    public IMediaPipeline? Media { get; }

    /// <summary>
    /// Builds the stack. Provider, credential, tier, and toolchain problems are all
    /// reported here -- before any session exists -- so a misconfiguration cannot
    /// corrupt session state.
    /// </summary>
    /// <param name="effectiveTier">
    /// The tier this invocation will actually use (a one-shot override wins over
    /// configuration), so an unsupported tier is rejected before any media work.
    /// </param>
    /// <param name="httpHandler">
    /// Optional transport for the provider adapter, supplied only by a test harness
    /// (<c>docs/DEVELOPMENT.md</c> section 7). Production passes <c>null</c>.
    /// </param>
    public static bool TryCreate(
        CliContext context,
        MeetCapConfiguration configuration,
        string dataRoot,
        string effectiveTier,
        bool requireMedia,
        out AsrStack? stack,
        out int exitCode,
        HttpMessageHandler? httpHandler = null)
    {
        stack = null;
        exitCode = 1;

        VolcengineAsrProvider? provider = null;
        try
        {
            VolcengineAsrProviderFactory.EnsureTierSupported(effectiveTier);
            provider = VolcengineAsrProviderFactory.Create(configuration, httpHandler);
        }
        catch (AsrConfigurationException ex)
        {
            context.Error.WriteLine($"meetcap: {ex.Message}");
            return false;
        }

        IMediaPipeline? media = null;
        if (requireMedia)
        {
            try
            {
                media = FFmpegMediaPipeline.Create(
                    configuration.Media.FfmpegBinaryFolder,
                    configuration.Media.FfmpegTemporaryFolder);
            }
            catch (MediaToolingException ex)
            {
                provider.Dispose();
                context.Error.WriteLine($"meetcap: {ex.Message}");
                return false;
            }
        }

        var database = new MeetCapDatabase(CommandSupport.DatabasePath(dataRoot));
        database.EnsureMigrated();

        var transcripts = new FileTranscriptStore();
        var artifacts = new FileSessionArtifactWriter();
        var processorOptions = new AsrJobProcessorOptions
        {
            DataRoot = dataRoot,
            RetryPolicy = new AsrRetryPolicy(
                configuration.Asr.RetryMaxAttempts,
                configuration.Asr.RetryInitialSeconds,
                configuration.Asr.RetryMaxSeconds),
            PollInterval = TimeSpan.FromSeconds(configuration.Asr.Volcengine.PollIntervalSeconds),
            PollTimeout = TimeSpan.FromSeconds(configuration.Asr.Volcengine.PollTimeoutSeconds),
            CostPerHourCny = configuration.Asr.Volcengine.CostPerHourCny,
            WriteMarkdown = configuration.Transcript.LiveMarkdown,
            TranscriptOptions = new TranscriptRenderOptions(
                configuration.Transcript.IncludeSource,
                configuration.Transcript.IncludeTimestamps,
                configuration.Transcript.IncludeSpeakerLabels),
        };

        var processor = new AsrJobProcessor(
            database.AsrJobs,
            provider,
            new VolcengineResponseNormalizer(),
            transcripts,
            database.Sessions,
            artifacts,
            processorOptions);

        stack = new AsrStack(
            database,
            database.Sessions,
            database.AsrJobs,
            artifacts,
            transcripts,
            processor,
            provider,
            media);

        exitCode = 0;
        return true;
    }

    public void Dispose() => Provider.Dispose();
}
