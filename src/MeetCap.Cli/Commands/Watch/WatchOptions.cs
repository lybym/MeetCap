namespace MeetCap.Cli.Commands.Watch;

using MeetCap.Core.Configuration;

/// <summary>
/// Effective watch settings derived from the <c>[watch]</c> configuration section, in
/// the units the engine uses (time spans). Derived once at command start so the state
/// machine holds plain values rather than re-reading configuration on every poll.
/// </summary>
internal sealed record WatchOptions(
    bool Enabled,
    IReadOnlyList<string> Processes,
    TimeSpan PollInterval,
    TimeSpan ExitDebounce,
    string CaptureMode,
    bool AutoAsr,
    string TitleTemplate)
{
    /// <summary>Builds the effective options from the <c>[watch]</c> config section.</summary>
    public static WatchOptions FromConfiguration(WatchSection watch)
    {
        ArgumentNullException.ThrowIfNull(watch);

        var processes = watch.Processes
            .Select(ProcessProbe.NormalizeName)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new WatchOptions(
            watch.Enabled,
            processes,
            TimeSpan.FromSeconds(Math.Max(1, watch.PollIntervalSeconds)),
            TimeSpan.FromSeconds(Math.Max(0, watch.ExitDebounceSeconds)),
            watch.CaptureMode,
            watch.AutoAsr,
            watch.TitleTemplate);
    }
}
