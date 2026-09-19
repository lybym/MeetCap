using System.Globalization;
using System.Reflection;
using MeetCap.Core.Asr;
using TOS;
using TOS.Model;
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
/// <para>
/// The oversized path itself is covered through the test-only client seam on the publisher
/// (<see cref="VolcengineTosAudioPublisher(VolcengineTosOptions?, Func{VolcengineTosOptions, ITosClient}?)"/>)
/// and the recording <see cref="FakeTosClient"/> below. That seam does not fake the upload:
/// it records exactly which official SDK calls the publisher makes and with what arguments, so
/// issue #29 acceptance criterion 4 — "the artifact is uploaded as a stream in a single
/// request, not buffered into memory and not split into multipart parts" — is asserted rather
/// than assumed. No test here touches the network; the fake answers every call locally.
/// </para>
/// </remarks>
public class VolcengineTosAudioPublisherTests : IDisposable
{
    private const string TestBucket = "meetcap-asr";
    private const string TestJobId = "job_oversized_1";

    /// <summary>
    /// The value <see cref="FakeTosClient.PreSignedURL"/> returns. It is obviously not a real
    /// signed URL, which is the point: any test that sees it in
    /// <see cref="AsrPublishedAudio.Url"/> has proven the URL came back from the SDK call
    /// instead of being synthesised by this adapter.
    /// </summary>
    private const string FakeSignedUrl =
        "https://tos-sentinel.invalid/meetcap-asr/fake-object?X-Tos-Signature=sentinel";

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

    /// <summary>
    /// Configuration with deliberately fake credentials. A test that accidentally let a real
    /// client be built from these would fail to reach TOS rather than reach the wrong bucket,
    /// and the seam means no client is built from them at all.
    /// </summary>
    private static VolcengineTosOptions TosOptions() => new(
        Bucket: TestBucket,
        Region: "cn-beijing",
        Endpoint: "tos-cn-beijing.volces.com",
        AccessKey: "AK-TEST",
        SecretKey: "SK-TEST");

    /// <summary>
    /// Builds a publisher whose TOS client is the recording fake, and hands the recorder back.
    /// </summary>
    private static VolcengineTosAudioPublisher RecordingPublisher(
        FakeTosClient recorder,
        VolcengineTosOptions? options = null)
    {
        // The factory is the whole point of the seam: a test can assert what the publisher asked
        // the SDK to do without a bucket, credentials, or network access.
        return new VolcengineTosAudioPublisher(options ?? TosOptions(), _ => recorder);
    }

