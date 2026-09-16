using MeetCap.Core.Sessions;
using MeetCap.Persistence.Storage;
using Xunit;

namespace MeetCap.Persistence.Tests.Storage;

public class SqliteSessionStoreTests
{
    private static readonly DateTimeOffset s_now = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);

    private static Session Session(string id = "ses_1", string status = SessionStatus.Processing) => new()
    {
        Id = id,
        Title = "Weekly Meeting",
        Mode = SessionMode.Import,
        SourceType = SessionSourceType.Import,
        Status = status,
        StartedAt = s_now,
        DurationMs = 754_000,
        ConfigSnapshotJson = "{\"config_version\":1}",
        ConfigVersion = 1,
        Tracks = new[] { AudioTrackName.Import },
        CreatedAt = s_now,
        UpdatedAt = s_now,
    };

    [Fact]
    public void RoundTripsAnImportSession()
    {
        using var workspace = new TempWorkspace();
        new SqliteMigrator().Migrate(workspace.DatabasePath);
        var store = new SqliteSessionStore(workspace.DatabasePath);

        var session = Session();
        store.Create(session);

        var loaded = store.Get(session.Id);

        Assert.NotNull(loaded);
        Assert.Equal(session.Id, loaded!.Id);
        Assert.Equal(session.Title, loaded.Title);
        Assert.Equal(SessionMode.Import, loaded.Mode);
        Assert.Equal(SessionSourceType.Import, loaded.SourceType);
        Assert.Equal(SessionStatus.Processing, loaded.Status);
        Assert.Equal(754_000, loaded.DurationMs);
        Assert.Equal(new[] { AudioTrackName.Import }, loaded.Tracks);
        Assert.Equal(session.StartedAt, loaded.StartedAt);
        Assert.Equal(session.CreatedAt, loaded.CreatedAt);
    }

    [Fact]
    public void Update_AdvancesStatusAndTimestamps()
    {
        using var workspace = new TempWorkspace();
        new SqliteMigrator().Migrate(workspace.DatabasePath);
        var store = new SqliteSessionStore(workspace.DatabasePath);

        var session = Session();
        store.Create(session);
        store.Update(session.WithStatus(SessionStatus.Completed, s_now.AddMinutes(5)));

        var loaded = store.Get(session.Id);
        Assert.Equal(SessionStatus.Completed, loaded!.Status);
        Assert.Equal(s_now.AddMinutes(5), loaded.UpdatedAt);
    }

    [Fact]
    public void Update_OnMissingSession_FailsInsteadOfSilentlyDoingNothing()
    {
        using var workspace = new TempWorkspace();
        new SqliteMigrator().Migrate(workspace.DatabasePath);

        var store = new SqliteSessionStore(workspace.DatabasePath);
        Assert.Throws<InvalidOperationException>(() => store.Update(Session("ses_missing")));
    }

    [Fact]
    public void Get_ReturnsNullForAnUnknownSession()
    {
        using var workspace = new TempWorkspace();
        new SqliteMigrator().Migrate(workspace.DatabasePath);

        Assert.Null(new SqliteSessionStore(workspace.DatabasePath).Get("ses_nope"));
    }

    [Fact]
    public void CountActive_CountsOnlyNonTerminalSessions()
    {
        using var workspace = new TempWorkspace();
        new SqliteMigrator().Migrate(workspace.DatabasePath);
        var store = new SqliteSessionStore(workspace.DatabasePath);

        store.Create(Session("ses_processing", SessionStatus.Processing));
        store.Create(Session("ses_completed", SessionStatus.Completed));

        Assert.Equal(1, store.CountActive());
    }
}
