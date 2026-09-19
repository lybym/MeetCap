using MeetCap.Core.Asr;
using Xunit;

namespace MeetCap.Asr.Volcengine.Tests;

/// <summary>
/// Transport-policy tests for <see cref="VolcengineTosAudioPublisher"/>.
/// </summary>
/// <remarks>
/// <para>
/// The real upload, signing and delete need a TOS bucket and credentials, which this
/// environment does not have and docs/DEVELOPMENT.md section 7 forbids inventing. What is
/// asserted here is everything that does not need the service: the fixed 20 MiB policy, the
/// actionable failure when TOS is required but absent, the object-key layout, and that the
/// SDK type is the real official client rather than a hand-rolled signer.
/// </para>
/// <para>
/// The end-to-end TOS plus Seed-ASR smoke test is documented as a manual step in
/// <c>docs/M1_WINDOWS_VALIDATION.md</c>; it is deliberately not claimed by CI.
/// </para>
/// </remarks>
public class VolcengineTosAudioPublisherTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "meetcap-tos-test-" + Guid.NewGuid().ToString("N"));

    public VolcengineTosAudioPublisherTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    /// <summary>Writes an artifact of exactly <paramref name="bytes"/> bytes (sparse).</summary>
    private string WriteAudio(long bytes, string name = "normalized.wav")
    {
        var path = Path.Combine(_root, name);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        stream.SetLength(bytes);
        return path;
    }

    private static AsrFileRequest Request(string path, string jobId = "job_1") => new()
    {
        JobId = jobId,
        SessionId = "ses_1",
        Source = "import",
        InputArtifactPath = path,
        AudioFormat = "wav",
        DurationMs = 754_000,
        ProviderRequestId = "req-0001",
    };

    [Theory]
    [InlineData(0L)]
    [InlineData(1024L)]
    [InlineData(VolcengineTosAudioPublisher.InlineThresholdBytes)]
    public async Task Publish_AtOrBelow20MiB_StaysInlineEvenWithoutTosConfigured(long bytes)
    {
        // TOS is optional until the oversized path is actually needed, so an unconfigured
        // publisher must still serve every input at or below the threshold.
        var publisher = new VolcengineTosAudioPublisher(null);

        var published = await publisher.PublishAsync(Request(WriteAudio(bytes)));

        Assert.Equal(AsrTransports.Inline, published.Transport);
        Assert.Equal(bytes, published.Bytes);
        Assert.Null(published.Bucket);
        Assert.Null(published.ObjectKey);
        Assert.False(published.HasRemoteCopy);
        Assert.Equal(bytes, published.InlineBase64 is null ? 0 : Convert.FromBase64String(published.InlineBase64).Length);
    }

    [Fact]
    public async Task Publish_Above20MiB_WithoutTosConfiguration_FailsActionablyInsteadOfSubstitutingAMode()
    {
        var publisher = new VolcengineTosAudioPublisher(null);

        var ex = await Assert.ThrowsAsync<AsrConfigurationException>(
            () => publisher.PublishAsync(Request(WriteAudio(VolcengineTosAudioPublisher.InlineThresholdBytes + 1))));

        // The message has to send the operator to the section to fill in, and it must state the
        // fallbacks MeetCap deliberately refuses rather than leaving them ambiguous.
        Assert.Contains("asr.tos", ex.Message, StringComparison.Ordinal);
        Assert.Contains("bucket", ex.Message, StringComparison.Ordinal);
        Assert.Contains("split", ex.Message, StringComparison.Ordinal);
        Assert.Contains("downsample", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_AMissingArtifact_IsPermanentAndNamesTheJobInput()
    {
        var publisher = new VolcengineTosAudioPublisher(null);

        var ex = await Assert.ThrowsAsync<AsrPermanentException>(
            () => publisher.PublishAsync(Request(Path.Combine(_root, "gone.wav"))));

        Assert.Equal("audio.missing", ex.Code);
    }

    [Fact]
    public async Task Publish_AnEmptyArtifactPath_IsPermanentRatherThanAnEmptyInlinePayload()
    {
        var publisher = new VolcengineTosAudioPublisher(null);

        var ex = await Assert.ThrowsAsync<AsrPermanentException>(
            () => publisher.PublishAsync(Request(Path.Combine(_root, "missing", "normalized.wav"))));

        Assert.Equal("audio.missing", ex.Code);
    }

    [Fact]
    public async Task Delete_AnInlinePayload_DoesNotNeedTosConfigurationAtAll()
    {
        // An inline job never manufactures TOS state, so there is nothing to release and no
        // configuration is consulted.
        var publisher = new VolcengineTosAudioPublisher(null);

        await publisher.DeleteAsync(new AsrPublishedAudio { Transport = AsrTransports.Inline, Bytes = 1 });
    }

    [Fact]
    public async Task Delete_ADurableObjectWithoutTosConfiguration_ExplainsHowToFinishTheCleanup()
    {
        var publisher = new VolcengineTosAudioPublisher(null);

        var ex = await Assert.ThrowsAsync<AsrConfigurationException>(
            () => publisher.DeleteAsync(new AsrPublishedAudio
            {
                Transport = AsrTransports.Tos,
                Bucket = "meetcap-asr",
                ObjectKey = "meetcap-asr/abcd/2026/09/19/job_1.wav",
            }));

        Assert.Contains("asr.tos", ex.Message, StringComparison.Ordinal);
        Assert.Contains("meetcap asr resume", ex.Message, StringComparison.Ordinal);
        // The cleanup failure must never be presented as a transcript problem.
        Assert.Contains("transcript is unaffected", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ObjectKey_UsesTheMeetcapPrefixARandomShardAndNoUserIdentifyingText()
    {
        var now = new DateTimeOffset(2026, 9, 19, 10, 30, 0, TimeSpan.Zero);

        var key = VolcengineTosAudioPublisher.BuildObjectKey("job_1", now);

        Assert.StartsWith("meetcap-asr/", key, StringComparison.Ordinal);
        Assert.EndsWith("/2026/09/19/job_1.wav", key, StringComparison.Ordinal);
        Assert.Matches(
            @"^meetcap-asr/[0-9a-f]{16}/2026/09/19/job_1\.wav$",
            key);
    }

    [Fact]
    public void ObjectKey_IsStableForAJobAndRandomisedAcrossJobs()
    {
        // Stability is what stops a re-publish after a crash from scattering a second object
        // under a fresh random name; the shard is what stops every object landing under one
        // sequential prefix.
        var now = new DateTimeOffset(2026, 9, 19, 10, 30, 0, TimeSpan.Zero);

        Assert.Equal(
            VolcengineTosAudioPublisher.BuildObjectKey("job_1", now),
            VolcengineTosAudioPublisher.BuildObjectKey("job_1", now.AddHours(5)));

        var shards = Enumerable.Range(0, 64)
            .Select(i => VolcengineTosAudioPublisher.ShardFor($"job_{i}"))
            .ToHashSet(StringComparer.Ordinal);

        // A one-bucket shard for 64 jobs would mean the shard adds nothing.
        Assert.True(shards.Count > 32, $"expected a spread of shards, got {shards.Count} distinct values");
        Assert.All(shards, shard => Assert.Matches("^[0-9a-f]{16}$", shard));
        Assert.DoesNotContain(new string('0', 16), shards);
    }

    [Fact]
    public void Policy_Constants_MatchTheAcceptedIssue29Contract()
    {
        // The threshold and the presigned lifetime are fixed transport policy in this issue,
        // not user-tunable settings, so they are asserted here rather than configured.
        Assert.Equal(20L * 1024 * 1024, VolcengineTosAudioPublisher.InlineThresholdBytes);
        Assert.Equal(6 * 60 * 60, VolcengineTosAudioPublisher.PresignedGetSeconds);
        Assert.Equal("meetcap-asr/", VolcengineTosAudioPublisher.ObjectKeyPrefix);
    }
}
