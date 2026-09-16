using MeetCap.AudioPipeline;
using MeetCap.AudioPipeline.Tests.TestSupport;
using MeetCap.Core.Capture;
using MeetCap.Core.Sessions;
using Xunit;

namespace MeetCap.AudioPipeline.Tests;

/// <summary>
/// The gap audit behind issue #4's acceptance criteria: a discontinuity has to be
/// represented explicitly instead of being hidden by timestamp shifting, and recovery
/// must never claim success while a known gap remains (docs/RELIABILITY.md sections 6
/// and 7).
/// </summary>
public class SessionGapAuditorTests
{
    private static readonly AudioFormat Format = TestAudio.Formats.Mono48kPcm;

    [Fact]
    public void Audit_AContiguousTrack_HasNoGaps()
    {
        using var workspace = new TempWorkspace();
        Add(workspace, sequence: 1, startMs: 0, endMs: 60_000, ChunkStates.Closed);
        Add(workspace, sequence: 2, startMs: 60_000, endMs: 120_000, ChunkStates.Closed);

        var audit = new SessionGapAuditor(workspace.Database).Audit(workspace.SessionId);

        Assert.False(audit.HasGap);
        Assert.False(audit.RecoveryIncomplete);
        Assert.Equal(0, audit.TotalGapMs);
        Assert.Equal("no gaps", audit.Describe());
    }

