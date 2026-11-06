using MeetCap.Core.Asr;
using MeetCap.Core.Speakers;
using MeetCap.Core.Transcripts;
using MeetCap.Persistence.Storage;
using MeetCap.Speakers.Tests.Fakes;
using Xunit;

namespace MeetCap.Speakers.Tests;

/// <summary>
/// Safety regression for issue #49's review update (2026-11-06): "Include a safety regression
/// for same-label collisions across provider jobs and manual locks. Check that a speaker label
/// reused across provider jobs is not scored/attributed ambiguously, and that manual locks are
/// respected."
/// </summary>
/// <remarks>
/// <para>
/// Provider speaker labels are anonymous and provider-job-scoped. Two independent ASR jobs can
/// reuse the same label (e.g. both report <c>speaker_0</c>) for unrelated voices, so a
/// session-wide identity bound from one job must not be silently applied to the other. The
/// attribution pipeline therefore never voiceprint-scores a label that collides across
/// provider jobs: it stays unknown unless a manual assignment covers it.
/// </para>
/// <para>
/// The counterpart — that an <i>automatic</i> binding <i>is</i> demoted when a later job
/// reuses its label — is covered by
/// <see cref="SpeakerAttributionServiceTests.LaterJobCollision_DemotesPriorAutomaticAssignment"/>.
/// The tests here add the missing half of the safety guarantee: a <i>manual</i> lock is
/// <b>not</b> demoted by a later cross-provider-job collision, and a colliding label is never
/// scored ambiguously even when an enrolled voice would otherwise match.
/// </para>
/// </remarks>
public class SpeakerLabelCollisionSafetyRegressionTests : IDisposable
{
    private readonly string _dbPath;
    private readonly MeetCapDatabase _database;
    private readonly SpeakerMatchPolicy _policy = new(Threshold: 0.80, Margin: 0.05);

