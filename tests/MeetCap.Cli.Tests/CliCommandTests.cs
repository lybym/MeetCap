using System.CommandLine;
using Xunit;

namespace MeetCap.Cli.Tests;

/// <summary>
/// Integration coverage for the System.CommandLine command hierarchy required by
/// Issue #2: command dispatch, option parsing, usage errors, and help behavior.
/// </summary>
public class CliCommandTests
{
    [Fact]
    public void ConfigInit_WritesDefaultConfigAndReportsPath()
    {
        using var harness = CliHarness.Create();

        var result = harness.Run("config", "init");

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(harness.ConfigFilePath));
        Assert.Contains("Wrote default configuration to", result.Output);
    }

    [Fact]
    public void ConfigInit_SecondRunFails()
    {
        using var harness = CliHarness.Create();

        Assert.Equal(0, harness.Run("config", "init").ExitCode);
        var second = harness.Run("config", "init");

        Assert.Equal(1, second.ExitCode);
        Assert.Contains("already exists", second.Error);
    }

    [Fact]
    public void ConfigInit_ForceOptionOverwrites()
    {
        using var harness = CliHarness.Create();

        Assert.Equal(0, harness.Run("config", "init").ExitCode);
        harness.WriteConfig("# locally edited\n");

        var forced = harness.Run("config", "init", "--force");

        Assert.Equal(0, forced.ExitCode);
        Assert.DoesNotContain("# locally edited", File.ReadAllText(harness.ConfigFilePath));
    }

    [Fact]
    public void ConfigInit_ShortForceAliasIsParsed()
    {
        using var harness = CliHarness.Create();

        Assert.Equal(0, harness.Run("config", "init").ExitCode);
        Assert.Equal(0, harness.Run("config", "init", "-f").ExitCode);
    }

    [Fact]
    public void ConfigPath_PrintsConfigFilePath()
    {
        using var harness = CliHarness.Create();

        var result = harness.Run("config", "path");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(harness.ConfigFilePath, result.Output.Trim());
    }

    [Fact]
    public void ConfigValidate_DefaultConfigIsValid()
    {
        using var harness = CliHarness.Create();
        Assert.Equal(0, harness.Run("config", "init").ExitCode);

        var result = harness.Run("config", "validate");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Configuration valid", result.Output);
    }

    [Fact]
    public void ConfigValidate_InvalidValueReportsActionableError()
    {
        using var harness = CliHarness.Create();
        harness.WriteConfig("[capture]\ndefault_mode = \"hybrid\"\n");

        var result = harness.Run("config", "validate");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("capture.default_mode", result.Error);
    }

    [Fact]
    public void ConfigValidate_MissingConfigReportsLoadError()
    {
        using var harness = CliHarness.Create();

        var result = harness.Run("config", "validate");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Configuration file not found", result.Error);
    }

    [Fact]
    public void UnknownOption_IsRejectedWithUsageError()
    {
        using var harness = CliHarness.Create();

        var result = harness.Run("config", "init", "--bogus");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--bogus", result.Error);
    }

    [Fact]
    public void MissingVerb_ReportsUsageError()
    {
        using var harness = CliHarness.Create();

        var result = harness.Run();

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("missing command", result.Error);
        Assert.Contains("config", result.Error);
    }

    [Fact]
    public void UnknownVerb_IsRejectedByTheParser()
    {
        using var harness = CliHarness.Create();

        var result = harness.Run("bogus");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Unrecognized command or argument", result.Error);
    }

    [Fact]
    public void MissingConfigSubcommand_ReportsUsageError()
    {
        using var harness = CliHarness.Create();

        var result = harness.Run("config");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("missing subcommand", result.Error);
    }

    [Fact]
    public void RootHelp_ListsCommandHierarchy()
    {
        using var harness = CliHarness.Create();

        var result = harness.Run("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("config", result.Output);
        Assert.Contains("status", result.Output);
        Assert.Contains("--config-dir", result.Output);
    }

    [Fact]
    public void ConfigCommandHelp_ListsSubcommands()
    {
        using var harness = CliHarness.Create();

        var result = harness.Run("config", "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("init", result.Output);
        Assert.Contains("validate", result.Output);
        Assert.Contains("show", result.Output);
    }

    [Fact]
    public void StatusCommand_ReportsInactiveSessionAndExitsZero()
    {
        using var harness = CliHarness.Create();

        var result = harness.Run("--data-root", harness.DataRoot, "status");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("sessions: none active", result.Output);
        Assert.Contains(harness.DataRoot, result.Output);
    }

    [Fact]
    public void GlobalDataRootOptionAfterVerb_IsRejectedByTheParser()
    {
        using var harness = CliHarness.Create();

        // Global options belong to the root command; the framework reports them as
        // unrecognized when they appear after the verb.
        var result = harness.Run("status", "--data-root", harness.DataRoot);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Unrecognized command or argument", result.Error);
    }

    [Fact]
    public void GlobalConfigDirOption_SelectsAlternateConfigDirectory()
    {
        using var harness = CliHarness.Create();
        var alternate = Path.Combine(Path.GetTempPath(), "meetcap-cli-alt-" + Guid.NewGuid().ToString("N"));

        try
        {
            // Assert on the rendered path first so a failure reports the directory the
            // command actually resolved instead of only a missing file.
            var pathResult = harness.Run("--config-dir", alternate, "config", "path");
            Assert.Equal(0, pathResult.ExitCode);
            Assert.Contains(alternate, pathResult.Output);

            var initResult = harness.Run("--config-dir", alternate, "config", "init");
            Assert.Equal(0, initResult.ExitCode);
            Assert.Contains(alternate, initResult.Output);
            Assert.True(
                File.Exists(Path.Combine(alternate, "config.toml")),
                $"expected config.toml under {alternate}; output was: {initResult.Output}");
            Assert.False(File.Exists(harness.ConfigFilePath));
        }
        finally
        {
            if (Directory.Exists(alternate))
            {
                Directory.Delete(alternate, true);
            }
        }
    }
}
