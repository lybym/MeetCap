using MeetCap.Core.Ids;
using MeetCap.Core.Speakers;
using MeetCap.Persistence.Storage;
using MeetCap.Speakers.Tests.Fakes;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MeetCap.Speakers.Tests;

/// <summary>
/// Tests the local Speaker Registry enrollment and identification workflow
/// (Issue #8 acceptance criteria 1, 2). Uses a real SQLite store and a fake
/// identity provider so the registry is exercised end to end without the
/// sherpa-onnx native runtime (<c>docs/DEVELOPMENT.md</c> section 7).
/// </summary>
public class SpeakerRegistryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly MeetCapDatabase _database;

    public SpeakerRegistryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "meetcap-speaker-test-" + Guid.NewGuid().ToString("N"), "meetcap.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        _database = new MeetCapDatabase(_dbPath);
        _database.EnsureMigrated();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        var dir = Path.GetDirectoryName(_dbPath);
        if (dir is not null && Directory.Exists(dir))
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private SpeakerAudioSample Sample(params float[] values) => new()
    {
        Samples = values.Length > 0 ? values : [0.1f, 0.2f, 0.3f, 0.4f],
        SampleRate = 16000,
    };

    [Fact]
    public async Task Enroll_CreatesSpeakerAndPersistsEmbedding()
    {
        var provider = FakeSpeakerIdentityProvider.WithFixedEmbedding([1, 0, 0, 0]);
        var registry = new SpeakerRegistry(_database.Speakers, provider);

        var speaker = await registry.EnrollAsync("Alice", Sample(), CancellationToken.None);

        Assert.Equal("Alice", speaker.DisplayName);
        Assert.StartsWith(Ids.SpeakerPrefix, speaker.Id);
        var embeddings = _database.Speakers.ListEmbeddings(speaker.Id);
        Assert.Single(embeddings);
        Assert.Equal(4, embeddings[0].Dimension);
    }

    [Fact]
    public async Task Enroll_MultipleTimesForSameName_AddsEmbeddings()
    {
        var provider = FakeSpeakerIdentityProvider.WithFixedEmbedding([1, 0, 0, 0]);
        var registry = new SpeakerRegistry(_database.Speakers, provider);

        await registry.EnrollAsync("Alice", Sample(), CancellationToken.None);
        await registry.EnrollAsync("Alice", Sample(), CancellationToken.None);

        var speaker = _database.Speakers.ListSpeakers().Single(s => s.DisplayName == "Alice");
        var embeddings = _database.Speakers.ListEmbeddings(speaker.Id);
        Assert.Equal(2, embeddings.Count);
    }

    [Fact]
    public async Task Enroll_DifferentNames_CreatesSeparateSpeakers()
    {
        var provider = FakeSpeakerIdentityProvider.WithFixedEmbedding([1, 0, 0, 0]);
        var registry = new SpeakerRegistry(_database.Speakers, provider);

        await registry.EnrollAsync("Alice", Sample(), CancellationToken.None);
        await registry.EnrollAsync("Bob", Sample(), CancellationToken.None);

        Assert.Equal(2, _database.Speakers.ListSpeakers().Count);
    }

    [Fact]
    public async Task Identify_ReturnsRankedCandidates()
    {
        var provider = FakeSpeakerIdentityProvider.WithFixedEmbedding([1, 0, 0, 0]);
        var registry = new SpeakerRegistry(_database.Speakers, provider);

        // Enroll Alice with embedding [1,0,0,0] and Bob with [0,1,0,0].
        var aliceProvider = FakeSpeakerIdentityProvider.WithFixedEmbedding([1, 0, 0, 0]);
        var bobProvider = FakeSpeakerIdentityProvider.WithFixedEmbedding([0, 1, 0, 0]);
        var aliceReg = new SpeakerRegistry(_database.Speakers, aliceProvider);
        var bobReg = new SpeakerRegistry(_database.Speakers, bobProvider);
        await aliceReg.EnrollAsync("Alice", Sample(), CancellationToken.None);
        await bobReg.EnrollAsync("Bob", Sample(), CancellationToken.None);

        // Identify with a probe that matches Alice exactly.
        var candidates = await registry.IdentifyAsync(Sample(), CancellationToken.None);

        Assert.NotEmpty(candidates);
        // Alice's embedding [1,0,0,0] matches the probe [1,0,0,0] exactly (cosine = 1.0).
        // Bob's embedding [0,1,0,0] is orthogonal to the probe (cosine = 0.0).
        Assert.Equal("Alice", candidates[0].DisplayName);
        Assert.True(candidates[0].Score > candidates[1].Score);
    }

    [Fact]
    public async Task Identify_NoEnrolledSpeakers_ReturnsEmpty()
    {
        var provider = FakeSpeakerIdentityProvider.WithFixedEmbedding([1, 0, 0, 0]);
        var registry = new SpeakerRegistry(_database.Speakers, provider);

        var candidates = await registry.IdentifyAsync(Sample(), CancellationToken.None);
        Assert.Empty(candidates);
    }

    [Fact]
    public async Task AssignManually_CreatesLockedAssignment()
    {
        var provider = FakeSpeakerIdentityProvider.WithFixedEmbedding([1, 0, 0, 0]);
        var registry = new SpeakerRegistry(_database.Speakers, provider);

        // Enroll a speaker first.
        await registry.EnrollAsync("Alice", Sample(), CancellationToken.None);
        var speaker = _database.Speakers.ListSpeakers().Single();

        var assignment = registry.AssignManually("ses_1", "speaker_0", speaker);

        Assert.Equal("ses_1", assignment.SessionId);
        Assert.Equal("speaker_0", assignment.SpeakerLabel);
        Assert.Equal(speaker.Id, assignment.SpeakerId);
        Assert.Equal(SpeakerAssignmentSource.Manual, assignment.Source);
        Assert.True(assignment.Locked);

        // Verify it was persisted.
        var loaded = _database.Speakers.GetAssignment("ses_1", "speaker_0");
        Assert.NotNull(loaded);
        Assert.True(loaded!.Locked);
    }

    [Fact]
    public async Task AssignManually_IsIdempotent_UpsertsSameLabel()
    {
        var provider = FakeSpeakerIdentityProvider.WithFixedEmbedding([1, 0, 0, 0]);
        var registry = new SpeakerRegistry(_database.Speakers, provider);
        await registry.EnrollAsync("Alice", Sample(), CancellationToken.None);
        var speaker = _database.Speakers.ListSpeakers().Single();

        registry.AssignManually("ses_1", "speaker_0", speaker);
        registry.AssignManually("ses_1", "speaker_0", speaker);

        var assignments = _database.Speakers.ListAssignments("ses_1");
        Assert.Single(assignments);
    }
}
