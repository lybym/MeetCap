using MeetCap.Core.Capture;
using MeetCap.Core.Sessions;
using MeetCap.Persistence.Storage;
using Xunit;

namespace MeetCap.Persistence.Tests.Storage;

public class RepositoryTests : IDisposable
{
    private readonly string _directory;
    private readonly MeetCapDatabase _database;

    public RepositoryTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "meetcap-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _database = new MeetCapDatabase(Path.Combine(_directory, "meetcap.db"));
        _database.EnsureMigrated();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp directory.
        }
    }

    private static readonly DateTimeOffset Instant = new(2026, 9, 15, 14, 0, 0, TimeSpan.Zero);

    private static SessionRecord Session(
        string id,
        string status = SessionStatus.Recording,
        string title = "Weekly Meeting",
        DateTimeOffset? createdAt = null)
    {
        var created = createdAt ?? Instant;
        return new SessionRecord
        {
            Id = id,
            Title = title,
            Mode = SessionModes.Offline,
            SourceType = SessionSourceTypes.Live,
            Status = status,
            StartedAt = Instant,
            ConfigVersion = 1,
            ConfigSnapshot = "{\"chunk_seconds\":60}",
            Tracks = new[] { AudioSources.Mic },
            CreatedAt = created,
            UpdatedAt = created,
        };
    }

    [Fact]
    public void SessionRepository_RoundTripsEveryDocumentedField()
    {
        _database.Sessions.Insert(Session("ses_one"));

        var loaded = _database.Sessions.Find("ses_one");

        Assert.NotNull(loaded);
        Assert.Equal("Weekly Meeting", loaded.Title);
        Assert.Equal(SessionModes.Offline, loaded.Mode);
        Assert.Equal(SessionSourceTypes.Live, loaded.SourceType);
        Assert.Equal(SessionStatus.Recording, loaded.Status);
        Assert.Equal(Instant, loaded.StartedAt);
        Assert.Null(loaded.StoppedAt);
        Assert.Equal(1, loaded.ConfigVersion);
        Assert.Equal("{\"chunk_seconds\":60}", loaded.ConfigSnapshot);
        Assert.Equal(new[] { AudioSources.Mic }, loaded.Tracks);
    }

    [Fact]
    public void SessionRepository_Insert_RejectsADuplicateId()
    {
        _database.Sessions.Insert(Session("ses_dupe"));

        Assert.ThrowsAny<Microsoft.Data.Sqlite.SqliteException>(
            () => _database.Sessions.Insert(Session("ses_dupe")));
    }

    [Fact]
    public void SessionRepository_Upsert_RefreshesAnExistingRow()
    {
        _database.Sessions.Insert(Session("ses_up", title: "First"));
        _database.Sessions.Upsert(Session("ses_up", title: "Second"));

        Assert.Equal("Second", _database.Sessions.Find("ses_up")!.Title);
    }

    [Fact]
    public void SessionRepository_UpdateLifecycle_RecordsTheStop()
    {
        _database.Sessions.Insert(Session("ses_stop"));

        var stoppedAt = Instant.AddMinutes(30);
        _database.Sessions.UpdateLifecycle("ses_stop", SessionStatus.Completed, stoppedAt, stoppedAt, 1_800_000);

        var loaded = _database.Sessions.Find("ses_stop")!;
        Assert.Equal(SessionStatus.Completed, loaded.Status);
        Assert.Equal(stoppedAt, loaded.StoppedAt);
        Assert.Equal(1_800_000, loaded.DurationMs);
    }

    [Fact]
    public void SessionRepository_ListsActiveAndRecoverableSessions()
    {
        _database.Sessions.Insert(Session("ses_a", SessionStatus.Recording));
        _database.Sessions.Insert(Session("ses_b", SessionStatus.Completed));
        _database.Sessions.Insert(Session("ses_c", SessionStatus.Interrupted));
        _database.Sessions.Insert(Session("ses_d", SessionStatus.Finalizing));

        Assert.Equal(
            new[] { "ses_a", "ses_d" },
            _database.Sessions.ListActive().Select(s => s.Id).OrderBy(id => id));

        Assert.Equal(
            new[] { "ses_a", "ses_d" },
            _database.Sessions.ListNeedingRecovery().Select(s => s.Id).OrderBy(id => id));
    }

    [Fact]
    public void SessionRepository_FindActiveSession_PrefersTheNewest()
    {
        var older = Session("ses_older", SessionStatus.Created, createdAt: Instant);
        var newer = Session("ses_newer", SessionStatus.Recording, createdAt: Instant.AddMinutes(5));

        _database.Sessions.Insert(older);
        _database.Sessions.Insert(newer);

        Assert.Equal("ses_newer", _database.Sessions.FindActiveSession()!.Id);
        Assert.Equal(2, _database.CountActiveSessions());
    }

    [Fact]
    public void AudioChunkRepository_Upsert_MovesAChunkFromOpenToClosed()
    {
        _database.Sessions.Insert(Session("ses_chunk"));

        _database.Chunks.Upsert(Chunk("ses_chunk", 1, ChunkStates.Open, byteLength: 0));
        Assert.Equal(ChunkStates.Open, _database.Chunks.Find("ses_chunk", AudioSource.Mic, 1)!.Status);

        var closedAt = Instant.AddMinutes(1);
        _database.Chunks.Upsert(Chunk("ses_chunk", 1, ChunkStates.Closed, byteLength: 5_760_000, closedAt: closedAt));

        var loaded = _database.Chunks.Find("ses_chunk", AudioSource.Mic, 1)!;
        Assert.Equal(ChunkStates.Closed, loaded.Status);
        Assert.Equal(5_760_000, loaded.ByteLength);
        Assert.Equal(closedAt, loaded.ClosedAt);
        Assert.Equal(1, _database.Chunks.CountForSession("ses_chunk"));
    }

    [Fact]
    public void AudioChunkRepository_RoundTripsFormatAndTimingAnchors()
    {
        _database.Sessions.Insert(Session("ses_anchor"));
        _database.Chunks.Upsert(Chunk("ses_anchor", 3, ChunkStates.Closed, byteLength: 1_920, closedAt: Instant));

        var loaded = _database.Chunks.Find("ses_anchor", AudioSource.Mic, 3)!;

        Assert.Equal(new AudioFormat(48_000, 2, 32, AudioSampleFormat.IeeeFloat), loaded.Format);
        Assert.Equal("audio/mic/000003.wav", loaded.RelativePath);
        Assert.Equal(120_000, loaded.StartMs);
        Assert.Equal(180_000, loaded.EndMs);
        Assert.Equal(4_800_000, loaded.DevicePositionFrames);
        Assert.Equal(999_000, loaded.QpcPositionTicks);
        Assert.Equal(60_000, loaded.DurationMs);
    }

    [Fact]
    public void AudioChunkRepository_AggregatesPerSession()
    {
        _database.Sessions.Insert(Session("ses_totals"));
        _database.Chunks.Upsert(Chunk("ses_totals", 1, ChunkStates.Closed, byteLength: 100));
        _database.Chunks.Upsert(Chunk("ses_totals", 2, ChunkStates.Recovered, byteLength: 250));
        _database.Chunks.Upsert(Chunk("ses_totals", 3, ChunkStates.Corrupt, byteLength: 10));

        Assert.Equal(3, _database.Chunks.CountForSession("ses_totals"));
        Assert.Equal(360, _database.Chunks.TotalByteLengthForSession("ses_totals"));
        Assert.Single(_database.Chunks.ListByStatus("ses_totals", ChunkStates.Recovered));
        Assert.Equal(3, _database.Chunks.ListForSession("ses_totals").Count);
    }

    [Fact]
    public void AudioChunkRepository_RejectsChunksForAnUnknownSession()
    {
        Assert.ThrowsAny<Microsoft.Data.Sqlite.SqliteException>(
            () => _database.Chunks.Upsert(Chunk("ses_unknown", 1, ChunkStates.Open, byteLength: 0)));
    }

    private static AudioChunkRecord Chunk(
        string sessionId,
        int sequence,
        string status,
        long byteLength,
        DateTimeOffset? closedAt = null) => new()
    {
        Id = AudioChunkRecord.BuildId(sessionId, AudioSource.Mic, sequence),
        SessionId = sessionId,
        Source = AudioSource.Mic,
        Sequence = sequence,
        RelativePath = $"audio/mic/{sequence:D6}.wav",
        StartMs = sequence == 1 ? 0 : 60_000 * (sequence - 1),
        EndMs = sequence * 60_000,
        Format = new AudioFormat(48_000, 2, 32, AudioSampleFormat.IeeeFloat),
        ByteLength = byteLength,
        Status = status,
        DevicePositionFrames = 4_800_000,
        QpcPositionTicks = 999_000,
        CreatedAt = Instant,
        ClosedAt = closedAt,
    };
}
