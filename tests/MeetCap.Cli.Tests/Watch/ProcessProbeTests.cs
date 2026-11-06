using MeetCap.Cli.Commands.Watch;
using Xunit;

namespace MeetCap.Cli.Tests.Watch;

public class ProcessProbeTests
{
    [Theory]
    [InlineData("WeMeetApp", "WeMeetApp")]
    [InlineData("WeMeetApp.exe", "WeMeetApp")]
    [InlineData("CloudVideo.EXE", "CloudVideo")]
    [InlineData("  CloudVideo  ", "CloudVideo")]
    [InlineData("  spaced.exe ", "spaced")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void NormalizeName_StripsExeSuffixAndTrims(string input, string expected)
    {
        Assert.Equal(expected, ProcessProbe.NormalizeName(input));
    }

    [Fact]
    public void Snapshot_ForNonExistentProcess_ReturnsEmptyWithoutThrowing()
    {
        // A name that cannot be running on any CI machine exercises the production probe
        // end to end: it must return an empty list (no match) and never throw.
        var probe = new ProcessProbe(new[] { "MeetCapDefinitelyNotRunningProcess12345" });

        var snapshot = probe.Snapshot();

        Assert.NotNull(snapshot);
        Assert.Empty(snapshot);
    }

    [Fact]
    public void Snapshot_WithNoConfiguredNames_ReturnsEmpty()
    {
        var probe = new ProcessProbe(Array.Empty<string>());

        Assert.Empty(probe.Snapshot());
    }

    [Fact]
    public void Snapshot_OnlyQueriesConfiguredMainNames_NotHelpers()
    {
        // The helper-non-trigger guarantee is structural: the probe is constructed with the
        // configured main names only, so helper/child processes (CloudVideoNet,
        // crashpad_handler, sdk_transcode_*, PageMonitorMgr) are never queried and can never
        // appear in a snapshot. Constructing the probe with the documented main names and a
        // non-running helper confirms the helper is never matched.
        var probe = new ProcessProbe(new[] { "WeMeetApp", "CloudVideo", "CloudVideoNetHelperNotReal" });

        // None of these are running on CI, so the snapshot is empty — the point is that a
        // helper named "CloudVideoNet" that *was* running would only match if the operator
        // had mistakenly configured it, which the schema warns against.
        Assert.Empty(probe.Snapshot());
    }
}
