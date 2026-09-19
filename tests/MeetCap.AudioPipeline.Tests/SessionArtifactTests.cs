using System.Text.Json;
using MeetCap.AudioPipeline.Tests.TestSupport;
using MeetCap.Core.Capture;
using MeetCap.Core.Sessions;
using Xunit;

namespace MeetCap.AudioPipeline.Tests;

public class SessionArtifactTests : IDisposable
{
    private readonly string _directory;

    public SessionArtifactTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "meetcap-artifacts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public void Manifest_RoundTripsInTheDocumentedSnakeCaseShape()
    {
        var path = Path.Combine(_directory, "session.json");
        var manifest = new SessionManifest
        {
            SessionId = "ses_20260915T140000Z_00000001",
            Title = "Weekly Meeting",
            Mode = SessionModes.Offline,
            SourceType = SessionSourceTypes.Live,
            Status = SessionStatus.Completed,
            StartedAt = new DateTimeOffset(2026, 9, 15, 14, 0, 0, TimeSpan.Zero),
            StoppedAt = new DateTimeOffset(2026, 9, 15, 15, 0, 0, TimeSpan.Zero),
            ConfigVersion = 1,
            Tracks = new[] { AudioSources.Mic },
            ChunkSeconds = 60,
            Capture = new[]
            {
                new CaptureTrackInfo("mic", "mic-id", "USB Microphone", 48_000, 2, 32, AudioSampleFormatNames.IeeeFloat),
            },
        };

        SessionManifestStore.Save(path, manifest);

        Assert.True(SessionManifestStore.TryLoad(path, out var loaded, out var error), error);
        Assert.Equal(manifest.SessionId, loaded!.SessionId);
        Assert.Equal("Weekly Meeting", loaded.Title);
        Assert.Equal(SessionStatus.Completed, loaded.Status);
        Assert.Equal(60, loaded.ChunkSeconds);
        Assert.Equal(new[] { AudioSources.Mic }, loaded.Tracks);

        var track = Assert.Single(loaded.Capture);
        Assert.Equal("mic-id", track.DeviceId);
        Assert.Equal("ieee_float", track.SampleFormat);

        var json = File.ReadAllText(path);
        Assert.Contains("\"session_id\"", json, StringComparison.Ordinal);
        Assert.Contains("\"source_type\"", json, StringComparison.Ordinal);
        Assert.Contains("\"chunk_seconds\"", json, StringComparison.Ordinal);
        Assert.Contains("\"device_id\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_SaveLeavesNoTemporaryFileBehind()
    {
        var path = Path.Combine(_directory, "session.json");

        SessionManifestStore.Save(path, Minimal());
        SessionManifestStore.Save(path, Minimal());

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Manifest_TryLoad_ReportsAMissingFileWithoutThrowing()
    {
        Assert.False(SessionManifestStore.TryLoad(Path.Combine(_directory, "nope.json"), out var manifest, out var error));
        Assert.Null(manifest);
        Assert.Contains("missing", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Manifest_TryLoad_ReportsMalformedJsonWithoutThrowing()
    {
        var path = Path.Combine(_directory, "session.json");
        File.WriteAllText(path, "{ not json");

        Assert.False(SessionManifestStore.TryLoad(path, out _, out var error));
        Assert.Contains("valid JSON", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void EventSink_WritesOneDocumentedJsonObjectPerLine()
    {
        var path = Path.Combine(_directory, "events.jsonl");

        using (var sink = new JsonlSessionEventSink(path))
        {
            sink.Write(new SessionEvent(SessionEventNames.SessionStarted, 0) { Source = "mic" });
            sink.Write(new SessionEvent(SessionEventNames.ChunkClosed, 60_000)
            {
                Source = "mic",
                Chunk = "000001.wav",
                StartMs = 0,
                EndMs = 60_000,
            });
        }

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);

        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal("session.started", first.RootElement.GetProperty("event").GetString());
        Assert.Equal(0, first.RootElement.GetProperty("at_ms").GetInt64());
        Assert.Equal("mic", first.RootElement.GetProperty("source").GetString());

        // Fields an event does not define are omitted rather than written as null.
        Assert.False(first.RootElement.TryGetProperty("chunk", out _));

        using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal("audio.chunk.closed", second.RootElement.GetProperty("event").GetString());
        Assert.Equal("000001.wav", second.RootElement.GetProperty("chunk").GetString());
        Assert.Equal(60_000, second.RootElement.GetProperty("end_ms").GetInt64());
    }

    [Fact]
    public void EventSink_AppendsRatherThanTruncating()
    {
        var path = Path.Combine(_directory, "events.jsonl");

        using (var sink = new JsonlSessionEventSink(path))
        {
            sink.Write(new SessionEvent(SessionEventNames.SessionStarted, 0));
        }

        using (var sink = new JsonlSessionEventSink(path))
        {
            sink.Write(new SessionEvent(SessionEventNames.SessionStopped, 1_000));
        }

        Assert.Equal(2, File.ReadAllLines(path).Length);
    }

    [Fact]
    public void EventSink_StaysReadableWhileTheSessionIsStillRecording()
    {
        var path = Path.Combine(_directory, "events.jsonl");

        using (var sink = new JsonlSessionEventSink(path))
        {
            sink.Write(new SessionEvent(SessionEventNames.SessionStarted, 0));

            // A live session keeps the log open for the whole recording, and `meetcap
            // status` as well as the forced-kill checklist (docs/RELIABILITY.md section
            // 11) read it while that is happening. A reader must therefore be able to open
            // the still-open file at all, which is what the sink's share mode controls.
            // (The reader opts into write sharing too: a reader that denies writes would
            // conflict with the live appender.)
            Assert.Equal(
                SessionEventNames.SessionStarted,
                ReadFirstEventName(path));

            // The appender is still usable after the concurrent read.
            sink.Write(new SessionEvent(SessionEventNames.CaptureGap, 10) { GapMs = 5 });
        }

        // Both writes landed as whole lines.
        Assert.Equal(2, File.ReadAllLines(path).Length);
    }

    private static string? ReadFirstEventName(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var line = reader.ReadLine();
        Assert.NotNull(line);
        using var document = JsonDocument.Parse(line!);
        return document.RootElement.GetProperty("event").GetString();
    }

    [Fact]
    public async Task EventSink_SerializesConcurrentWritersIntoWholeLines()
    {
        var path = Path.Combine(_directory, "events.jsonl");

        using (var sink = new JsonlSessionEventSink(path))
        {
            await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
            {
                for (var i = 0; i < 50; i++)
                {
                    sink.Write(new SessionEvent(SessionEventNames.CaptureGap, worker * 100 + i) { Source = "mic" });
                }
            })));
        }

        var lines = File.ReadAllLines(path);
        Assert.Equal(400, lines.Length);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            Assert.Equal("capture.gap", document.RootElement.GetProperty("event").GetString());
        }
    }

    [Fact]
    public void InMemorySink_RecordsEventsInOrder()
    {
        var sink = new InMemorySessionEventSink();

        sink.Write(new SessionEvent(SessionEventNames.SessionStarted, 0));
        sink.Write(new SessionEvent(SessionEventNames.CaptureGap, 10) { GapMs = 5 });
        sink.Write(new SessionEvent(SessionEventNames.SessionStopped, 20));

        Assert.Equal(3, sink.Events.Count);
        Assert.Single(sink.WithName(SessionEventNames.CaptureGap));
    }

    [Fact]
    public void StopSignal_RequestIsIdempotentAndClearable()
    {
        var path = Path.Combine(_directory, "stop.request");
        var signal = new SessionStopSignal(path);

        Assert.False(signal.IsRequested());

        signal.Request("meetcap stop");
        signal.Request("meetcap stop");

        Assert.True(signal.IsRequested());
        Assert.Equal("meetcap stop", signal.ReadReason());
        Assert.False(File.Exists(path + ".tmp"));

        signal.Clear();
        Assert.False(signal.IsRequested());
    }

    [Fact]
    public void ConfigSnapshot_ContainsNoSecretMaterial()
    {
        var settings = new CaptureSettings(@"C:\data", 60, 5, 1_000, 5, "mic-1", 1);

        var json = CaptureConfigSnapshot.ToJson(settings);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(60, document.RootElement.GetProperty("chunk_seconds").GetInt32());
        Assert.Equal("mic-1", document.RootElement.GetProperty("microphone_device_id").GetString());

        // Issue #34: the device-recovery window is part of how a session behaved after a device
        // loss, so a session read later can state how long its tracks kept retrying before they
        // reported the endpoint unrecoverable (docs/CONFIGURATION.md section 12).
        Assert.Equal(20, document.RootElement.GetProperty("device_recovery_seconds").GetInt32());

        Assert.DoesNotContain("credential", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SessionPaths_FollowTheDocumentedLayout()
    {
        using var workspace = new TempWorkspace();
        var paths = workspace.Paths;

        Assert.Equal(Path.Combine(workspace.DataRoot, "sessions", workspace.SessionId), paths.SessionDirectory);
        Assert.Equal(Path.Combine(paths.SessionDirectory, "session.json"), paths.ManifestPath);
        Assert.Equal(Path.Combine(paths.SessionDirectory, "events.jsonl"), paths.EventsPath);
        Assert.Equal(Path.Combine(paths.SessionDirectory, "audio", "mic"), paths.AudioDirectory(AudioSource.Mic));
        Assert.Equal("audio/mic/000001.wav", paths.RelativeChunkPath(AudioSource.Mic, 1));
        Assert.EndsWith("000001.wav.part", paths.ChunkPartPath(AudioSource.Mic, 1), StringComparison.Ordinal);
    }

    [Fact]
    public void SessionPaths_EnumeratesSessionDirectoriesInOrder()
    {
        using var workspace = new TempWorkspace();
        new SessionPaths(workspace.DataRoot, "ses_20260915T130000Z_00000000").CreateDirectories();
        new SessionPaths(workspace.DataRoot, "ses_20260915T160000Z_00000002").CreateDirectories();

        var directories = SessionPaths.EnumerateSessionDirectories(workspace.DataRoot);

        Assert.Equal(3, directories.Count);
        Assert.Equal(
            directories.OrderBy(d => d, StringComparer.Ordinal),
            directories);
    }

    private static SessionManifest Minimal() => new()
    {
        SessionId = "ses_20260915T140000Z_00000001",
        Title = "T",
        Mode = SessionModes.Offline,
        SourceType = SessionSourceTypes.Live,
        Status = SessionStatus.Created,
        ConfigVersion = 1,
    };
}
