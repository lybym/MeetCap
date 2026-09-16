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
    [InlineData(SessionStatus.Recording, SessionStatus.Finalizing, true)]
    [InlineData(SessionStatus.Finalizing, SessionStatus.Processing, true)]
    [InlineData(SessionStatus.Processing, SessionStatus.Completed, true)]
    [InlineData(SessionStatus.Created, SessionStatus.Completed, false)]
    [InlineData(SessionStatus.Completed, SessionStatus.Processing, false)]
    [InlineData(SessionStatus.Recording, SessionStatus.Completed, false)]
    public void CanTransition_MatchesTheDocumentedLifecycle(string from, string to, bool expected) =>
        Assert.Equal(expected, SessionStateMachine.CanTransition(from, to));

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
    public void ActiveStatuses_AreTheNonTerminalOnes()
    {
        Assert.True(SessionStatus.IsActive(SessionStatus.Processing));
        Assert.False(SessionStatus.IsActive(SessionStatus.Completed));
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
