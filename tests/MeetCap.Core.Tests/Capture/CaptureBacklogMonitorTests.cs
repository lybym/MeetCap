using MeetCap.Core.Capture;
using Xunit;

namespace MeetCap.Core.Tests.Capture;

/// <summary>
/// docs/RELIABILITY.md section 4 requires every queue to have an explicit bound and a
/// degraded signal when downstream work cannot keep up. These tests are about the
/// accounting that makes that requirement observable: the bound, the deepest backlog,
/// the packets the bound refused, and how long a stalled consumer held a backlog.
/// </summary>
public class CaptureBacklogMonitorTests
{
    [Fact]
    public void Queued_NeverExceedsTheConfiguredBound()
    {
        var monitor = new CaptureBacklogMonitor(capacity: 5);

        for (var i = 0; i < 100; i++)
        {
            monitor.RecordProduced();
        }

        // The monitor reports the queue, not the producer's intent: a producer that keeps
        // producing into a full queue is exactly the drop case.
        Assert.Equal(5, monitor.Queued);
        Assert.Equal(5, monitor.PeakQueued);
        Assert.Equal(100, monitor.Produced);
    }

    [Fact]
    public void Queued_FallsAsTheConsumerDrains()
    {
        var monitor = new CaptureBacklogMonitor(capacity: 10);

        monitor.RecordProduced();
        monitor.RecordProduced();
        monitor.RecordProduced();
        monitor.RecordConsumed();

        Assert.Equal(2, monitor.Queued);
        Assert.Equal(3, monitor.PeakQueued);
    }

    [Fact]
    public void RecordDropped_CountsBothThePacketsAndTheOverflowEvents()
    {
        var monitor = new CaptureBacklogMonitor(capacity: 2);

        monitor.RecordDropped();
        monitor.RecordDropped();
        monitor.RecordProduced();
        monitor.RecordDropped();

        Assert.Equal(3, monitor.Dropped);
        Assert.Equal(3, monitor.OverflowEvents);
        Assert.Equal(1, monitor.Produced);
    }

    [Fact]
    public void RecordStallObservation_CountsOneStallEventPerStalledPeriod()
    {
        var monitor = new CaptureBacklogMonitor(capacity: 10);

        // A stalled period is a run of non-zero observations. The longest observation in
        // the run is the reported length, and the run counts once.
        monitor.RecordStallObservation(250);
        monitor.RecordStallObservation(500);
        monitor.RecordStallObservation(750);

        Assert.Equal(1, monitor.StallEvents);
        Assert.Equal(750, monitor.LongestStallMs);

        // A drained queue ends the period...
        monitor.RecordStallObservation(0);
        Assert.Equal(1, monitor.StallEvents);

        // ...so a later stall is a new event.
        monitor.RecordStallObservation(300);
        Assert.Equal(2, monitor.StallEvents);
        Assert.Equal(750, monitor.LongestStallMs);
    }

    [Fact]
    public void Snapshot_CarriesTheBoundTheGapAccountingAndTheDegradedVerdict()
    {
        var monitor = new CaptureBacklogMonitor(capacity: 64);
        monitor.RecordProduced();
        monitor.RecordConsumed();
        monitor.RecordStallObservation(1_500);

        var snapshot = monitor.Snapshot(gapTotalMs: 2_000, gapCount: 1);

        Assert.Equal(64, snapshot.CapacityPackets);
        Assert.Equal(1, snapshot.PeakQueuedPackets);
        Assert.Equal(0, snapshot.DroppedPackets);
        Assert.Equal(1, snapshot.StallEvents);
        Assert.Equal(1_500, snapshot.LongestStallMs);
        Assert.Equal(2_000, snapshot.GapTotalMs);
        Assert.Equal(1, snapshot.GapCount);
        Assert.True(snapshot.IsDegraded);
    }

    [Fact]
    public void EmptyHealth_IsNotDegraded()
    {
        Assert.False(AudioBufferHealth.Empty.IsDegraded);
        Assert.Equal(0, AudioBufferHealth.Empty.CapacityPackets);
    }

    [Fact]
    public void Constructor_RejectsANonPositiveBound()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureBacklogMonitor(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureBacklogMonitor(-1));
    }
}
