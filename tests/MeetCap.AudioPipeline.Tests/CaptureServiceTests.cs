using MeetCap.AudioPipeline.Tests.TestSupport;
using MeetCap.Core.Capture;
using MeetCap.Core.Sessions;
using Xunit;

namespace MeetCap.AudioPipeline.Tests;

public class CaptureServiceTests
{
    private static readonly AudioFormat Format = TestAudio.Formats.Mono48kPcm;

    [Fact]
    public void ListDevices_OrdersTheDefaultFirst()
    {
        using var harness = new SessionHarness();
        harness.Devices.Replace(
            new CaptureDeviceInfo("b", "Zulu Mic", false),
            new CaptureDeviceInfo("a", "Alpha Mic", true),
            new CaptureDeviceInfo("c", "Mike Mic", false));

        var devices = harness.Service.ListDevices();

        Assert.Equal(new[] { "Alpha Mic", "Mike Mic", "Zulu Mic" }, devices.Select(d => d.DisplayName));
    }

    [Fact]
    public void ResolveDevice_UsesTheConfiguredEndpoint()
    {
        using var harness = new SessionHarness(microphoneDeviceId: "mic-default");

        Assert.Equal("mic-default", harness.Service.ResolveDevice().Id);
    }

    [Fact]
    public void FindActiveSession_ReturnsNullWhenNothingIsRecording()
    {
        using var workspace = new TempWorkspace(sessionStatus: SessionStatus.Completed);
        using var harness = new SessionHarness(workspace: workspace);

        Assert.Null(harness.Service.FindActiveSession());
    }

    [Fact]
    public void FindActiveSession_ReturnsTheNewestOpenSession()
    {
        using var harness = new SessionHarness();
        harness.Workspace.Database.Sessions.Insert(new SessionRecord
        {
            Id = "ses_20260915T150000Z_0000000b",
            Title = "Later",
            Mode = SessionModes.Offline,
            SourceType = SessionSourceTypes.Live,
            Status = SessionStatus.Recording,
            ConfigVersion = 1,
            Tracks = new[] { AudioSources.Mic },
            CreatedAt = new DateTimeOffset(2026, 9, 15, 15, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 9, 15, 15, 0, 0, TimeSpan.Zero),
        });

        var active = harness.Service.FindActiveSession();

        Assert.NotNull(active);
        Assert.Equal("ses_20260915T150000Z_0000000b", active.SessionId);
        Assert.Equal("Later", active.Title);
    }

    [Fact]
    public void RequestStop_WithoutAnActiveSession_ReportsFailure()
    {
        using var workspace = new TempWorkspace(sessionStatus: SessionStatus.Completed);
        using var harness = new SessionHarness(workspace: workspace);

        var outcome = harness.Service.RequestStop(TimeSpan.FromMilliseconds(50));

        Assert.False(outcome.Signalled);
        Assert.Null(outcome.SessionId);
        Assert.Contains("no active recording session", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestStop_WhenTheSessionDirectoryIsGone_ReportsFailure()
    {
        using var harness = new SessionHarness();
        Directory.Delete(harness.Workspace.Paths.SessionDirectory, recursive: true);

        var outcome = harness.Service.RequestStop(TimeSpan.FromMilliseconds(50));

        Assert.False(outcome.Signalled);
        Assert.Equal(harness.Workspace.SessionId, outcome.SessionId);
        Assert.Contains("meetcap status", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestStop_WritesTheMarkerAndWaitsForTheRecorderToFinish()
    {
        using var harness = new SessionHarness();
        var paths = harness.Workspace.Paths;
        var signal = new SessionStopSignal(paths.StopRequestPath);

        // Stand in for the recording process: it notices the marker and finalizes.
        var recorder = Task.Run(async () =>
        {
            while (!signal.IsRequested())
            {
                await Task.Delay(20).ConfigureAwait(false);
            }

            harness.Workspace.Database.Sessions.UpdateLifecycle(
                harness.Workspace.SessionId,
                SessionStatus.Completed,
                harness.Clock.UtcNow,
                harness.Clock.UtcNow,
                1_000);
        });

        var outcome = harness.Service.RequestStop(TimeSpan.FromSeconds(5));
        await recorder;

        Assert.True(outcome.Signalled);
        Assert.True(outcome.ConfirmedStopped);
        Assert.Equal(harness.Workspace.SessionId, outcome.SessionId);
    }

    [Fact]
    public void RequestStop_TimesOutWithoutFailingWhenTheRecorderIsStuck()
    {
        using var harness = new SessionHarness();

        var outcome = harness.Service.RequestStop(TimeSpan.FromMilliseconds(300));

        // The marker was written, so the recorder will stop when it next checks; the
        // command simply could not confirm it in time.
        Assert.True(outcome.Signalled);
        Assert.False(outcome.ConfirmedStopped);
        Assert.True(new SessionStopSignal(harness.Workspace.Paths.StopRequestPath).IsRequested());
    }

    [Fact]
    public void RunStartupRecovery_ScansTheConfiguredDataRoot()
    {
        using var harness = new SessionHarness();
        harness.Workspace.Database.Sessions.UpdateLifecycle(
            harness.Workspace.SessionId,
            SessionStatus.Recording,
            harness.Clock.UtcNow,
            null,
            0);

        var report = harness.Service.RunStartupRecovery();

        Assert.Equal(1, report.RecoveredSessions);
        Assert.Equal(SessionStatus.Interrupted, harness.Workspace.Database.Sessions.Find(harness.Workspace.SessionId)!.Status);
    }

    [Fact]
    public void PrepareSession_UsesTheResolvedDeviceAndConfiguredChunkLength()
    {
        using var harness = new SessionHarness(chunkSeconds: 45);
        var source = new FakeCaptureSource(Format, harness.Device);
        harness.Sources.Enqueue(source);

        var session = harness.Service.PrepareSession("Chunked");

        Assert.StartsWith("ses_", session.SessionId, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(session.SessionDirectory, SessionPaths.ManifestFileName)));

        var stored = harness.Database.Sessions.Find(session.SessionId)!;
        Assert.Equal("Chunked", stored.Title);
        Assert.Contains("\"chunk_seconds\":45", stored.ConfigSnapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void OrderDevices_HandlesAnEmptyList()
    {
        Assert.Empty(CaptureService.OrderDevices(Array.Empty<CaptureDeviceInfo>()));
    }
}
