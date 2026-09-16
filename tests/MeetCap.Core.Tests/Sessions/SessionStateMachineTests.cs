using MeetCap.Core.Sessions;
using Xunit;

namespace MeetCap.Core.Tests.Sessions;

public class SessionStateMachineTests
{
    private static readonly DateTimeOffset s_now = new(2026, 9, 15, 10, 0, 0, TimeSpan.Zero);

    private static Session Session(string status = SessionStatus.Created) => new()
    {
        Id = "ses_1",
        Title = "Weekly Meeting",
        Mode = SessionMode.Import,
        SourceType = SessionSourceType.Import,
        Status = status,
        CreatedAt = s_now,
        UpdatedAt = s_now,
    };

    [Theory]
    [InlineData(SessionStatus.Created, SessionStatus.Recording, true)]
    [InlineData(SessionStatus.Created, SessionStatus.Processing, true)]
    [InlineData(SessionStatus.Created, SessionStatus.Interrupted, true)]
    [InlineData(SessionStatus.Recording, SessionStatus.Finalizing, true)]
    [InlineData(SessionStatus.Recording, SessionStatus.Interrupted, true)]
    [InlineData(SessionStatus.Finalizing, SessionStatus.Completed, true)]
    [InlineData(SessionStatus.Finalizing, SessionStatus.Processing, true)]
    [InlineData(SessionStatus.Finalizing, SessionStatus.Interrupted, true)]
    [InlineData(SessionStatus.Processing, SessionStatus.Completed, true)]
    [InlineData(SessionStatus.Processing, SessionStatus.Interrupted, true)]
    [InlineData(SessionStatus.Created, SessionStatus.Completed, false)]
    [InlineData(SessionStatus.Completed, SessionStatus.Processing, false)]
    [InlineData(SessionStatus.Completed, SessionStatus.Interrupted, false)]
    [InlineData(SessionStatus.Interrupted, SessionStatus.Completed, false)]
    [InlineData(SessionStatus.Interrupted, SessionStatus.Recording, false)]
    [InlineData(SessionStatus.Recording, SessionStatus.Completed, false)]
    public void CanTransition_MatchesTheDocumentedLifecycle(string from, string to, bool expected) =>
        Assert.Equal(expected, SessionStateMachine.CanTransition(from, to));

    [Fact]
    public void M1RecordingPath_StopsAtFinalizingThenCompletesWithoutProcessing()
    {
        // docs/ARCHITECTURE.md section 20: M1 implements the subset with no post-capture
        // work, so a clean offline recording goes straight from FINALIZING to COMPLETED
        // and never enters PROCESSING.
        var session = Session(SessionStatus.Created)
            .WithStatus(SessionStatus.Recording, s_now.AddSeconds(1))
            .WithStatus(SessionStatus.Finalizing, s_now.AddMinutes(5))
            .WithStatus(SessionStatus.Completed, s_now.AddMinutes(5).AddSeconds(2));

        Assert.Equal(SessionStatus.Completed, session.Status);
        Assert.True(SessionStateMachine.IsTerminal(SessionStatus.Completed));
    }

    [Theory]
    [InlineData(SessionStatus.Created)]
    [InlineData(SessionStatus.Recording)]
    [InlineData(SessionStatus.Finalizing)]
    [InlineData(SessionStatus.Processing)]
    public void Interrupted_IsReachableFromEveryNonTerminalStatus(string from)
    {
        // Every non-terminal status can be found abandoned by startup recovery, or be
        // abandoned by the recording process, so INTERRUPTED is a legal edge from each.
        var interrupted = Session(from).WithStatus(SessionStatus.Interrupted, s_now.AddMinutes(1));

        Assert.Equal(SessionStatus.Interrupted, interrupted.Status);
        Assert.True(SessionStateMachine.IsTerminal(SessionStatus.Interrupted));
    }

    [Fact]
    public void M1RecordingPath_CanBeInterruptedFromFinalizing()
    {
        // The close/index window: capture ended and the artifacts were being closed when
        // the process died, leaving a FINALIZING session that recovery must terminalize.
        var interrupted = Session(SessionStatus.Finalizing)
            .WithStatus(SessionStatus.Interrupted, s_now.AddMinutes(6));

        Assert.Equal(SessionStatus.Interrupted, interrupted.Status);
    }

    [Fact]
    public void ImportSession_GoesStraightToProcessingAndThenCompleted()
    {
        var session = Session(SessionStatus.Processing);
        var completed = session.WithStatus(SessionStatus.Completed, s_now.AddMinutes(1));

        Assert.Equal(SessionStatus.Completed, completed.Status);
        Assert.Equal(s_now.AddMinutes(1), completed.UpdatedAt);
    }

    [Fact]
    public void CompletedSession_CannotBeReopened()
    {
        Assert.Throws<InvalidSessionTransitionException>(
            () => Session(SessionStatus.Completed).WithStatus(SessionStatus.Processing, s_now));
    }

    [Fact]
    public void InterruptedSession_CannotBeReopened()
    {
        // INTERRUPTED is terminal, so a session that recovery already terminalized can
        // never be advanced back into the recording lifecycle.
        Assert.Throws<InvalidSessionTransitionException>(
            () => Session(SessionStatus.Interrupted).WithStatus(SessionStatus.Recording, s_now));
    }

    [Fact]
    public void ActiveStatuses_AreTheNonTerminalOnes()
    {
        Assert.True(SessionStatus.IsActive(SessionStatus.Processing));
        Assert.True(SessionStatus.IsActive(SessionStatus.Finalizing));
        Assert.False(SessionStatus.IsActive(SessionStatus.Completed));
        Assert.False(SessionStatus.IsActive(SessionStatus.Interrupted));
    }
}

public class SessionArtifactPathsTests
{
    [Fact]
    public void LayoutMatchesTheDocumentedArtifactContract()
    {
        var paths = new SessionArtifactPaths(@"C:\data", "ses_1");

        Assert.EndsWith(Path.Combine("sessions", "ses_1", "session.json"), paths.SessionJson, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("sessions", "ses_1", "events.jsonl"), paths.EventsJsonl, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("transcript", "raw.jsonl"), paths.RawTranscriptJsonl, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("transcript", "live.md"), paths.LiveTranscriptMarkdown, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("asr", "jobs", "job_1", "response.json"), paths.JobResponseJson("job_1"), StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("asr", "jobs", "job_1", "normalized.jsonl"), paths.JobNormalizedJsonl("job_1"), StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("audio", "import", "meeting.m4a"), paths.ImportAudioFile("meeting.m4a"), StringComparison.Ordinal);
    }

    [Fact]
    public void RelativeArtifactPaths_RoundTripThroughTheSessionDirectory()
    {
        // input_artifact is stored session-relative so the artifact contract survives a
        // data-root move.
        var paths = new SessionArtifactPaths(@"C:\data", "ses_1");
        var absolute = paths.ImportAudioFile("normalized.wav");

        Assert.Equal(absolute, paths.ResolveRelative(paths.ToRelative(absolute)));
        Assert.Equal("audio/import/normalized.wav", paths.ToRelative(absolute));
    }
}
