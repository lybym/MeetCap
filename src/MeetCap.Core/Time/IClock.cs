namespace MeetCap.Core.Time;

/// <summary>
/// Wall-clock abstraction. Injected so timing-dependent recording behavior (flush
/// intervals, periodic disk checks) is deterministic under test.
/// </summary>
/// <remarks>
/// This clock is deliberately NOT the capture timeline source. Session-relative
/// audio timing is derived from the device position/format exposed by the capture
/// boundary (docs/ARCHITECTURE.md section 8), never from wall-clock reads.
/// </remarks>
public interface IClock
{
    /// <summary>The current UTC time.</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>The production <see cref="IClock"/>.</summary>
public sealed class SystemClock : IClock
{
    /// <summary>Shared instance; the type is stateless.</summary>
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