    /// <summary>
    /// Asserts the computed key has the accepted layout
    /// <c>meetcap-asr/&lt;16 hex&gt;/&lt;yyyy&gt;/&lt;MM&gt;/&lt;dd&gt;/&lt;job&gt;.wav</c>, and that the
    /// date it carries is the UTC date the publish actually happened in.
    /// </summary>
    /// <remarks>
    /// The key embeds <c>DateTime.UtcNow</c>, so the shard and the four date components cannot be
    /// compared against a literal. They are still asserted, not skipped: the shard is checked to
    /// be 16 lowercase hex digits, and the date is re-parsed and required to fall inside the
    /// window spanned by the test. Skipping the date entirely would leave the layout half-proven.
    /// </remarks>
    private static void AssertObjectKeyShape(
        string key,
        string jobId,
        DateTimeOffset earliestUtc,
        DateTimeOffset latestUtc)
    {
        Assert.StartsWith(VolcengineTosAudioPublisher.ObjectKeyPrefix, key, StringComparison.Ordinal);
        Assert.EndsWith($"/{jobId}.wav", key, StringComparison.Ordinal);

        var segments = key.Split('/');
        Assert.Equal(6, segments.Length);
        Assert.Equal("meetcap-asr", segments[0]);
        Assert.Matches("^[0-9a-f]{16}$", segments[1]);
        Assert.False(
            string.Equals(segments[1], "0000000000000000", StringComparison.Ordinal),
            "the shard must never be all zeroes");

        var dateSegment = $"{segments[2]}-{segments[3]}-{segments[4]}";
        var parsed = DateTime.SpecifyKind(
            DateTime.ParseExact(dateSegment, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTimeKind.Utc);

        Assert.InRange(parsed, earliestUtc.UtcDateTime.Date, latestUtc.UtcDateTime.Date);
    }

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

    [Fact]
    public async Task Publish_Above20MiB_UploadsTheFileAsAStreamInExactlyOneRequest()
    {
        // Issue #29 acceptance criterion 4: the oversized artifact travels as a stream in a
        // single request. Handing the SDK a byte[] (or a MemoryStream over one) would pull tens
        // of megabytes into the managed heap for every oversized job, which is exactly the
        // memory pressure the object-storage path exists to avoid.
        var bytes = VolcengineTosAudioPublisher.InlineThresholdBytes + 1;
        var path = WriteAudio(bytes);
        var before = DateTimeOffset.UtcNow;
        var recorder = new FakeTosClient();
        var publisher = RecordingPublisher(recorder);

        var published = await publisher.PublishAsync(Request(path, TestJobId));

        var after = DateTimeOffset.UtcNow;

        // Exactly one simple upload: no retry loop, no second copy, and no multipart part upload
        // (the fake throws NotSupportedException for every multipart member, so reaching one would
        // have surfaced as a failure rather than a silent pass).
        var upload = Assert.Single(recorder.PutObjects);

        // The crux of this test. PublishAsync owns the FileStream and disposes it when it returns,
        // so the boolean has to have been captured inside the fake at call time: a later
        // `upload.Content is Stream` would still hold, but reading the stream would not.
        Assert.True(
            upload.ContentIsStream,
            $"the SDK was handed a {upload.ContentTypeName} instead of a Stream; a buffered byte array is not acceptable here");
        Assert.Equal(typeof(FileStream).FullName, upload.ContentTypeName);
        Assert.IsType<FileStream>(upload.Content);

        Assert.Equal(bytes, upload.ContentLength);
        Assert.Equal(TestBucket, upload.Bucket);
        Assert.Equal("audio/wav", upload.ContentType);
        Assert.NotNull(upload.Key);
        AssertObjectKeyShape(upload.Key, TestJobId, before, after);

        // The returned identity is the durable one, and the URL is the SDK's, not a synthesized
        // string: only the fake could have produced this sentinel.
        Assert.Equal(AsrTransports.Tos, published.Transport);
        Assert.Equal(FakeSignedUrl, published.Url);
        Assert.Equal(bytes, published.Bytes);
        Assert.Equal(TestBucket, published.Bucket);
        Assert.Equal(upload.Key, published.ObjectKey);
        Assert.True(published.HasRemoteCopy);
    }

    [Fact]
    public async Task Publish_Above20MiB_NeverUsesAMultipartUpload()
    {
        // Criterion 4 says one request, so multipart upload is out of scope for this path. The
        // recording fake proves it at the call site and the reflection scan below proves the
        // publisher has no multipart code path at all — an upload that is unreachable today but
        // present in the type would be a silent second implementation waiting to be switched on.
        var recorder = new FakeTosClient();
        var publisher = RecordingPublisher(recorder);

        var published = await publisher.PublishAsync(
            Request(
                WriteAudio(VolcengineTosAudioPublisher.InlineThresholdBytes + 1),
                TestJobId));

        Assert.Single(recorder.PutObjects);
        Assert.Empty(recorder.MultipartCalls);
        Assert.Equal(AsrTransports.Tos, published.Transport);

        // The artifact was streamed, never buffered, so no buffer type can appear either.
        Assert.DoesNotContain(
            "Multipart",
            Assert.Single(recorder.PutObjects).ContentTypeName,
            StringComparison.Ordinal);

        // "Multipart" is the TOS SDK's own naming for every part-based upload member, so a scan
        // for that token over the compiled members of the publisher is an honest proxy for "the
        // publisher never calls multipart": there is no SDK method the path could reach.
        var members = typeof(VolcengineTosAudioPublisher)
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Select(member => $"{member.MemberType} {member.Name}");

        Assert.DoesNotContain(members, member => member.Contains("Multipart", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Publish_Above20MiB_RequestsAPresignedGetWithTheFixedLifetime()
    {
        // Criterion 4 fixes the signed-URL lifetime at 6 hours and requires a GET. The URL is the
        // only capability the provider is given, so it must be read-only and must expire.
        var recorder = new FakeTosClient();
        var publisher = RecordingPublisher(recorder);

        var published = await publisher.PublishAsync(
            Request(
                WriteAudio(VolcengineTosAudioPublisher.InlineThresholdBytes + 1),
                TestJobId));

        var presign = Assert.Single(recorder.PreSignedUrls);

        Assert.Equal(TestBucket, presign.Bucket);
        Assert.Equal(HttpMethodType.HttpMethodGet, presign.HttpMethod);
        Assert.Equal(VolcengineTosAudioPublisher.PresignedGetSeconds, presign.Expires);

        // The URL has to sign the object that was actually uploaded: signing a different key
        // would hand the provider a URL that 404s only in production.
        Assert.Equal(Assert.Single(recorder.PutObjects).Key, presign.Key);
        Assert.Equal(presign.Key, published.ObjectKey);
        Assert.Equal(FakeSignedUrl, published.Url);
    }

    [Fact]
    public async Task Delete_AfterAnOversizedPublish_ReleasesTheSameStableObject()
    {
        // Cleanup uses the durable identity (bucket plus key) and never the signed URL: the URL is
        // ephemeral and is not persisted (docs/DATA_MODEL.md section 6.2), so delete has to work
        // from the identity alone.
        var recorder = new FakeTosClient();
        var publisher = RecordingPublisher(recorder);

        var published = await publisher.PublishAsync(
            Request(
                WriteAudio(VolcengineTosAudioPublisher.InlineThresholdBytes + 1),
                TestJobId));

        await publisher.DeleteAsync(published);

        var deleted = Assert.Single(recorder.DeletedObjects);
        Assert.Equal(TestBucket, deleted.Bucket);
        Assert.Equal(published.Bucket, deleted.Bucket);
        Assert.Equal(published.ObjectKey, deleted.Key);
        Assert.Equal(Assert.Single(recorder.PutObjects).Key, deleted.Key);

        // Nothing in the delete path needs the signed URL, and nothing re-uploads on cleanup.
        Assert.Single(recorder.PutObjects);
        Assert.Empty(recorder.MultipartCalls);
    }

    /// <summary>
    /// A recording in-memory implementation of the official <see cref="ITosClient"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the narrow boundary mock that docs/DEVELOPMENT.md section 7 permits: the TOS
    /// service cannot be reached from CI, so the boundary is substituted and the real-world
    /// round trip stays the documented manual validation in <c>docs/M1_WINDOWS_VALIDATION.md</c>.
    /// Nothing here is a fake success: the fake records the exact official SDK call the publisher
    /// made and its arguments, and it never pretends an upload happened against a real bucket.
    /// </para>
    /// <para>
    /// Everything the oversized path does not use throws <see cref="NotSupportedException"/>.
    /// That is deliberate: the multipart members throw, so a publisher that reached one would fail
    /// the test loudly instead of passing because the fake quietly accepted it.
    /// </para>
    /// <para>
    /// <see cref="PutObjectInput"/> is captured as the object the publisher passed, plus the two
    /// facts that cannot be read later: whether its <see cref="PutObjectInput.Content"/> was a
    /// <see cref="Stream"/> at call time, and the runtime type name. The publisher disposes the
    /// <see cref="FileStream"/> when <c>PublishAsync</c> returns, so a later inspection of
    /// <c>CanRead</c> would be false (or throw) even though the call was correct.
    /// </para>
    /// </remarks>
    private sealed class FakeTosClient : ITosClient
    {
        /// <summary>Every <see cref="PutObjectInput"/> passed to <see cref="PutObject"/>, in order.</summary>
        public List<PutObjectCall> PutObjects { get; } = [];

        /// <summary>Every <see cref="PreSignedURLInput"/> passed to <see cref="PreSignedURL"/>, in order.</summary>
        public List<PreSignedURLInput> PreSignedUrls { get; } = [];

        /// <summary>Every <see cref="DeleteObjectInput"/> passed to <see cref="DeleteObject"/>, in order.</summary>
        public List<DeleteObjectInput> DeletedObjects { get; } = [];

        /// <summary>
        /// Names of every multipart member that was reached. It stays empty because all of them
        /// throw, but the list makes "no multipart happened" directly assertable.
        /// </summary>
        public List<string> MultipartCalls { get; } = [];

        /// <summary>One recorded <see cref="PutObject"/> call.</summary>
        /// <param name="Input">The input object the publisher passed, kept for its identity fields.</param>
        /// <param name="Content">The exact <see cref="PutObjectInput.Content"/> reference, already disposed by the publisher by the time a test reads it.</param>
        /// <param name="ContentIsStream">Whether <c>Content</c> was a <see cref="Stream"/> at call time. The crux of acceptance criterion 4.</param>
        /// <param name="ContentTypeName">The runtime type name of <c>Content</c>, captured before disposal.</param>
        public sealed record PutObjectCall(
            PutObjectInput Input,
            object? Content,
            bool ContentIsStream,
            string? ContentTypeName)
        {
            public long ContentLength => Input.ContentLength;

            public string? Bucket => Input.Bucket;

            public string? Key => Input.Key;

            public string? ContentType => Input.ContentType;
        }

        public PutObjectOutput PutObject(PutObjectInput input)
        {
            ArgumentNullException.ThrowIfNull(input);

            // Record what was true at call time, then return. No bytes are read and no object is
            // created: the assertions are about the shape of the request the publisher issues.
            PutObjects.Add(new PutObjectCall(
                input,
                input.Content,
                input.Content is Stream,
                input.Content?.GetType().FullName));

            return new PutObjectOutput();
        }

        public PreSignedURLOutput PreSignedURL(PreSignedURLInput input)
        {
            ArgumentNullException.ThrowIfNull(input);
            PreSignedUrls.Add(input);

            // A distinct sentinel, not a real signature and not a URL MeetCap could have built
            // itself: a test that sees it in AsrPublishedAudio.Url has proven the value came from
            // the SDK boundary and was not synthesized locally.
            //
            // Volcengine.TOS.SDK 2.1.8 gives PreSignedURLOutput.SignedUrl an assembly-internal
            // setter, so even a fake outside the SDK cannot assign it as ordinary C#. Reflection is
            // the only way to populate the property from here — and that is itself the strongest
            // form of the claim under test: outside the SDK assembly the only other way a
            // PreSignedURLOutput ever carries a URL is as the return value of the SDK's own
            // presign, which is exactly what the publisher relies on.
            var output = new PreSignedURLOutput();
            typeof(PreSignedURLOutput)
                .GetProperty(nameof(PreSignedURLOutput.SignedUrl))!
                .SetValue(output, FakeSignedUrl);
            return output;
        }

        public DeleteObjectOutput DeleteObject(DeleteObjectInput input)
        {
            ArgumentNullException.ThrowIfNull(input);
            DeletedObjects.Add(input);
            return new DeleteObjectOutput();
        }

        public CreateMultipartUploadOutput CreateMultipartUpload(CreateMultipartUploadInput input) =>
            Multipart<CreateMultipartUploadOutput>(nameof(CreateMultipartUpload));

        public UploadPartOutput UploadPart(UploadPartInput input) =>
            Multipart<UploadPartOutput>(nameof(UploadPart));

        public UploadPartFromFileOutput UploadPartFromFile(UploadPartFromFileInput input) =>
            Multipart<UploadPartFromFileOutput>(nameof(UploadPartFromFile));

        public CompleteMultipartUploadOutput CompleteMultipartUpload(CompleteMultipartUploadInput input) =>
            Multipart<CompleteMultipartUploadOutput>(nameof(CompleteMultipartUpload));

        public AbortMultipartUploadOutput AbortMultipartUpload(AbortMultipartUploadInput input) =>
            Multipart<AbortMultipartUploadOutput>(nameof(AbortMultipartUpload));

        public UploadPartCopyOutput UploadPartCopy(UploadPartCopyInput input) =>
            Multipart<UploadPartCopyOutput>(nameof(UploadPartCopy));

        public ListMultipartUploadsOutput ListMultipartUploads(ListMultipartUploadsInput input) =>
            Multipart<ListMultipartUploadsOutput>(nameof(ListMultipartUploads));

        public ListPartsOutput ListParts(ListPartsInput input) =>
            Multipart<ListPartsOutput>(nameof(ListParts));

        public void Dispose()
        {
            // Nothing to release. The publisher owns the FileStream it opened, not the client.
        }

        public CreateBucketOutput CreateBucket(CreateBucketInput input) => NotUsed<CreateBucketOutput>(nameof(CreateBucket));

        public HeadBucketOutput HeadBucket(HeadBucketInput input) => NotUsed<HeadBucketOutput>(nameof(HeadBucket));

        public DeleteBucketOutput DeleteBucket(DeleteBucketInput input) => NotUsed<DeleteBucketOutput>(nameof(DeleteBucket));

        public ListBucketsOutput ListBuckets() => NotUsed<ListBucketsOutput>(nameof(ListBuckets));

        public ListBucketsOutput ListBuckets(ListBucketsInput input) => NotUsed<ListBucketsOutput>(nameof(ListBuckets));

        public CopyObjectOutput CopyObject(CopyObjectInput input) => NotUsed<CopyObjectOutput>(nameof(CopyObject));

        public DeleteMultiObjectsOutput DeleteMultiObjects(DeleteMultiObjectsInput input) => NotUsed<DeleteMultiObjectsOutput>(nameof(DeleteMultiObjects));

        public GetObjectOutput GetObject(GetObjectInput input) => NotUsed<GetObjectOutput>(nameof(GetObject));

        public GetObjectToFileOutput GetObjectToFile(GetObjectToFileInput input) => NotUsed<GetObjectToFileOutput>(nameof(GetObjectToFile));

        public GetObjectACLOutput GetObjectACL(GetObjectACLInput input) => NotUsed<GetObjectACLOutput>(nameof(GetObjectACL));

        public HeadObjectOutput HeadObject(HeadObjectInput input) => NotUsed<HeadObjectOutput>(nameof(HeadObject));

        public AppendObjectOutput AppendObject(AppendObjectInput input) => NotUsed<AppendObjectOutput>(nameof(AppendObject));

        public ListObjectsOutput ListObjects(ListObjectsInput input) => NotUsed<ListObjectsOutput>(nameof(ListObjects));

        public ListObjectVersionsOutput ListObjectVersions(ListObjectVersionsInput input) => NotUsed<ListObjectVersionsOutput>(nameof(ListObjectVersions));

        public RestoreObjectOutput RestoreObject(RestoreObjectInput input) => NotUsed<RestoreObjectOutput>(nameof(RestoreObject));

        public PutObjectFromFileOutput PutObjectFromFile(PutObjectFromFileInput input) => NotUsed<PutObjectFromFileOutput>(nameof(PutObjectFromFile));

        public PutObjectACLOutput PutObjectACL(PutObjectACLInput input) => NotUsed<PutObjectACLOutput>(nameof(PutObjectACL));

        public PutObjectTaggingOutput PutObjectTagging(PutObjectTaggingInput input) => NotUsed<PutObjectTaggingOutput>(nameof(PutObjectTagging));

        public GetObjectTaggingOutput GetObjectTagging(GetObjectTaggingInput input) => NotUsed<GetObjectTaggingOutput>(nameof(GetObjectTagging));

        public DeleteObjectTaggingOutput DeleteObjectTagging(DeleteObjectTaggingInput input) => NotUsed<DeleteObjectTaggingOutput>(nameof(DeleteObjectTagging));

        public SetObjectMetaOutput SetObjectMeta(SetObjectMetaInput input) => NotUsed<SetObjectMetaOutput>(nameof(SetObjectMeta));

        /// <summary>Records that a multipart member was reached, then fails the test loudly.</summary>
        private T Multipart<T>(string member)
        {
            MultipartCalls.Add(member);
            throw new NotSupportedException(
                $"VolcengineTosAudioPublisher called the multipart member '{member}'. Issue #29 " +
                "acceptance criterion 4 requires a single streaming PutObject request, so the " +
                "oversized path must never use a part-based upload.");
        }

        /// <summary>Fails loudly: the oversized path has no reason to touch this TOS member.</summary>
        private static T NotUsed<T>(string member) =>
            throw new NotSupportedException(
                $"VolcengineTosAudioPublisher called '{member}', which the TOS transport path is " +
                "not supposed to use. The recording fake only answers the calls the path needs.");
    }
}
