namespace MeetCap.Core.Asr;

/// <summary>
/// Retry policy for transient ASR failures, derived from
/// <c>[asr] retry_max_attempts / retry_initial_seconds / retry_max_seconds</c>.
/// </summary>
/// <remarks>
/// This governs the persistent job state machine. Polly applies the same
/// configured budget to in-process HTTP retries; the two are separate by design
/// (<c>docs/ASR_STRATEGY.md</c> section 6).
/// </remarks>
public sealed record AsrRetryPolicy(int MaxAttempts, int InitialSeconds, int MaxSeconds)
{
    public const int DefaultMaxAttempts = 8;
    public const int DefaultInitialSeconds = 5;
    public const int DefaultMaxSeconds = 300;

    public static readonly AsrRetryPolicy Default = new(
        DefaultMaxAttempts,
        DefaultInitialSeconds,
        DefaultMaxSeconds);

    /// <summary>
    /// Whether another attempt is allowed after <paramref name="attemptCount"/>
    /// failed attempts (the attempt that just failed is already counted).
    /// </summary>
    public bool CanRetry(int attemptCount) => attemptCount < MaxAttempts;

    /// <summary>Exponential backoff with the configured cap.</summary>
    public TimeSpan DelayFor(int attemptCount)
    {
        var exponent = Math.Max(0, Math.Min(attemptCount, 30));
        var seconds = InitialSeconds * Math.Pow(2, exponent);
        if (double.IsInfinity(seconds) || seconds > MaxSeconds)
        {
            seconds = MaxSeconds;
        }

        if (seconds < InitialSeconds)
        {
            seconds = InitialSeconds;
        }

        return TimeSpan.FromSeconds(seconds);
    }
}
