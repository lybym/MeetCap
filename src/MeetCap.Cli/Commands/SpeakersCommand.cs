namespace MeetCap.Cli.Commands;

using System.Buffers.Binary;
using System.Globalization;
using MeetCap.Core.Configuration;
using MeetCap.Core.Sessions;
using MeetCap.Core.Speakers;
using MeetCap.Core.Transcripts;
using MeetCap.Persistence.Storage;
using MeetCap.Speakers;
using MeetCap.Speakers.SherpaOnnx;

/// <summary>
/// Implements <c>meetcap speakers</c> subcommands
/// (<c>docs/ARCHITECTURE.md</c> section 17, <c>docs/ROADMAP.md</c> M6):
/// <list type="bullet">
/// <item><c>list</c> — list enrolled speakers.</item>
/// <item><c>enroll &lt;name&gt; --file &lt;path&gt;</c> — enroll a named speaker from a WAV file.</item>
/// <item><c>assign --session &lt;id&gt; --label &lt;speaker_N&gt; --name &lt;person&gt;</c> — manually bind a label to a person (locked).</item>
/// <item><c>attribute --session &lt;id&gt;</c> — run the attribution pipeline and write final.jsonl/final.md/attribution.json.</item>
/// </list>
/// </summary>
/// <remarks>
/// Parse/output only: enrollment, identification, and attribution all live behind the
/// domain interfaces this command composes. Every failure returns a non-zero exit code
/// with an actionable message and never touches the recording pipeline
/// (<c>docs/DEVELOPMENT.md</c> section 8).
/// </remarks>
internal static class SpeakersCommand
{
    // --- list ---

    public static int List(CliContext context)
    {
        if (!CommandSupport.TryLoadConfiguration(context, out var configuration, out var dataRoot, out var failure))
        {
            return failure;
        }

        var database = new MeetCapDatabase(CommandSupport.DatabasePath(dataRoot));
        database.EnsureMigrated();

        var speakers = database.Speakers.ListSpeakers();
        if (speakers.Count == 0)
        {
            context.Out.WriteLine("No speakers enrolled.");
            return 0;
        }

        context.Out.WriteLine($"{"ID",-16} {"Name",-24} {"Active",-8} {"Embeddings",-12}");
        context.Out.WriteLine(new string('-', 62));
        foreach (var speaker in speakers)
        {
            var count = database.Speakers.ListEmbeddings(speaker.Id).Count;
            context.Out.WriteLine(
                $"{speaker.Id,-16} {speaker.DisplayName,-24} {(speaker.Active ? "yes" : "no"),-8} {count,-12}");
        }

        return 0;
    }

    // --- enroll ---

    public static async Task<int> EnrollAsync(
        CliContext context,
        string name,
        string file,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            context.Error.WriteLine("meetcap speakers enroll: a speaker name is required.");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(file))
        {
            context.Error.WriteLine("meetcap speakers enroll: a --file path is required.");
            return 2;
        }

        var sourcePath = Path.GetFullPath(file);
        if (!File.Exists(sourcePath))
        {
            context.Error.WriteLine($"meetcap speakers enroll: audio file not found: {sourcePath}");
            return 1;
        }

        if (!CommandSupport.TryLoadConfiguration(context, out var configuration, out var dataRoot, out var failure))
        {
            return failure;
        }

        var database = new MeetCapDatabase(CommandSupport.DatabasePath(dataRoot));
        database.EnsureMigrated();

        ISpeakerIdentityProvider provider;
        try
        {
            provider = SherpaOnnxProviderFactory.Create(configuration, dataRoot);
        }
        catch (SpeakerProviderConfigurationException ex)
        {
            context.Error.WriteLine($"meetcap speakers enroll: {ex.Message}");
            return 1;
        }

        using var _ = provider as IDisposable;

        float[] samples;
        int sampleRate;
        try
        {
            (samples, sampleRate) = ReadWavSamples(sourcePath);
        }
        catch (Exception ex) when (ex is IOException or FormatException)
        {
            context.Error.WriteLine($"meetcap speakers enroll: could not read '{sourcePath}': {ex.Message}");
            return 1;
        }

