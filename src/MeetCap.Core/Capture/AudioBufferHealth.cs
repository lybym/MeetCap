namespace MeetCap.Core.Capture;

/// <summary>
/// The bounded-buffer accounting of one capture track: how deep the producer/consumer
/// queue actually got, how much audio the bounded queue had to drop, and how long a
/// stalled downstream consumer held a backlog.
/// </summary>
/// <remarks>
/// <para>
/// docs/RELIABILITY.md section 4 forbids unbounded in-memory audio queues and requires
/// a degraded event when downstream work cannot keep up. A degradation that is only
/// visible in a log line is not auditable, so the counts are accumulated in the
/// MeetCap-owned domain type and persisted with the session
/// (docs/DATA_MODEL.md section 3) instead of being reconstructed from logs.
/// </para>
/// <para>
/// The type is serialized as the <c>capture_health</c> object of <c>session.json</c> by the
/// same <c>SessionManifestStore</c> path as the rest of the manifest, so its property names
/// <em>are</em> the on-disk names (snake_case). There is deliberately no second hand-written
/// JSON form: two serializers for one record is how a wire name drifts away from what is
/// actually written.
/// </para>
/// <para>
/// This is deliberately a plain snapshot, not a metrics framework: docs/DEVELOPMENT.md
/// section 3 forbids growing a local recorder into distributed infrastructure, and a
/// bounded queue plus explicit counters is the whole reliability property.
/// </para>
/// </remarks>
public sealed record AudioBufferHealth
{
    /// <summary>An unused, healthy accounting record.</summary>
    public static AudioBufferHealth Empty { get; } = new();

    /// <summary>
    /// Queue capacity in packets. The bound, never exceeded by design.
    /// </summary>
    /// <remarks>
    /// Named <c>CapacityPackets</c> rather than <c>Capacity</c> so the persisted key is
    /// <c>capacity_packets</c>: a bare <c>capacity</c> does not say packets, and a reader of
    /// <c>session.json</c> should not have to guess the unit.
    /// </remarks>
    public int CapacityPackets { get; init; }

    /// <summary>Deepest backlog the queue actually reached, in packets.</summary>
    public int PeakQueuedPackets { get; init; }

    /// <summary>Packets the bounded queue refused, because downstream could not keep up.</summary>
    public int DroppedPackets { get; init; }

    /// <summary>How many times the queue overflowed (>= 1 when <see cref="DroppedPackets"/> is > 0).</summary>
    public int OverflowEvents { get; init; }

    /// <summary>Longest observed time the queue stayed non-empty without being drained.</summary>
    public long LongestStallMs { get; init; }

    /// <summary>How many discrete stalled-consumer periods were observed.</summary>
    public int StallEvents { get; init; }

    /// <summary>Audio time the capture timeline knows is missing, in milliseconds.</summary>
    public long GapTotalMs { get; init; }

    /// <summary>How many discontinuities produced that missing time.</summary>
    public int GapCount { get; init; }

    /// <summary>
    /// True when the session reported a gap, an overflow or a stalled consumer.
    /// </summary>
    /// <remarks>
    /// Computed, but serialized with the record as well: the verdict is part of what an
    /// operator or a later milestone reads out of a finished session, and re-deriving it
    /// from four counters is exactly the kind of inference docs/RELIABILITY.md section 7
    /// wants to avoid.
    /// </remarks>
    public bool IsDegraded
        => DroppedPackets > 0 || OverflowEvents > 0 || StallEvents > 0 || GapCount > 0;
}

