namespace MeetCap.AudioPipeline;

using MeetCap.Core.Capture;
using MeetCap.Core.Sessions;
using MeetCap.Persistence.Storage;

/// <summary>
/// Derives the missing stretches of a session's timeline from its durable chunk index
/// (docs/RELIABILITY.md section 7).
/// </summary>
/// <remarks>
/// <para>
/// Recovery needs this because the live recording can only report the discontinuities it
/// was alive to see. The audit is the other half: it reads the artifacts that survived and
/// states what is provably not there — a stretch between two durable chunks, a chunk whose
/// bytes could not be repaired, or a chunk number that never reached disk. Nothing here is
/// inferred from a log line, so the result is reproducible from <c>meetcap.db</c> plus the
/// audio directory alone.
/// </para>
/// <para>
/// The audit never mutates anything, so it is safe to run from any command. It is
/// deliberately a different mechanism from the live timeline's gap accounting
/// (<see cref="MeetCap.Core.Capture.CaptureTimeline"/>, which measures discontinuities as
/// they happen): the two answer the same question from different evidence, and recovery
/// uses the one that does not depend on the process having stayed alive.
/// </para>
/// </remarks>
public sealed class SessionGapAuditor
{
    private readonly MeetCapDatabase _database;

    public SessionGapAuditor(MeetCapDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    /// <summary>Audits one session, reading its chunk index at most once.</summary>
    public SessionAudit Audit(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        IReadOnlyList<AudioChunkRecord> chunks;
        try
        {
            chunks = _database.Chunks.ListForSession(sessionId);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            // An unreadable index means the audit cannot be honest about gaps, so it
            // reports the problem instead of silently returning "no gaps".
            return new SessionAudit(sessionId, Array.Empty<AudioGap>(), new[] { ex.Message });
        }

        return Audit(sessionId, chunks);
    }

    /// <summary>Audits an already-read chunk index.</summary>
    public static SessionAudit Audit(string sessionId, IReadOnlyList<AudioChunkRecord> chunks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(chunks);

        var gaps = new List<AudioGap>();
        var ordered = chunks
            .OrderBy(c => c.Source.ToWireName(), StringComparer.Ordinal)
            .ThenBy(c => c.Sequence)
            .ToList();

        foreach (var track in ordered.GroupBy(c => c.Source.ToWireName(), StringComparer.Ordinal))
        {
            AuditTrack(track.Key, track.ToList(), gaps);
        }

        gaps.Sort((left, right) =>
        {
            var bySource = string.CompareOrdinal(left.Source, right.Source);
            return bySource != 0 ? bySource : left.StartMs.CompareTo(right.StartMs);
        });

        return new SessionAudit(sessionId, gaps, Array.Empty<string>());
    }

    private static void AuditTrack(string source, List<AudioChunkRecord> chunks, List<AudioGap> gaps)
    {
        var lastDurableEnd = 0L;
        var haveDurable = false;
        var previousSequence = 0;
        var pendingUnreadable = new List<AudioChunkRecord>();
        var pendingMissing = new List<int>();

        foreach (var chunk in chunks)
        {
            var missing = MissingBetween(previousSequence, chunk.Sequence);
            previousSequence = chunk.Sequence;

            if (!ChunkStates.IsDurable(chunk.Status))
            {
                pendingUnreadable.Add(chunk);
                pendingMissing.AddRange(missing);
                continue;
            }

            // A run of unreadable chunks and any timeline hole that follows it are one
            // absence of audio, not several: the track is silent from the end of the last
            // durable chunk until this one starts. Reporting them as separate overlapping
            // gaps would misstate how much audio is missing.
            var gapStart = haveDurable ? lastDurableEnd : 0;
            var hasHole = chunk.StartMs > gapStart;
            var skipped = pendingMissing.Concat(missing).Distinct().OrderBy(s => s).ToList();
            if (hasHole || pendingUnreadable.Count > 0 || skipped.Count > 0)
            {
                gaps.Add(BuildGap(source, chunk, gapStart, pendingUnreadable, skipped, hasHole));
            }

            lastDurableEnd = Math.Max(lastDurableEnd, chunk.EndMs);
            haveDurable = true;
            pendingUnreadable.Clear();
            pendingMissing.Clear();
        }

        // Unreadable chunks with no durable chunk after them: the session's timeline ends
        // where the last durable audio ended, and the rest of the track never arrived.
        if (pendingUnreadable.Count > 0 || pendingMissing.Count > 0)
        {
            var start = haveDurable ? lastDurableEnd : 0;
            var end = pendingUnreadable.Count > 0
                ? Math.Max(pendingUnreadable[^1].EndMs, start)
                : start;
            var sequences = pendingUnreadable.Select(c => c.Sequence)
                .Concat(pendingMissing)
                .Distinct()
                .OrderBy(s => s)
                .ToList();

            gaps.Add(new AudioGap(
                sequences[0],
                source,
                start,
                end,
                pendingUnreadable.Count > 0 ? ReasonFor(pendingUnreadable) : AudioGapReasons.NotCaptured,
                pendingUnreadable.Count > 0
                    ? DetailFor(pendingUnreadable)
                    : SkippedSequencesDetail)
            {
                MissingSequences = sequences,
            });
        }
    }

    private static AudioGap BuildGap(
        string source,
        AudioChunkRecord next,
        long gapStart,
        List<AudioChunkRecord> unreadable,
        List<int> missing,
        bool hasHole)
    {
        // The whole stretch with no durable audio, classified by what caused it: a chunk
        // that could not be repaired, a timeline hole, or a chunk number that never
        // reached disk.
        var reason = unreadable.Count > 0 ? ReasonFor(unreadable) : AudioGapReasons.NotCaptured;
        var sequences = unreadable.Select(c => c.Sequence).Concat(missing).Distinct().OrderBy(s => s).ToList();

        return new AudioGap(
            sequences.Count > 0 ? sequences[0] : next.Sequence,
            source,
            gapStart,
            Math.Max(next.StartMs, gapStart),
            reason,
            unreadable.Count > 0 ? DetailFor(unreadable) : hasHole ? HoleDetail : SkippedSequencesDetail)
        {
            MissingSequences = sequences,
        };
    }

    private const string HoleDetail = "no durable chunk covers this stretch of the track timeline.";

    private const string SkippedSequencesDetail =
        "the track skipped chunk numbers without leaving a timeline hole.";

    private static string ReasonFor(List<AudioChunkRecord> unreadable)
        => unreadable.All(c => string.Equals(c.Status, ChunkStates.Missing, StringComparison.Ordinal))
            ? AudioGapReasons.ChunkMissing
            : AudioGapReasons.ChunkUnreadable;

    private static string DetailFor(List<AudioChunkRecord> unreadable)
    {
        var statuses = unreadable
            .Select(c => $"{c.Sequence:D6}={c.Status}")
            .Distinct(StringComparer.Ordinal);

        return "chunk(s) " + string.Join(", ", statuses) +
               " are not durable audio, so this stretch of the track timeline has no readable bytes.";
    }

    private static IReadOnlyList<int> MissingBetween(int previousSequence, int sequence)
    {
        if (previousSequence <= 0 || sequence <= previousSequence + 1)
        {
            return Array.Empty<int>();
        }

        var missing = new List<int>(sequence - previousSequence - 1);
        for (var candidate = previousSequence + 1; candidate < sequence; candidate++)
        {
            missing.Add(candidate);
        }

        return missing;
    }
}