    public SpeakerLabelCollisionSafetyRegressionTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "meetcap-iss49-collision-" + Guid.NewGuid().ToString("N"), "meetcap.db");
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        _database = new MeetCapDatabase(_dbPath);
        _database.EnsureMigrated();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var dir = Path.GetDirectoryName(_dbPath);
        if (dir is not null && Directory.Exists(dir))
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static TranscriptSegment Seg(string label, string text, long start, string jobId) => new()
    {
        SegmentId = $"seg_{label}_{start}_{jobId}", SessionId = "ses_1", Source = "mic",
        StartMs = start, EndMs = start + 5000, RawText = text, SpeakerLabel = label,
        ProviderJobId = jobId,
    };

    private async Task EnrollSpeaker(string name, float[] embedding)
    {
        var provider = FakeSpeakerIdentityProvider.WithFixedEmbedding(embedding);
        var registry = new SpeakerRegistry(_database.Speakers, provider);
        await registry.EnrollAsync(name, new SpeakerAudioSample { Samples = [1], SampleRate = 16000 }, CancellationToken.None);
    }

    // A provider that would match speaker_0 to Alice (embedding [1,0,0,0]) if it were ever
    // reached. The whole point of the collision guard is that it is NOT reached.
    private static FakeSpeakerIdentityProvider ProviderThatWouldMatchAlice() => new(sample =>
        sample.SpeakerLabel switch
        {
            "speaker_0" => new float[] { 1, 0, 0, 0 },
            _ => new float[] { 0, 0, 1, 0 },
        });

    [Fact]
    public async Task CrossProviderJobCollision_NeverReachesVoiceprintScoring_StaysUnknown()
    {
        // Alice is enrolled with an embedding that the provider would return for speaker_0, so
        // without the collision guard this label would be voiceprint-matched to Alice. Instead
        // the label is reused across two provider jobs, so it must stay unknown and the
        // voiceprint provider must never be handed a sample to score.
        await EnrollSpeaker("Alice", [1, 0, 0, 0]);
        var service = new SpeakerAttributionService(
            _database.Speakers, ProviderThatWouldMatchAlice(), _policy, 5, 15);

        var segments = new[]
        {
            Seg("speaker_0", "first", 0, "job_1"),
            Seg("speaker_0", "second", 5000, "job_2"),
        };

        var result = await service.BuildAsync("ses_1", segments,
            (_, _, _, _) => throw new Exception("a colliding label must not reach audio extraction"));

        // Not scored/attributed ambiguously: every segment stays anonymous.
        Assert.All(result.AttributedSegments, s => Assert.Null(s.SpeakerId));
        Assert.Equal(SpeakerAssignmentSource.Unknown, Assert.Single(result.Artifact.Entries).Source);
        Assert.Contains(result.Warnings, w => w.Contains("multiple provider jobs", StringComparison.Ordinal));

        // No ambiguous attribution row is persisted for the colliding label.
        Assert.Null(_database.Speakers.GetAssignment("ses_1", "speaker_0"));
    }

    [Fact]
    public async Task ManualLock_SurvivesCrossProviderJobCollision_AndIsNotDemoted()
    {
        // The safety guarantee the review update asks for: a manual lock is respected even when
        // a later provider job reuses the label. Enroll Alice AND Bob, then manually lock
        // speaker_0 to Bob. The provider would voiceprint-match speaker_0 to Alice, and a later
        // job reuses the label (collision). Unlike an automatic binding (which is demoted to
        // unknown by a later collision), the manual lock must survive: Bob stays bound, locked,
        // and the colliding provider is never scored.
        await EnrollSpeaker("Alice", [1, 0, 0, 0]);
        await EnrollSpeaker("Bob", [0, 1, 0, 0]);
        var bob = _database.Speakers.ListSpeakers().Single(s => s.DisplayName == "Bob");
        new SpeakerRegistry(_database.Speakers, ProviderThatWouldMatchAlice())
            .AssignManually("ses_1", "speaker_0", bob);

        var service = new SpeakerAttributionService(
            _database.Speakers, ProviderThatWouldMatchAlice(), _policy, 5, 15);

        var segments = new[]
        {
            Seg("speaker_0", "first", 0, "job_1"),
            Seg("speaker_0", "second", 5000, "job_2"),
        };

        var result = await service.BuildAsync("ses_1", segments,
            (_, _, _, _) => throw new Exception("a manual-locked colliding label must not reach audio extraction"));

        // The manual lock wins for every segment sharing the label, across both jobs.
        Assert.All(result.AttributedSegments, s =>
        {
            Assert.Equal(bob.Id, s.SpeakerId);
            Assert.Equal("Bob", s.SpeakerName);
            Assert.True(s.ManualSpeakerLock);
        });

        // The persisted assignment is still the manual Bob lock — NOT demoted to unknown by the
        // collision (contrast LaterJobCollision_DemotesPriorAutomaticAssignment).
        var assignment = _database.Speakers.GetAssignment("ses_1", "speaker_0")!;
        Assert.Equal(SpeakerAssignmentSource.Manual, assignment.Source);
        Assert.True(assignment.Locked);
        Assert.Equal(bob.Id, assignment.SpeakerId);

        // The artifact records the manual resolution and warns that the manual assignment
        // applies to every segment with the colliding label.
        var entry = Assert.Single(result.Artifact.Entries);
        Assert.Equal(SpeakerAssignmentSource.Manual, entry.Source);
        Assert.True(entry.Locked);
        Assert.Contains(result.Warnings, w => w.Contains("manual assignment applies", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ManualLock_SurvivesReAttributionAfterLaterJobArrives()
    {
        // A second safety angle: attribution is re-runnable. A manual lock established before a
        // collision must still hold when attribution is rebuilt after the colliding job arrives,
        // and the colliding label is never scored across either pass.
        await EnrollSpeaker("Alice", [1, 0, 0, 0]);
        await EnrollSpeaker("Bob", [0, 1, 0, 0]);
        var bob = _database.Speakers.ListSpeakers().Single(s => s.DisplayName == "Bob");
        var registry = new SpeakerRegistry(_database.Speakers, ProviderThatWouldMatchAlice());
        registry.AssignManually("ses_1", "speaker_0", bob);

        var service = new SpeakerAttributionService(
            _database.Speakers, ProviderThatWouldMatchAlice(), _policy, 5, 15);

        // First pass: a single, unambiguous job. The manual lock already exists, so it wins and
        // the voiceprint path is never taken.
        var first = await service.BuildAsync("ses_1",
            new[] { Seg("speaker_0", "first", 0, "job_1") },
            (_, _, _, _) => throw new Exception("manual lock must not reach extraction"));
        Assert.All(first.AttributedSegments, s => Assert.Equal(bob.Id, s.SpeakerId));

        // Second pass: a later job reuses the label, producing a collision. The manual lock
        // survives the rebuild unchanged.
        var second = await service.BuildAsync("ses_1",
            new[]
            {
                Seg("speaker_0", "first", 0, "job_1"),
                Seg("speaker_0", "second", 5000, "job_2"),
            },
            (_, _, _, _) => throw new Exception("manual lock must not reach extraction on rebuild"));

        Assert.All(second.AttributedSegments, s =>
        {
            Assert.Equal(bob.Id, s.SpeakerId);
            Assert.True(s.ManualSpeakerLock);
        });

        var assignment = _database.Speakers.GetAssignment("ses_1", "speaker_0")!;
        Assert.Equal(SpeakerAssignmentSource.Manual, assignment.Source);
        Assert.True(assignment.Locked);
        Assert.Equal(bob.Id, assignment.SpeakerId);
    }
}