/// <summary>
/// Live accounting for one bounded producer/consumer queue.
/// </summary>
/// <remarks>
/// <para>
/// The capture callback only writes; the consumer only reads. This type is what turns
/// those two counters into the evidence docs/RELIABILITY.md section 4 asks for: the
/// bound that was configured, the deepest backlog actually reached, how much audio the
/// bound had to drop, and how long a stalled consumer held a backlog.
/// </para>
/// <para>
/// Written from both threads, so the counters are updated with interlocked operations and
/// the snapshot is read for reporting only.
/// </para>
/// </remarks>
public sealed class CaptureBacklogMonitor
{
    private readonly int _capacity;
    private long _produced;
    private long _consumed;
    private int _dropped;
    private int _overflowEvents;
    private int _peakQueued;
    private int _stallEvents;
    private long _currentStallMs;
    private long _longestStallMs;

    public CaptureBacklogMonitor(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Queue capacity must be positive.");
        }

        _capacity = capacity;
    }

    /// <summary>The configured bound, in packets.</summary>
    public int Capacity => _capacity;

    /// <summary>Packets accepted into the queue.</summary>
    public long Produced => Interlocked.Read(ref _produced);

    /// <summary>Packets the consumer has taken out of the queue.</summary>
    public long Consumed => Interlocked.Read(ref _consumed);

    /// <summary>Packets currently in the queue.</summary>
    public int Queued => (int)Math.Clamp(Produced - Consumed, 0, _capacity);

    /// <summary>Packets the bound refused.</summary>
    public int Dropped => Volatile.Read(ref _dropped);

    /// <summary>How many times the bound refused at least one packet.</summary>
    public int OverflowEvents => Volatile.Read(ref _overflowEvents);

    /// <summary>Deepest backlog observed, in packets.</summary>
    public int PeakQueued => Volatile.Read(ref _peakQueued);

    /// <summary>How many distinct stalled-consumer periods were observed.</summary>
    public int StallEvents => Volatile.Read(ref _stallEvents);

    /// <summary>Longest stalled period observed, in milliseconds.</summary>
    public long LongestStallMs => Interlocked.Read(ref _longestStallMs);

    /// <summary>Records one packet accepted by the bounded queue.</summary>
    public void RecordProduced()
    {
        Interlocked.Increment(ref _produced);
        var queued = Queued;
        UpdatePeak(queued);
    }

    /// <summary>Records one packet the bounded queue refused.</summary>
    public void RecordDropped()
    {
        Interlocked.Increment(ref _dropped);
        Interlocked.Increment(ref _overflowEvents);
    }

    /// <summary>Records that the consumer drained one packet.</summary>
    public void RecordConsumed() => Interlocked.Increment(ref _consumed);

    /// <summary>
    /// Records one stall observation of <paramref name="stalledMs"/> milliseconds.
    /// A non-zero value continues the current stalled period; a zero value ends it.
    /// </summary>
    public void RecordStallObservation(long stalledMs)
    {
        if (stalledMs <= 0)
        {
            Interlocked.Exchange(ref _currentStallMs, 0);
            return;
        }

        if (Interlocked.Exchange(ref _currentStallMs, 0) == 0)
        {
            Interlocked.Increment(ref _stallEvents);
        }

        Interlocked.Exchange(ref _currentStallMs, stalledMs);
        UpdateLongest(stalledMs);
    }

    /// <summary>A consistent snapshot for a report or the session manifest.</summary>
    public AudioBufferHealth Snapshot(long gapTotalMs, int gapCount)
        => new()
        {
            CapacityPackets = _capacity,
            PeakQueuedPackets = PeakQueued,
            DroppedPackets = Dropped,
            OverflowEvents = OverflowEvents,
            LongestStallMs = LongestStallMs,
            StallEvents = StallEvents,
            GapTotalMs = gapTotalMs,
            GapCount = gapCount,
        };

    private void UpdatePeak(int queued)
    {
        var current = Volatile.Read(ref _peakQueued);
        while (queued > current)
        {
            var observed = Interlocked.CompareExchange(ref _peakQueued, queued, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }

    private void UpdateLongest(long stalledMs)
    {
        var current = Interlocked.Read(ref _longestStallMs);
        while (stalledMs > current)
        {
            var observed = Interlocked.CompareExchange(ref _longestStallMs, stalledMs, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}