    [Fact]
    public void Audit_ATimelineHoleBetweenTwoDurableChunks_IsReportedAsNotCaptured()
    {
        using var workspace = new TempWorkspace();
        Add(workspace, sequence: 1, startMs: 0, endMs: 60_000, ChunkStates.Closed);

        // 5 s of the track has no chunk at all, then the recording continued.
        Add(workspace, sequence: 2, startMs: 65_000, endMs: 125_000, ChunkStates.Closed);

        var audit = new SessionGapAuditor(workspace.Database).Audit(workspace.SessionId);

        var gap = Assert.Single(audit.Gaps);
        Assert.Equal(AudioGapReasons.NotCaptured, gap.Reason);
        Assert.Equal(60_000, gap.StartMs);
        Assert.Equal(65_000, gap.EndMs);
        Assert.Equal(5_000, gap.GapMs);
        Assert.True(audit.RecoveryIncomplete);
        Assert.Contains("5000 ms", audit.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Audit_AChunkThatCouldNotBeRepaired_IsClassifiedExplicitly()
    {
        using var workspace = new TempWorkspace();
        Add(workspace, sequence: 1, startMs: 0, endMs: 60_000, ChunkStates.Closed);

        // The bytes are on disk but irrecoverable: recovery must not pretend this stint of
        // audio is available, and must not silently shift the next chunk earlier either.
        // With the corrupt row carrying no usable position, the absence runs from the end
        // of the last durable chunk to the start of the next one — and it is reported once,
        // classified by what caused it.
        Add(workspace, sequence: 2, startMs: 0, endMs: 0, ChunkStates.Corrupt);
        Add(workspace, sequence: 3, startMs: 120_000, endMs: 180_000, ChunkStates.Closed);

        var audit = new SessionGapAuditor(workspace.Database).Audit(workspace.SessionId);

        var gap = Assert.Single(audit.Gaps);
        Assert.Equal(AudioGapReasons.ChunkUnreadable, gap.Reason);
        Assert.Equal(2, gap.Sequence);
        Assert.Equal(60_000, gap.StartMs);
        Assert.Equal(120_000, gap.EndMs);
        Assert.Equal(60_000, gap.GapMs);
        Assert.Equal(1, audit.UnreadableGapCount);
        Assert.Contains("000002=corrupt", gap.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Audit_AHoleAndAnUnreadableChunkInTheSameStretch_AreOneGap()
    {
        using var workspace = new TempWorkspace();
        Add(workspace, sequence: 1, startMs: 0, endMs: 60_000, ChunkStates.Closed);

        // Sequence 2 never reached the index, and sequence 3's bytes are unreadable, so the
        // track has no durable audio from 60 s to 180 s. That is one 120 s absence, not two
        // overlapping ones.
        Add(workspace, sequence: 3, startMs: 120_000, endMs: 180_000, ChunkStates.Corrupt);
        Add(workspace, sequence: 4, startMs: 180_000, endMs: 240_000, ChunkStates.Closed);

        var audit = new SessionGapAuditor(workspace.Database).Audit(workspace.SessionId);

        var gap = Assert.Single(audit.Gaps);
        Assert.Equal(60_000, gap.StartMs);
        Assert.Equal(180_000, gap.EndMs);
        Assert.Equal(120_000, gap.GapMs);
        Assert.Equal(new[] { 2, 3 }, gap.MissingSequences);
    }

    [Fact]
    public void Audit_UnreadableChunksAfterTheLastDurableAudio_ExtendOnlyAsFarAsTheyKnow()
    {
        using var workspace = new TempWorkspace();
        Add(workspace, sequence: 1, startMs: 0, endMs: 60_000, ChunkStates.Closed);
        Add(workspace, sequence: 2, startMs: 60_000, endMs: 120_000, ChunkStates.Corrupt);

        var audit = new SessionGapAuditor(workspace.Database).Audit(workspace.SessionId);

        var gap = Assert.Single(audit.Gaps);
        Assert.Equal(AudioGapReasons.ChunkUnreadable, gap.Reason);
        Assert.Equal(60_000, gap.StartMs);
        Assert.Equal(120_000, gap.EndMs);
    }

    [Fact]
    public void Audit_AMissingIndexedChunk_IsClassifiedAsMissing()
    {
        using var workspace = new TempWorkspace();
        Add(workspace, sequence: 1, startMs: 0, endMs: 60_000, ChunkStates.Closed);
        Add(workspace, sequence: 2, startMs: 60_000, endMs: 120_000, ChunkStates.Missing);

        var audit = new SessionGapAuditor(workspace.Database).Audit(workspace.SessionId);

        var gap = Assert.Single(audit.Gaps);
        Assert.Equal(AudioGapReasons.ChunkMissing, gap.Reason);
        Assert.Contains("000002=missing", gap.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Audit_ASkippedChunkNumberWithoutATimelineHole_IsStillStated()
    {
        using var workspace = new TempWorkspace();
        Add(workspace, sequence: 1, startMs: 0, endMs: 60_000, ChunkStates.Closed);

        // Sequence 2 never reached the index, yet the timeline is contiguous. No audio is
        // provably missing, but the skipped number is what a lost artifact looks like, so
        // it is reported with a zero-length span instead of being dropped on the floor.
        Add(workspace, sequence: 3, startMs: 60_000, endMs: 120_000, ChunkStates.Closed);

        var audit = new SessionGapAuditor(workspace.Database).Audit(workspace.SessionId);

        var gap = Assert.Single(audit.Gaps);
        Assert.Equal(0, gap.GapMs);
        Assert.Equal(new[] { 2 }, gap.MissingSequences);
        Assert.Contains("skipped chunk numbers", gap.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Audit_ReportsEachTrackIndependently()
    {
        using var workspace = new TempWorkspace();
        Add(workspace, sequence: 1, startMs: 0, endMs: 60_000, ChunkStates.Closed, AudioSource.Mic);
        Add(workspace, sequence: 2, startMs: 65_000, endMs: 125_000, ChunkStates.Closed, AudioSource.Mic);

        var audit = new SessionGapAuditor(workspace.Database).Audit(workspace.SessionId);

        var gap = Assert.Single(audit.Gaps);
        Assert.Equal("mic", gap.Source);
    }

    [Fact]
    public void Audit_AnEmptySession_HasNoGaps()
    {
        using var workspace = new TempWorkspace();

        var audit = new SessionGapAuditor(workspace.Database).Audit(workspace.SessionId);

        Assert.False(audit.HasGap);
        Assert.False(audit.RecoveryIncomplete);
    }

    [Fact]
    public void DescribeGaps_StatesWhereTheAudioIsMissing()
    {
        using var workspace = new TempWorkspace();
        Add(workspace, sequence: 1, startMs: 0, endMs: 60_000, ChunkStates.Closed);
        Add(workspace, sequence: 2, startMs: 62_500, endMs: 122_500, ChunkStates.Closed);

        var audit = new SessionGapAuditor(workspace.Database).Audit(workspace.SessionId);

        var line = Assert.Single(audit.DescribeGaps());
        Assert.Contains("mic", line, StringComparison.Ordinal);
        Assert.Contains("60000..62500", line, StringComparison.Ordinal);
        Assert.Contains("2500 ms", line, StringComparison.Ordinal);
        Assert.Contains(AudioGapReasons.NotCaptured, line, StringComparison.Ordinal);
    }

    private static void Add(
        TempWorkspace workspace,
        int sequence,
        long startMs,
        long endMs,
        string status,
        AudioSource source = AudioSource.Mic)
    {
        workspace.Database.Chunks.Upsert(new AudioChunkRecord
        {
            Id = AudioChunkRecord.BuildId(workspace.SessionId, source, sequence),
            SessionId = workspace.SessionId,
            Source = source,
            Sequence = sequence,
            RelativePath = workspace.Paths.RelativeChunkPath(source, sequence),
            StartMs = startMs,
            EndMs = endMs,
            Format = Format,
            ByteLength = (endMs - startMs) * 96,
            Status = status,
            CreatedAt = DateTimeOffset.UnixEpoch,
            ClosedAt = ChunkStates.IsDurable(status) ? DateTimeOffset.UnixEpoch : null,
        });
    }
}
