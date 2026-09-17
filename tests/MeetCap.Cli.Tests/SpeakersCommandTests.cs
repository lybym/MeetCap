using MeetCap.Cli.Commands;
using MeetCap.Core.Ids;
using MeetCap.Core.Speakers;
using MeetCap.Persistence.Storage;
using Xunit;

namespace MeetCap.Cli.Tests;

/// <summary>
/// End-to-end CLI tests for <c>meetcap speakers</c> (Issue #8, M6). Uses the real
/// command tree with a temporary configuration store and captured output
/// (<c>docs/DEVELOPMENT.md</c> section 7).
/// </summary>
public class SpeakersCommandTests : IDisposable
{
    private readonly CliHarness _harness = CliHarness.Create();

    public SpeakersCommandTests()
    {
        _harness.WriteCaptureConfig();
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void Speakers_NoSubcommand_ReturnsUsageError()
    {
        var result = _harness.Run("speakers");
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("missing subcommand", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Speakers_List_Empty_PrintsNoSpeakers()
    {
        var result = _harness.Run("speakers", "list");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("No speakers enrolled", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Speakers_List_ShowsEnrolledSpeaker()
    {
        // Pre-insert a speaker via the store so the list command shows it.
        var database = new MeetCapDatabase(CommandSupport.DatabasePath(_harness.DataRoot));
        database.EnsureMigrated();

        var speaker = new Speaker
        {
            Id = Ids.NewSpeakerId(),
            DisplayName = "Alice",
            Aliases = [],
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        database.Speakers.CreateSpeaker(speaker);

        var result = _harness.Run("speakers", "list");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Alice", result.Output);
    }

    [Fact]
    public void Speakers_Assign_CreatesManualLockedAssignment()
    {
        var database = new MeetCapDatabase(CommandSupport.DatabasePath(_harness.DataRoot));
        database.EnsureMigrated();

        var speaker = new Speaker
        {
            Id = Ids.NewSpeakerId(),
            DisplayName = "Alice",
            Aliases = [],
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        database.Speakers.CreateSpeaker(speaker);

        var result = _harness.Run(
            "speakers", "assign",
            "--session", "ses_test_1",
            "--label", "speaker_0",
            "--name", "Alice");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Speaker assigned", result.Output);
        Assert.Contains("locked:  yes", result.Output);

        // Verify the assignment was persisted.
        var loaded = database.Speakers.GetAssignment("ses_test_1", "speaker_0");
        Assert.NotNull(loaded);
        Assert.Equal(speaker.Id, loaded!.SpeakerId);
        Assert.True(loaded.Locked);
        Assert.Equal(SpeakerAssignmentSource.Manual, loaded.Source);
    }

    [Fact]
    public void Speakers_Assign_UnknownSpeaker_ReturnsError()
    {
        var database = new MeetCapDatabase(CommandSupport.DatabasePath(_harness.DataRoot));
        database.EnsureMigrated();

        var result = _harness.Run(
            "speakers", "assign",
            "--session", "ses_1",
            "--label", "speaker_0",
            "--name", "Nobody");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("no enrolled speaker", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Speakers_Assign_MissingArgs_ReturnsUsageError()
    {
        var result = _harness.Run("speakers", "assign");
        Assert.NotEqual(0, result.ExitCode);
    }
}
