using MeetCap.Core.Sessions;
using Xunit;

namespace MeetCap.AudioPipeline.Tests;

public class DevicePositionAnomalyReporterTests
{
    [Fact]
    public void RepeatedDiagnostics_AreBoundedAndCountedIncludingTheStopTail()
    {
        var reporter = new DevicePositionAnomalyReporter();
        var sink = new Sink();
        for (var i = 0; i < 10000; i++)
            reporter.Observe(sink, new SessionEvent(SessionEventNames.CaptureDiscontinuity, i * 10)
            { Source = "loopback", DevicePositionFrames = i, QpcPositionTicks = i * 100000, Detail = "backwards" });
        reporter.Flush(sink);
        reporter.Flush(sink);
        Assert.Equal(10000, sink.Events.Sum(e => e.Count));
        Assert.InRange(sink.Events.Count, 20, 21);
        Assert.All(sink.Events, e => Assert.Equal("device_position_backwards", e.Reason));
        Assert.Equal(99990, sink.Events[^1].AtMs);
        Assert.Equal(9999, sink.Events[^1].DevicePositionFrames);
    }
    private sealed class Sink : ISessionEventSink
    {
        public List<SessionEvent> Events { get; } = [];
        public void Write(SessionEvent evt) => Events.Add(evt);
    }
}
