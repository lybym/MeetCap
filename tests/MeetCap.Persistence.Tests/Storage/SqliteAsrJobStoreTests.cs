using MeetCap.Core.Asr;
using MeetCap.Persistence.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeetCap.Persistence.Tests.Storage;

/// <summary>
/// The persistent ASR queue is the durability boundary for M3: retry state must
/// survive process restart, so these tests reopen the database between writes rather
/// than keeping a handle alive.
/// </summary>
public class SqliteAsrJobStoreTests
{
    private static readonly DateTimeOffset s_now = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);

    private static AsrJob Job(
        string id = "job_1",
        AsrJobStatus status = AsrJobStatus.Pending,
        DateTimeOffset? nextRetryAt = null,
        string sessionId = "ses_1",
        int attempts = 0) => new()
    {
        Id = id,
        SessionId = sessionId,
        Source = "import",
        Provider = "volcengine",
        StartMs = 0,
        EndMs = 754_000,
        InputArtifact = "audio/import/normalized.wav",
        Status = status,
        ProviderRequestId = "req-" + id,
        AttemptCount = attempts,
        NextRetryAt = nextRetryAt,
        DurationMs = 754_000,
        SpeakerInfoRequested = true,
        EstimatedCostCny = 0.16,
        CreatedAt = s_now,
        UpdatedAt = s_now,
    };

    private static SqliteAsrJobStore Store(TempWorkspace workspace) => new(workspace.DatabasePath);

    [Fact]
    public void RoundTripsEveryPersistedField()
    {
        using var workspace = new TempWorkspace();
        new SqliteMigrator().Migrate(workspace.DatabasePath);

        var job = Job() with
        {
            Status = AsrJobStatus.Polling,
            AttemptCount = 2,
            NextRetryAt = s_now.AddSeconds(30),
            RequestMetadataPath = @"C:\data\sessions\ses_1\asr\jobs\job_1\request.json",
            RawResponsePath = @"C:\data\sessions\ses_1\asr\jobs\job_1\response.json",
            NormalizedResultPath = @"C:\data\sessions\ses_1\asr\jobs\job_1\normalized.jsonl",
            ErrorCode = "http.503",
            ErrorMessage = "temporarily unavailable",
            ProviderLogId = "20260918120000ABCDEF",
            SpeakerInfoReturned = true,
            SubmittedAt = s_now.AddSeconds(1),
        };

        Store(workspace).Create(job);

        // A fresh store instance stands in for a later process.
        var loaded = Store(workspace).Get(job.Id);

        Assert.NotNull(loaded);
        Assert.Equal(job.SessionId, loaded!.SessionId);
        Assert.Equal(job.InputArtifact, loaded.InputArtifact);
        Assert.Equal(AsrJobStatus.Polling, loaded.Status);
        Assert.Equal("req-job_1", loaded.ProviderRequestId);
        Assert.Equal(2, loaded.AttemptCount);
        Assert.Equal(s_now.AddSeconds(30), loaded.NextRetryAt);
        Assert.Equal(job.RequestMetadataPath, loaded.RequestMetadataPath);
        Assert.Equal(job.RawResponsePath, loaded.RawResponsePath);
        Assert.Equal(job.NormalizedResultPath, loaded.NormalizedResultPath);
        Assert.Equal("http.503", loaded.ErrorCode);
        Assert.Equal("temporarily unavailable", loaded.ErrorMessage);
        Assert.Equal("20260918120000ABCDEF", loaded.ProviderLogId);
        // `tier` is a schema-compatibility column with one constant value.
        Assert.Equal("standard", loaded.Tier);
        Assert.True(loaded.SpeakerInfoRequested);
        Assert.True(loaded.SpeakerInfoReturned);
        Assert.Equal(0.16, loaded.EstimatedCostCny, precision: 6);
        Assert.Equal(s_now.AddSeconds(1), loaded.SubmittedAt);
        Assert.Equal(job.DurationMs, loaded.DurationMs);
    }

    [Fact]
    public void ProviderRequestId_IsPersistedBeforeTheFirstSubmit()
    {
        // This is what lets a restart resume by polling instead of submitting again.
        using var workspace = new TempWorkspace();
        new SqliteMigrator().Migrate(workspace.DatabasePath);

        Store(workspace).Create(Job());

        Assert.Equal("req-job_1", Store(workspace).Get("job_1")!.ProviderRequestId);
    }

    [Fact]
    public void ProviderLogId_DefaultsToNullForAJobThatHasNotReachedTheProviderYet()
    {
        using var workspace = new TempWorkspace();
        new SqliteMigrator().Migrate(workspace.DatabasePath);

        Store(workspace).Create(Job());

        Assert.Null(Store(workspace).Get("job_1")!.ProviderLogId);
    }

    [Fact]
    public void ProviderLogId_IsPersistedByUpdateForTheNextProcess()
    {
        using var workspace = new TempWorkspace();
        new SqliteMigrator().Migrate(workspace.DatabasePath);
        var store = Store(workspace);

        store.Create(Job());
        store.Update(AsrJobTransitions.RecordProviderLogId(store.Get("job_1")!, "log-1", s_now));

        // A fresh store instance stands in for a later process.
        Assert.Equal("log-1", Store(workspace).Get("job_1")!.ProviderLogId);
    }

    [Fact]
    public void Update_PersistsTransitionsForTheNextProcess()
    {
        using var workspace = new TempWorkspace();
        new SqliteMigrator().Migrate(workspace.DatabasePath);

        Store(workspace).Create(Job());
        var polling = AsrJobTransitions.BeginPolling(
            AsrJobTransitions.MarkSubmitted(
                AsrJobTransitions.BeginSubmit(Job(), s_now),
                "request.json",
                s_now),
            s_now);
        Store(workspace).Update(polling);

        var loaded = Store(workspace).Get("job_1");
        Assert.Equal(AsrJobStatus.Polling, loaded!.Status);
        Assert.Equal(1, loaded.AttemptCount);
        Assert.Equal("request.json", loaded.RequestMetadataPath);
    }

    [Fact]
    public void Update_OnMissingJob_FailsInsteadOfSilentlyDoingNothing()
    {
        using var workspace = new TempWorkspace();
        new SqliteMigrator().Migrate(workspace.DatabasePath);

        Assert.Throws<InvalidOperationException>(() => Store(workspace).Update(Job("job_missing")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_RejectsAJobWithoutAProviderRequestId(string requestId)
    {
        // The invariant is enforced where rows are written, so the domain type and the store
        // tell one story instead of relying on a defensive branch in the processor.
        using var workspace = new TempWorkspace();
        new SqliteMigrator().Migrate(workspace.DatabasePath);

        var ex = Assert.Throws<ArgumentException>(
            () => Store(workspace).Create(Job() with { ProviderRequestId = requestId }));

        Assert.Contains("provider request id", ex.Message, StringComparison.Ordinal);
        Assert.Null(Store(workspace).Get("job_1"));
    }

    [Fact]
    public void Get_FailsLoudlyAndNamesTheJobWhenAStoredRowHasNoProviderRequestId()
    {
        // The column stays nullable in SQLite, so an externally written or hand-edited row can
        // still violate the invariant. That must fail loudly and identify the row instead of
        // aborting the queue anonymously.
        using var workspace = new TempWorkspace();
        new SqliteMigrator().Migrate(workspace.DatabasePath);

        using (var connection = new SqliteConnection(
                   new SqliteConnectionStringBuilder
                   {
                       DataSource = workspace.DatabasePath,
                       Pooling = false,
                   }.ToString()))
        {
            connection.Open();
            using var cmd = new SqliteCommand(
                "INSERT INTO asr_jobs (id, session_id, source, tier, provider, input_artifact, status, " +
                "provider_request_id, created_at, updated_at) " +
                "VALUES ('job_broken', 'ses_1', 'import', 'standard', 'volcengine', " +
                "'audio/import/a.wav', 'pending', NULL, @now, @now)",
                connection);
            cmd.Parameters.AddWithValue("@now", "2026-09-15T00:00:00Z");
            cmd.ExecuteNonQuery();
        }

        var ex = Assert.Throws<InvalidOperationException>(() => Store(workspace).Get("job_broken"));

        Assert.Contains("job_broken", ex.Message, StringComparison.Ordinal);
        Assert.Contains("provider request id", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ListResumable_ReturnsNonTerminalJobsDueNow()
    {
        using var workspace = new TempWorkspace();
        new SqliteMigrator().Migrate(workspace.DatabasePath);
        var store = Store(workspace);

        store.Create(Job("job_pending"));
        store.Create(Job("job_future", AsrJobStatus.RetryWait, s_now.AddMinutes(10)));
        store.Create(Job("job_due", AsrJobStatus.RetryWait, s_now.AddMinutes(-1)));
        store.Create(Job("job_done", AsrJobStatus.Succeeded));
        store.Create(Job("job_failed", AsrJobStatus.Failed));

        var resumable = store.ListResumable(s_now, limit: 10).Select(j => j.Id).OrderBy(id => id).ToArray();

        Assert.Equal(new[] { "job_due", "job_pending" }, resumable);
    }

    [Fact]
    public void ListResumable_HonoursTheSessionFilterAndLimit()
    {
        using var workspace = new TempWorkspace();
        new SqliteMigrator().Migrate(workspace.DatabasePath);
        var store = Store(workspace);

        store.Create(Job("job_a", sessionId: "ses_1"));
        store.Create(Job("job_b", sessionId: "ses_2"));

        Assert.Equal(new[] { "job_a" }, store.ListResumable(s_now, 10, "ses_1").Select(j => j.Id));
        Assert.Single(store.ListResumable(s_now, 1));
        Assert.Empty(store.ListResumable(s_now, 0));
    }

    [Fact]
    public void CountByStatus_ReflectsTheQueueDepth()
    {
        using var workspace = new TempWorkspace();
        new SqliteMigrator().Migrate(workspace.DatabasePath);
        var store = Store(workspace);

        store.Create(Job("job_1", AsrJobStatus.Pending));
        store.Create(Job("job_2", AsrJobStatus.RetryWait, s_now));
        store.Create(Job("job_3", AsrJobStatus.Succeeded));

        Assert.Equal(1, store.CountByStatus(AsrJobStatus.Pending));
        Assert.Equal(1, store.CountByStatus(AsrJobStatus.RetryWait));
        Assert.Equal(1, store.CountByStatus(AsrJobStatus.Succeeded));
        Assert.Equal(0, store.CountByStatus(AsrJobStatus.Failed));
    }

    [Fact]
    public void ListBySession_ReturnsOnlyThatSessionsJobs()
    {
        using var workspace = new TempWorkspace();
        new SqliteMigrator().Migrate(workspace.DatabasePath);
        var store = Store(workspace);

        store.Create(Job("job_a", sessionId: "ses_1"));
        store.Create(Job("job_b", sessionId: "ses_2"));

        Assert.Equal(new[] { "job_a" }, store.ListBySession("ses_1").Select(j => j.Id));
    }
}
