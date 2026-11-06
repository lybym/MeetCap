using Xunit;

namespace MeetCap.Cli.Tests;

/// <summary>
/// Command-level tests for <c>meetcap watch</c>. The daemon loop is covered by the
/// watcher state-machine tests with an injected probe; these tests cover the opt-in gate,
/// configuration validation, and command-tree wiring — the paths that must exit promptly
/// without starting a real polling loop.
/// </summary>
public class WatchCommandTests
{
    [Fact]
    public void RootHelp_ListsWatchCommand()
    {
        using var harness = CliHarness.Create();

        var result = harness.Run("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("watch", result.Output);
    }

    [Fact]
    public void Watch_DisabledByDefault_RefusesWithActionableMessage()
    {
        using var harness = CliHarness.Create();
        harness.WriteCaptureConfig();

        var result = harness.Run("watch");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("[watch] enabled is false", result.Error);
        Assert.Contains("non-meeting audio", result.Error);
    }

    [Fact]
    public void Watch_EnabledWithoutProcesses_RefusesWithActionableMessage()
    {
        using var harness = CliHarness.Create();
        harness.WriteConfig(
            "config_version = 1\n" +
            "\n" +
            "[app]\ndefault_title = \"Untitled Meeting\"\n" +
            "\n" +
            "[capture]\ndefault_mode = \"offline\"\nchunk_seconds = 1\nbuffer_seconds = 5\nflush_interval_ms = 200\n" +
            "\n" +
            "[capture.offline]\nmicrophone_device_id = \"default\"\n" +
            "\n" +
            "[storage]\ndata_root = '" + harness.DataRoot + "'\nminimum_free_space_gb = 1\n" +
            "\n" +
            "[asr]\nenabled = false\n" +
            "\n" +
            "[watch]\nenabled = true\nprocesses = []\n");

        var result = harness.Run("watch");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("processes is empty", result.Error);
    }

    [Fact]
    public void Watch_EnabledWithInvalidCaptureMode_Refuses()
    {
        using var harness = CliHarness.Create();
        harness.WriteConfig(
            "config_version = 1\n" +
            "\n" +
            "[app]\ndefault_title = \"Untitled Meeting\"\n" +
            "\n" +
            "[capture]\ndefault_mode = \"offline\"\nchunk_seconds = 1\nbuffer_seconds = 5\nflush_interval_ms = 200\n" +
            "\n" +
            "[capture.offline]\nmicrophone_device_id = \"default\"\n" +
            "\n" +
            "[storage]\ndata_root = '" + harness.DataRoot + "'\nminimum_free_space_gb = 1\n" +
            "\n" +
            "[asr]\nenabled = false\n" +
            "\n" +
            "[watch]\nenabled = true\nprocesses = [\"WeMeetApp\"]\ncapture_mode = \"hybrid\"\n");

        var result = harness.Run("watch");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("watch.capture_mode", result.Error);
    }

    [Fact]
    public void ConfigInit_WritesWatchSectionWithDefaults()
    {
        using var harness = CliHarness.Create();

        Assert.Equal(0, harness.Run("config", "init").ExitCode);

        var config = File.ReadAllText(harness.ConfigFilePath);
        Assert.Contains("[watch]", config);
        Assert.Contains("enabled = false", config);
        Assert.Contains("WeMeetApp", config);
        Assert.Contains("CloudVideo", config);
        Assert.Contains("auto_asr = false", config);
        Assert.Contains("exit_debounce_seconds = 10", config);
    }

    [Fact]
    public void ConfigValidate_AcceptsWatchSection()
    {
        using var harness = CliHarness.Create();
        harness.WriteConfig(
            "config_version = 1\n" +
            "\n" +
            "[app]\ndefault_title = \"Untitled Meeting\"\n" +
            "\n" +
            "[capture]\ndefault_mode = \"offline\"\nchunk_seconds = 1\nbuffer_seconds = 5\nflush_interval_ms = 200\n" +
            "\n" +
            "[capture.offline]\nmicrophone_device_id = \"default\"\n" +
            "\n" +
            "[storage]\ndata_root = '" + harness.DataRoot + "'\nminimum_free_space_gb = 1\n" +
            "\n" +
            "[asr]\nenabled = false\n" +
            "\n" +
            "[watch]\nenabled = false\nprocesses = [\"WeMeetApp\", \"CloudVideo\"]\npoll_interval_seconds = 5\ncapture_mode = \"online\"\nauto_asr = false\nexit_debounce_seconds = 10\ntitle_template = \"{app} {start:yyyy-MM-dd HH-mm-ss}\"\n");

        var result = harness.Run("config", "validate");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Configuration valid", result.Output);
    }
}
