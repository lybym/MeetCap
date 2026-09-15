using MeetCap.Core.Sessions;
using Xunit;

namespace MeetCap.Core.Tests.Sessions;

public class SessionIdsTests
{
    private static readonly DateTimeOffset Instant = new(2026, 9, 15, 14, 25, 30, TimeSpan.Zero);

    [Fact]
    public void Create_ProducesASortablePrefixedId()
    {
        var id = SessionIds.Create(Instant, new byte[] { 0xab, 0xcd, 0xef, 0x01 });

        Assert.Equal("ses_20260915T142530Z_abcdef01", id);
        Assert.True(SessionIds.IsValid(id));
    }

    [Fact]
    public void Create_ConvertsToUtc()
    {
        var local = new DateTimeOffset(2026, 9, 15, 22, 25, 30, TimeSpan.FromHours(8));

        var id = SessionIds.Create(local, new byte[] { 0, 0, 0, 0 });

        Assert.StartsWith("ses_20260915T142530Z_", id, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_IsUniqueAcrossCalls()
    {
        var ids = Enumerable.Range(0, 32).Select(_ => SessionIds.Create(Instant)).ToHashSet();

        Assert.Equal(32, ids.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ses_")]
    [InlineData("ses_20260915T142530Z_ABCDEF01")]
    [InlineData("20260915T142530Z_abcdef01")]
    [InlineData("ses_20260915X142530Z_abcdef01")]
    public void IsValid_RejectsMalformedIds(string? value)
    {
        Assert.False(SessionIds.IsValid(value));
    }

    [Fact]
    public void SessionStatus_CoversTheM0ActiveSet()
    {
        // The M0 status command counted these four as active; M1 must not change that
        // meaning, only add the interrupted terminal state.
        Assert.Equal(
            new[] { "CREATED", "RECORDING", "FINALIZING", "PROCESSING" },
            SessionStatus.Active);

        Assert.Contains(SessionStatus.Interrupted, SessionStatus.All);
        Assert.DoesNotContain(SessionStatus.Interrupted, SessionStatus.Active);
        Assert.DoesNotContain(SessionStatus.Completed, SessionStatus.Active);
    }

    [Fact]
    public void ChunkStates_IdentifyDurableStates()
    {
        Assert.True(ChunkStates.IsDurable(ChunkStates.Closed));
        Assert.True(ChunkStates.IsDurable(ChunkStates.Recovered));
        Assert.False(ChunkStates.IsDurable(ChunkStates.Open));
        Assert.False(ChunkStates.IsDurable(ChunkStates.Corrupt));
        Assert.False(ChunkStates.IsDurable(ChunkStates.Missing));
    }
}
