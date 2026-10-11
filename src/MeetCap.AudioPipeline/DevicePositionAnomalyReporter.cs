namespace MeetCap.AudioPipeline;

using MeetCap.Core.Sessions;

/// <summary>Bounds repeated clock diagnostics without altering timing or gap events.</summary>
internal sealed class DevicePositionAnomalyReporter
{
    private long? _lastReportedMs;
    private int _pending;
    private SessionEvent? _latest;
    public void Observe(ISessionEventSink events, SessionEvent diagnostic)
    {
        _latest = diagnostic;
        _pending++;
        if (_lastReportedMs is null || diagnostic.AtMs - _lastReportedMs >= 5000) Flush(events);
    }
    public void Flush(ISessionEventSink events)
    {
        if (_pending == 0 || _latest is null) return;
        events.Write(_latest with { Count = _pending, Reason = "device_position_backwards" });
        _lastReportedMs = _latest.AtMs;
        _pending = 0;
    }
}
