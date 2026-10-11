using MeetCap.Core.Asr;
using MeetCap.Core.Media;
using Xunit;

namespace MeetCap.Asr.Tests;

public sealed class NormalizingAsrProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meetcap-normalize-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private AsrFileRequest Request(string source = "mic")
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "native.wav");
        File.WriteAllText(path, "native audio remains authoritative");
        return new AsrFileRequest { JobId = "job_1", SessionId = "ses_1", Source = source,
            InputArtifactPath = path, AudioFormat = "wav", DurationMs = 300000,
            StartOffsetMs = 600000, ProviderRequestId = "fixed-request", RequestSpeakerInfo = true };
    }

    [Fact]
    public async Task LiveNormalization_PreservesOriginalAndTimeline_AndRebuildsInterruptedDerivative()
    {
        var request = Request();
        var inner = new Provider();
        var media = new Media();
        var provider = new NormalizingAsrProvider(inner, () => media, _root);
        await provider.SubmitFileAsync(request);
        var actual = Assert.IsType<AsrFileRequest>(inner.Request);
        Assert.NotEqual(request.InputArtifactPath, actual.InputArtifactPath);
        Assert.Contains(Path.Combine("asr", "jobs", "job_1"), actual.InputArtifactPath);
        Assert.Equal(request with { InputArtifactPath = actual.InputArtifactPath }, actual);
        Assert.Equal("native audio remains authoritative", File.ReadAllText(request.InputArtifactPath));
        Assert.False(File.Exists(actual.InputArtifactPath + ".part"));
        File.WriteAllText(actual.InputArtifactPath + ".part", "interrupted");
        await provider.SubmitFileAsync(request);
        Assert.Equal(2, media.Normalizations);
        Assert.Equal("derived PCM", File.ReadAllText(actual.InputArtifactPath));
    }

    [Fact]
    public async Task ImportAndPolling_DoNotRequireMediaTools()
    {
        var request = Request("import");
        var inner = new Provider();
        var provider = new NormalizingAsrProvider(inner, () => throw new Exception("must not locate tools"), _root);
        var submission = await provider.SubmitFileAsync(request);
        Assert.Equal(request, inner.Request);
        await provider.GetResultAsync(submission, request);
        Assert.Equal(1, inner.Polls);
    }

    [Fact]
    public async Task BadDerivative_IsRetryable_AndNeverSubmitted()
    {
        var request = Request();
        var inner = new Provider();
        var media = new Media { WrongDuration = true };
        var provider = new NormalizingAsrProvider(inner, () => media, _root);
        var ex = await Assert.ThrowsAsync<AsrTransientException>(() => provider.SubmitFileAsync(request));
        Assert.Equal("audio.normalization", ex.Code);
        Assert.Null(inner.Request);
        Assert.Equal("native audio remains authoritative", File.ReadAllText(request.InputArtifactPath));
    }

    [Fact]
    public async Task AlreadyNormalizedLiveAudio_IsNotReencoded()
    {
        var request = Request();
        var inner = new Provider();
        var media = new Media { AlreadyNormalized = true };
        await new NormalizingAsrProvider(inner, () => media, _root).SubmitFileAsync(request);
        Assert.Equal(request, inner.Request);
        Assert.Equal(0, media.Normalizations);
    }

    private sealed class Media : IMediaPipeline
    {
        public int Normalizations;
        public bool WrongDuration;
        public bool AlreadyNormalized;
        public MediaInfo Inspect(string path)
        {
            var normalized = AlreadyNormalized || path.EndsWith(".part", StringComparison.Ordinal);
            return new MediaInfo { Path = path, FormatName = "wav", DurationMs = WrongDuration && normalized ? 1 : 300000,
                ByteLength = new FileInfo(path).Length, AudioCodec = normalized ? "pcm_s16le" : "pcm_f32le",
                SampleRateHz = normalized ? 16000 : 48000, Channels = normalized ? 1 : 2, BitDepth = normalized ? 16 : 32 };
        }
        public Task NormalizeAsync(string inputPath, string outputPath, MediaNormalizationPlan plan, CancellationToken ct = default)
        {
            Normalizations++;
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(outputPath, "derived PCM");
            return Task.CompletedTask;
        }
    }
    private sealed class Provider : IAsrProvider
    {
        public string Name => "test";
        public AsrFileRequest? Request;
        public int Polls;
        public Task<AsrSubmission> SubmitFileAsync(AsrFileRequest request, CancellationToken ct = default)
        {
            Request = request;
            return Task.FromResult(new AsrSubmission { ProviderRequestId = request.ProviderRequestId, SanitizedRequestJson = "{}" });
        }
        public Task<AsrPollResult> GetResultAsync(AsrSubmission submission, AsrFileRequest request, CancellationToken ct = default)
        { Polls++; return Task.FromResult(AsrPollResult.Pending()); }
    }
}