        var registry = new SpeakerRegistry(database.Speakers, provider);
        var sample = new SpeakerAudioSample
        {
            Samples = samples,
            SampleRate = sampleRate,
            SpeakerLabel = null,
        };

        Speaker speaker;
        try
        {
            speaker = await registry.EnrollAsync(name, sample, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Error.WriteLine($"meetcap speakers enroll: identity provider failed: {ex.Message}");
            return 1;
        }

        var embeddingCount = database.Speakers.ListEmbeddings(speaker.Id).Count;
        context.Out.WriteLine("Speaker enrolled.");
        context.Out.WriteLine($"  id:         {speaker.Id}");
        context.Out.WriteLine($"  name:       {speaker.DisplayName}");
        context.Out.WriteLine($"  embeddings: {embeddingCount}");
        context.Out.WriteLine($"  source:     {sourcePath}");
        return 0;
    }

    // --- assign ---

    public static int Assign(
        CliContext context,
        string sessionId,
        string speakerLabel,
        string speakerName)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            context.Error.WriteLine("meetcap speakers assign: --session is required.");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(speakerLabel))
        {
            context.Error.WriteLine("meetcap speakers assign: --label is required.");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(speakerName))
        {
            context.Error.WriteLine("meetcap speakers assign: --name is required.");
            return 2;
        }

        if (!CommandSupport.TryLoadConfiguration(context, out var configuration, out var dataRoot, out var failure))
        {
            return failure;
        }

        var database = new MeetCapDatabase(CommandSupport.DatabasePath(dataRoot));
        database.EnsureMigrated();

        var speaker = database.Speakers.ListSpeakers()
            .FirstOrDefault(s => string.Equals(s.DisplayName, speakerName, StringComparison.OrdinalIgnoreCase));
        if (speaker is null)
        {
            context.Error.WriteLine(
                $"meetcap speakers assign: no enrolled speaker named '{speakerName}'. " +
                "Run 'meetcap speakers enroll' first.");
            return 1;
        }

        var registry = new SpeakerRegistry(database.Speakers, new NullSpeakerIdentityProvider());
        var assignment = registry.AssignManually(sessionId, speakerLabel, speaker);

        context.Out.WriteLine("Speaker assigned.");
        context.Out.WriteLine($"  session: {assignment.SessionId}");
        context.Out.WriteLine($"  label:   {assignment.SpeakerLabel}");
        context.Out.WriteLine($"  speaker: {assignment.SpeakerName} ({assignment.SpeakerId})");
        context.Out.WriteLine($"  source:  {SpeakerAssignmentSources.ToWire(assignment.Source)}");
        context.Out.WriteLine($"  locked:  {(assignment.Locked ? "yes" : "no")}");
        return 0;
    }

    // --- attribute ---

    public static async Task<int> AttributeAsync(
        CliContext context,
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            context.Error.WriteLine("meetcap speakers attribute: --session is required.");
            return 2;
        }

        if (!CommandSupport.TryLoadConfiguration(context, out var configuration, out var dataRoot, out var failure))
        {
            return failure;
        }

        var database = new MeetCapDatabase(CommandSupport.DatabasePath(dataRoot));
        database.EnsureMigrated();

        var paths = new SessionArtifactPaths(dataRoot, sessionId);
        if (!File.Exists(paths.RawTranscriptJsonl))
        {
            context.Error.WriteLine(
                $"meetcap speakers attribute: no raw transcript at '{paths.RawTranscriptJsonl}'. " +
                "Run ASR first.");
            return 1;
        }

        var transcripts = new MeetCap.Asr.Transcripts.FileTranscriptStore();
        var segments = transcripts.ReadJsonl(paths.RawTranscriptJsonl);
        if (segments.Count == 0)
        {
            context.Error.WriteLine("meetcap speakers attribute: raw transcript is empty.");
            return 1;
        }

        // Build the identity provider if the model is available. If it is not, the
        // attribution pipeline still runs: manual assignments are applied and everything
        // else stays unknown (docs/ARCHITECTURE.md section 20: SPEAKER_PROVIDER_FAILED
        // is a degraded condition, not a fatal one).
        ISpeakerIdentityProvider? provider = null;
        try
        {
            provider = SherpaOnnxProviderFactory.Create(configuration, dataRoot);
        }
        catch (SpeakerProviderConfigurationException ex)
        {
            context.Error.WriteLine($"warning: {ex.Message}");
            context.Error.WriteLine("warning: speaker identity matching is disabled; only manual assignments will be applied.");
        }

        using var _ = provider as IDisposable;

        var policy = new SpeakerMatchPolicy(
            configuration.Speakers.Identity.MatchThreshold,
            configuration.Speakers.Identity.MatchMargin);

        var attributionService = new SpeakerAttributionService(
            database.Speakers,
            provider ?? new NullSpeakerIdentityProvider(),
            policy,
            configuration.Speakers.Identity.SampleMinSeconds,
            configuration.Speakers.Identity.SampleMaxSeconds);

        // Keep the sample on the transcript's source track. An ambiguous cross-track
        // label must not silently read another participant's audio.
        SpeakerSampleExtractor extractor = (label, startMs, endMs, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            var sources = segments.Where(s => s.SpeakerLabel == label && s.StartMs < endMs && s.EndMs > startMs)
                .Select(s => s.Source).Distinct(StringComparer.Ordinal).ToArray();
            return Task.FromResult(sources.Length == 1
                ? SessionSpeakerSampleReader.Extract(paths, sources[0], label, startMs, endMs) : null);
        };

        SpeakerAttributionResult result;
        try
        {
            result = await attributionService.BuildAsync(
                sessionId, segments, extractor, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Error.WriteLine($"meetcap speakers attribute: {ex.Message}");
            return 1;
        }

        // Write the attribution artifact.
        Directory.CreateDirectory(paths.SpeakersDirectory);
        File.WriteAllText(paths.SpeakerAttributionJson, SpeakerAttributionJson.ToJson(result.Artifact));

        // Write the final attributed transcripts (raw_text is never modified).
        var renderOptions = new TranscriptRenderOptions(
            configuration.Transcript.IncludeSource,
            configuration.Transcript.IncludeTimestamps,
            configuration.Transcript.IncludeSpeakerLabels);

        transcripts.WriteJsonl(paths.FinalTranscriptJsonl, result.AttributedSegments);
        if (configuration.Transcript.WriteMarkdown)
        {
            transcripts.WriteMarkdown(paths.FinalTranscriptMarkdown, sessionId, result.AttributedSegments, renderOptions);
        }

        context.Out.WriteLine("Speaker attribution complete.");
        context.Out.WriteLine($"  session:    {sessionId}");
        context.Out.WriteLine($"  artifact:   {paths.SpeakerAttributionJson}");
        context.Out.WriteLine($"  final:      {paths.FinalTranscriptJsonl}");
        if (configuration.Transcript.WriteMarkdown)
        {
            context.Out.WriteLine($"  markdown:   {paths.FinalTranscriptMarkdown}");
        }

        var named = result.Artifact.Entries.Count(e => e.SpeakerId is not null);
        var unknown = result.Artifact.Entries.Count - named;
        context.Out.WriteLine($"  resolved:   {named} named, {unknown} unknown");

        foreach (var warning in result.Warnings)
        {
            context.Error.WriteLine($"warning: {warning}");
        }

        return 0;
    }

    // --- minimal WAV reader ---

    private static (float[] Samples, int SampleRate) ReadWavSamples(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);

        // RIFF header
        var riff = reader.ReadBytes(4);
        if (riff.Length < 4 || riff[0] != 'R' || riff[1] != 'I' || riff[2] != 'F' || riff[3] != 'F')
        {
            throw new FormatException("Not a RIFF/WAVE file.");
        }

        reader.ReadUInt32(); // file size
        var wave = reader.ReadBytes(4);
        if (wave.Length < 4 || wave[0] != 'W' || wave[1] != 'A' || wave[2] != 'V' || wave[3] != 'E')
        {
            throw new FormatException("Not a WAVE file.");
        }

        int sampleRate = 0;
        short bitsPerSample = 0;
        short channels = 0;
        ushort audioFormat = 1; // PCM by default
        byte[]? data = null;

        while (stream.Position < stream.Length)
        {
            var chunkId = reader.ReadBytes(4);
            if (chunkId.Length < 4) break;

            var chunkSize = reader.ReadUInt32();
            var chunkName = System.Text.Encoding.ASCII.GetString(chunkId);

            if (chunkName == "fmt ")
            {
                audioFormat = reader.ReadUInt16();
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32(); // byte rate
                reader.ReadInt16(); // block align
                bitsPerSample = reader.ReadInt16();
                if (chunkSize > 16)
                {
                    reader.ReadBytes((int)chunkSize - 16);
                }
            }
            else if (chunkName == "data")
            {
                data = reader.ReadBytes((int)chunkSize);
            }
            else
            {
                // Skip unknown chunks (pad to even).
                reader.ReadBytes((int)(chunkSize + (chunkSize % 2)));
            }
        }

        if (data is null || data.Length == 0)
        {
            throw new FormatException("WAVE file has no data chunk.");
        }

        if (sampleRate == 0)
        {
            throw new FormatException("WAVE file has no fmt chunk.");
        }

        var samples = ConvertToFloat(data, bitsPerSample, audioFormat, channels);
        return (samples, sampleRate);
    }

    private static float[] ConvertToFloat(byte[] data, short bitsPerSample, ushort audioFormat, short channels)
    {
        // Convert to mono float[] at the source sample rate. The sherpa-onnx extractor
        // takes the sample rate, so resampling is handled by the runtime.
        var bytesPerSample = bitsPerSample / 8;
        if (bytesPerSample == 0) return [];

        var frameCount = data.Length / (bytesPerSample * channels);
        var result = new float[frameCount];

        for (var i = 0; i < frameCount; i++)
        {
            var frameOffset = i * bytesPerSample * channels;
            float sum = 0;
            for (var ch = 0; ch < channels; ch++)
            {
                var offset = frameOffset + ch * bytesPerSample;
                sum += bitsPerSample switch
                {
                    16 => (short)(data[offset] | (data[offset + 1] << 8)) / 32768f,
                    32 when audioFormat == 3 => // IEEE float 32-bit
                        BitConverter.ToSingle(data, offset),
                    32 => // 32-bit int
                        BitConverter.ToInt32(data, offset) / 2147483648f,
                    _ => 0,
                };
            }

            result[i] = sum / channels;
        }

        return result;
    }

    /// <summary>
    /// A no-op identity provider used when the real provider is unavailable (missing
    /// model). It returns empty candidates so all labels without manual assignments
    /// stay unknown, which is the correct degraded behavior
    /// (<c>docs/ARCHITECTURE.md</c> section 20).
    /// </summary>
    private sealed class NullSpeakerIdentityProvider : ISpeakerIdentityProvider
    {
        public string Name => "none";
        public string ModelName => "unavailable";
        public string ModelVersion => "0";
        public int EmbeddingDimension => 0;

        public Task<ExtractedEmbedding> ExtractAsync(SpeakerAudioSample sample, CancellationToken cancellationToken = default)
            => throw new SpeakerProviderConfigurationException(
                "No speaker identity provider is available. Set speakers.sherpa_onnx.model_path " +
                "to the 3D-Speaker ERes2Net-base model.");

        public Task<IReadOnlyList<SpeakerCandidate>> IdentifyAsync(
            ExtractedEmbedding probe,
            IReadOnlyList<EnrolledSpeaker> enrolled,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SpeakerCandidate>>([]);
    }
}
