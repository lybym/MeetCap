namespace MeetCap.Core.Asr;

/// <summary>
/// Estimates provider cost for a job from its duration and the configured hourly
/// rate (<c>docs/ASR_STRATEGY.md</c> sections 2 and 13).
/// </summary>
/// <remarks>
/// This is an estimate for local accounting only. Public provider pricing is
/// provider configuration, not a product guarantee, which is why the rate is a
/// configuration value rather than a constant.
/// </remarks>
public static class AsrCostEstimator
{
    /// <summary>Estimated cost in CNY for <paramref name="durationMs"/> at <paramref name="costPerHourCny"/>.</summary>
    public static double Estimate(long durationMs, double costPerHourCny)
    {
        if (durationMs <= 0 || costPerHourCny <= 0)
        {
            return 0;
        }

        return Math.Round(durationMs / 3_600_000.0 * costPerHourCny, 6, MidpointRounding.ToEven);
    }
}
