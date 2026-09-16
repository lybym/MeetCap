using System.Text.Json;
using MeetCap.Core.Sessions;
using MeetCap.Persistence.Storage;
using Xunit;

namespace MeetCap.Persistence.Tests.Storage;

/// <summary>
/// session.json is the durable half of the import source-artifact mapping, and
/// events.jsonl is append-only operational history (docs/DATA_MODEL.md sections 2-4).
/// </summary>
public class FileSessionArtifactWriterTests
{
    private static readonly DateTimeOffset s_now = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);

    private static SessionDocument Document(IReadOnlyList<SourceArtifact>? artifacts = null) => new()
    {
        SessionId = "ses_1",
        Title = "Weekly Meeting",
        Mode = SessionMode.Import,
        SourceType = SessionSourceType.Import,
        StartedAt = s_now,
        ConfigVersion = 1,
        Tracks = new[] { AudioTrackName.Import },
        SourceArtifacts = artifacts ?? Array.Empty<SourceArtifact>(),
    };

    [Fact]
    public void EnsureLayout_CreatesTheDocumentedDirectories()
    {
        using var workspace = new TempWorkspace();
        var paths = new SessionArtifactPaths(workspace.Root, "ses_1");

        new FileSessionArtifactWriter().EnsureLayout(paths);

        Assert.True(Directory.Exists(paths.SessionDirectory));
        Assert.True(Directory.Exists(paths.ImportAudioDirectory));
        Assert.True(Directory.Exists(paths.AsrJobsDirectory));
        Assert.True(Directory.Exists(paths.TranscriptDirectory));
        Assert.True(Directory.Exists(paths.LogsDirectory));
    }

    [Fact]
    public void EnsureLayout_MarksTheDataRootAsPrivateLocalData()
    {
        // The repository .gitignore no longer ignores a nested `sessions/` directory, so the
        // data root protects itself: recordings, transcripts, and voiceprints must never be
        // committable just because the data root sits inside a working tree.
        using var workspace = new TempWorkspace();
        var paths = new SessionArtifactPaths(workspace.Root, "ses_1");

        new FileSessionArtifactWriter().EnsureLayout(paths);

        var marker = Path.Combine(workspace.Root, DataRootMarker.MarkerFileName);
        Assert.True(File.Exists(marker));
        Assert.Contains("*", File.ReadAllText(marker), StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureLayout_SeedsTheDataRootForANestedSessionDirectory()
    {
        using var workspace = new TempWorkspace();
        var dataRoot = Path.Combine(workspace.Root, "nested", "data");

        new FileSessionArtifactWriter().EnsureLayout(new SessionArtifactPaths(dataRoot, "ses_1"));

        Assert.True(File.Exists(Path.Combine(dataRoot, DataRootMarker.MarkerFileName)));
    }

    [Fact]
    public void EnsureLayout_NeverOverwritesAnExistingGitIgnore()
    {
        // The repository root already has a real .gitignore; pointing --data-root at a checkout
        // must not clobber it.
        using var workspace = new TempWorkspace();
        var existing = Path.Combine(workspace.Root, DataRootMarker.MarkerFileName);
        File.WriteAllText(existing, "# keep me\n");

        new FileSessionArtifactWriter().EnsureLayout(new SessionArtifactPaths(workspace.Root, "ses_1"));

        Assert.Equal("# keep me\n", File.ReadAllText(existing));
    }

    [Fact]
    public void SessionDocument_RoundTripsIncludingTheSourceArtifactMapping()
    {
        using var workspace = new TempWorkspace();
        var paths = new SessionArtifactPaths(workspace.Root, "ses_1");
        var writer = new FileSessionArtifactWriter();

        var document = Document(new[]
        {
            new SourceArtifact
            {
                Role = SourceArtifact.Roles.Original,
                OriginalPath = @"C:\recordings\meeting.m4a",
                StoredPath = paths.ImportAudioFile("meeting.m4a"),
                FileName = "meeting.m4a",
                ByteLength = 1234,
                Sha256 = "abc123",
            },
            new SourceArtifact
            {
                Role = SourceArtifact.Roles.Normalized,
                OriginalPath = @"C:\recordings\meeting.m4a",
                StoredPath = paths.ImportAudioFile("normalized.wav"),
                FileName = "normalized.wav",
                ByteLength = 5678,
                Sha256 = "def456",
            },
        });

        writer.WriteSessionDocument(paths, document);
        var loaded = writer.ReadSessionDocument(paths);

        Assert.NotNull(loaded);
        Assert.Equal("ses_1", loaded!.SessionId);
        Assert.Equal(2, loaded.SourceArtifacts.Count);
        Assert.Equal(SourceArtifact.Roles.Original, loaded.SourceArtifacts[0].Role);
        Assert.Equal(@"C:\recordings\meeting.m4a", loaded.SourceArtifacts[0].OriginalPath);
        Assert.Equal("abc123", loaded.SourceArtifacts[0].Sha256);
        Assert.Equal(SourceArtifact.Roles.Normalized, loaded.SourceArtifacts[1].Role);
    }

    [Fact]
    public void SessionDocument_IsStoredWithSnakeCaseKeys()
    {
        using var workspace = new TempWorkspace();
        var paths = new SessionArtifactPaths(workspace.Root, "ses_1");
        var writer = new FileSessionArtifactWriter();
        writer.WriteSessionDocument(paths, Document());

        using var json = JsonDocument.Parse(File.ReadAllText(paths.SessionJson));
        var root = json.RootElement;

        Assert.Equal("ses_1", root.GetProperty("session_id").GetString());
        Assert.Equal("import", root.GetProperty("source_type").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("source_artifacts").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("tracks").ValueKind);
    }

    [Fact]
    public void ReadSessionDocument_ReturnsNullWhenAbsent()
    {
        using var workspace = new TempWorkspace();
        var paths = new SessionArtifactPaths(workspace.Root, "ses_1");

        Assert.Null(new FileSessionArtifactWriter().ReadSessionDocument(paths));
    }

    [Fact]
    public void AppendEvent_WritesTheDocumentedFlatShapeAndNeverRewritesHistory()
    {
        using var workspace = new TempWorkspace();
        var paths = new SessionArtifactPaths(workspace.Root, "ses_1");
        var writer = new FileSessionArtifactWriter();

        writer.AppendEvent(paths, SessionEvents.SessionCreated, 0, new Dictionary<string, object?>
        {
            ["session_id"] = "ses_1",
        });
        writer.AppendEvent(paths, SessionEvents.AsrJobQueued, 0, new Dictionary<string, object?>
        {
            ["job_id"] = "job_1",
            ["start_ms"] = 0,
            ["end_ms"] = 754000,
        });

        var lines = File.ReadAllLines(paths.EventsJsonl);
        Assert.Equal(2, lines.Length);

        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal("session.created", first.RootElement.GetProperty("event").GetString());
        Assert.Equal(0, first.RootElement.GetProperty("at_ms").GetInt64());
        Assert.Equal("ses_1", first.RootElement.GetProperty("session_id").GetString());

        using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal("asr.job.queued", second.RootElement.GetProperty("event").GetString());
        Assert.Equal("job_1", second.RootElement.GetProperty("job_id").GetString());
        Assert.Equal(754000, second.RootElement.GetProperty("end_ms").GetInt64());
    }
}
