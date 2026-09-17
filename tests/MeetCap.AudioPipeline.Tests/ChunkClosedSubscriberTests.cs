using System.Text.Json;
using MeetCap.AudioPipeline.Tests.TestSupport;
using MeetCap.Core.Capture;
using MeetCap.Core.Sessions;
using Xunit;

namespace MeetCap.AudioPipeline.Tests;

/// <summary>
/// Step 05 regression coverage for the closed-chunk hand-off seam: a subscriber that fails
/// must never be treated as a failure of the recording
/// (<c>docs/ARCHITECTURE.md</c> section 9.3, <c>docs/RELIABILITY.md</c> sections 1 and 2).
/// </summary>
/// <remarks>
/// The batch builder is one subscriber and it does real file work, so the way its failure
/// travels out of the recording is a durability property, not an implementation detail: a
/// misattributed failure marks a healthy session <c>INTERRUPTED</c> and makes
/// <c>meetcap start</c> exit non-zero.
/// </remarks>
public class ChunkClosedSubscriberTests
{
    private static readonly AudioFormat Format = TestAudio.Formats.Mono48kPcm;

    [Fact]
    public async Task RunAsync_AThrowingSubscriberCannotFailTheRecording()
    {
        using var harness = new SessionHarness(chunkSeconds: 1);
        var source = new FakeCaptureSource(Format, harness.Device);
        harness.Sources.Enqueue(source);

        var session = harness.Service.PrepareSession("Throwing Subscriber");
        var paths = new SessionPaths(harness.DataRoot, session.SessionId);

        var calls = 0;
        session.ChunkClosed += _ =>
        {
            calls++;
            throw new InvalidOperationException("scripted downstream failure");
        };

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);

        Assert.True(await Wait.UntilAsync(() => source.StartCount == 1), "capture did not start");
        TestAudio.EmitSeconds(source, Format, 0, milliseconds: 3_000);
        Assert.True(await Wait.UntilAsync(() => calls >= 3), "the subscriber was never called");
        cancellation.Cancel();

        var outcome = await Wait.ForAsync(run, 20_000, "the recording did not finish");

        // The recording is healthy: a downstream consumer's failure is not a capture or
        // storage failure, and it must not reach the session state or the exit code.
        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.True(outcome.IsClean);
        Assert.False(outcome.Degraded);
        Assert.Equal(3, outcome.ChunksClosed);
        Assert.Empty(Directory.GetFiles(paths.AudioDirectory(AudioSource.Mic), "*.part"));

        var stored = harness.Database.Sessions.Find(session.SessionId)!;
        Assert.Equal(SessionStatus.Completed, stored.Status);
        Assert.NotNull(stored.StoppedAt);

        var manifest = JsonDocument.Parse(File.ReadAllText(paths.ManifestPath)).RootElement;
        Assert.False(manifest.GetProperty("degraded").GetBoolean());
        Assert.False(manifest.GetProperty("gaps_remain").GetBoolean());

        // The event log is held open for the whole session by design, so the owner releases the
        // session before reading it (docs/ARCHITECTURE.md section 9.3).
        session.Dispose();

        // The failure is still visible: one explicit capture.discontinuity per refused chunk,
        // and nothing that claims the audio was lost.
        var events = ReadEvents(paths);
        var discontinuities = events
            .Where(e => Name(e) == SessionEventNames.CaptureDiscontinuity)
            .ToArray();
        Assert.Equal(3, discontinuities.Length);
        Assert.All(discontinuities, e =>
            Assert.Contains("downstream consumer", e.GetProperty("detail").GetString()!, StringComparison.Ordinal));
        Assert.Equal(0, events.Count(e => Name(e) == SessionEventNames.CaptureGap));
        Assert.Equal(0, CountDiskEvents(paths));
    }

    [Fact]
    public async Task RunAsync_AThrowingSubscriberDoesNotStopLaterChunksFromBeingClosed()
    {
        using var harness = new SessionHarness(chunkSeconds: 1);
        var source = new FakeCaptureSource(Format, harness.Device);
        harness.Sources.Enqueue(source);

        var session = harness.Service.PrepareSession("Throwing Subscriber Recovery");
        var paths = new SessionPaths(harness.DataRoot, session.SessionId);

        var sequences = new List<int>();
        session.ChunkClosed += chunk =>
        {
            sequences.Add(chunk.Sequence);
            throw new InvalidOperationException("scripted downstream failure");
        };

        using var cancellation = new CancellationTokenSource();
        var run = session.RunAsync(cancellation.Token);

        Assert.True(await Wait.UntilAsync(() => source.StartCount == 1), "capture did not start");
        TestAudio.EmitSeconds(source, Format, 0, milliseconds: 3_000);
        Assert.True(await Wait.UntilAsync(() => sequences.Count >= 3), "the subscriber was never called");
        cancellation.Cancel();

        var outcome = await Wait.ForAsync(run, 20_000, "the recording did not finish");

        // Every chunk still reached the spool and the disk, in order, after earlier
        // announcements threw.
        Assert.Equal(SessionStatus.Completed, outcome.Status);
        Assert.Equal(new[] { 1, 2, 3 }, sequences);
        Assert.Equal(
            new[] { "000001.wav", "000002.wav", "000003.wav" },
            Directory.GetFiles(paths.AudioDirectory(AudioSource.Mic), "*.wav")
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());

        var chunks = harness.Database.Chunks.ListForSession(session.SessionId);
        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c => Assert.Equal(ChunkStates.Closed, c.Status));
    }
    private static string? Name(JsonElement element) =>
        element.TryGetProperty("event", out var value) ? value.GetString() : null;

    private static int CountDiskEvents(SessionPaths paths) =>
        ReadEvents(paths).Count(e =>
            Name(e) is SessionEventNames.StorageLowDiskSpace
                or SessionEventNames.StorageDiskExhausted
                or SessionEventNames.StorageProbeFailed);

    private static IReadOnlyList<JsonElement> ReadEvents(SessionPaths paths) =>
        File.ReadAllLines(paths.EventsPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();
}
