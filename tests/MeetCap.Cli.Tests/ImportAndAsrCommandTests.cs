using Xunit;

namespace MeetCap.Cli.Tests;

/// <summary>
/// <c>meetcap import</c> and <c>meetcap asr resume</c> surface (Issue #5, M3).
/// </summary>
/// <remarks>
/// The success path needs FFmpeg and Volcengine credentials, neither of which exists
/// in CI, so the CLI-level tests cover command surface and the failure ordering that
/// the acceptance criteria call out: invalid credentials and configuration must fail
/// visibly without leaving any session state behind. The end-to-end path is covered by
/// MeetCap.IntegrationTests with the provider boundary mocked, and real Volcengine
/// transcription is explicitly NOT verified (docs/DEVELOPMENT.md section 7).
/// </remarks>
public class ImportAndAsrCommandTests
{
    private const string ValidProviderConfig = """
        config_version = 1

        [asr.volcengine]
        app_id = "test-app-id"
        credential = "literal-test-token"
        resource_id = "volc.bigasr.auc"
        """;

    [Fact]
    public void RootHelp_ListsImportAndAsrCommands()
    {
        using var harness = CliHarness.Create();

        var result = harness.Run("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("import", result.Output);
        Assert.Contains("asr", result.Output);
    }

    [Fact]
    public void ImportHelp_DocumentsFileTitleAndTier()
    {
        using var harness = CliHarness.Create();

        var result = harness.Run("import", "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--title", result.Output);
        Assert.Contains("--tier", result.Output);
    }

    [Fact]
    public void Import_MissingSourceFile_FailsWithThePathAndCreatesNoSession()
    {
        using var harness = CliHarness.Create();
        harness.WriteConfig(ValidProviderConfig);
        var missing = Path.Combine(harness.DataRoot, "does-not-exist.wav");

        var result = harness.Run("--data-root", harness.DataRoot, "import", missing, "--title", "Nope");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(missing, result.Error);
        Assert.False(Directory.Exists(Path.Combine(harness.DataRoot, "sessions")));
    }

    [Fact]
    public void Import_EmptyAppId_FailsWithAnActionableMessageBeforeCreatingSessionState()
    {
        using var harness = CliHarness.Create();
        harness.WriteConfig("config_version = 1\n\n[asr.volcengine]\napp_id = \"\"\n");
        var source = WriteSource(harness);

        var result = harness.Run("--data-root", harness.DataRoot, "import", source);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("asr.volcengine.app_id", result.Error);

        // Fail-visible, state-clean: no session directory and no database were created.
        Assert.False(Directory.Exists(Path.Combine(harness.DataRoot, "sessions")));
        Assert.False(File.Exists(Path.Combine(harness.DataRoot, "meetcap.db")));
    }

    [Fact]
    public void Import_UnresolvableCredential_FailsVisiblyAndNamesTheEnvironmentVariable()
    {
        using var harness = CliHarness.Create();
        harness.WriteConfig(
            "config_version = 1\n\n[asr.volcengine]\n" +
            "app_id = \"test-app-id\"\n" +
            "credential = \"env:MEETCAP_M3_TEST_MISSING_TOKEN\"\n");
        var source = WriteSource(harness);

        var result = harness.Run("--data-root", harness.DataRoot, "import", source);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("MEETCAP_M3_TEST_MISSING_TOKEN", result.Error);
        Assert.False(File.Exists(Path.Combine(harness.DataRoot, "meetcap.db")));
        Assert.False(Directory.Exists(Path.Combine(harness.DataRoot, "sessions")));
    }

    [Fact]
    public void Import_DisabledAsr_FailsVisibly()
    {
        using var harness = CliHarness.Create();
        harness.WriteConfig(ValidProviderConfig + "\n[asr]\nenabled = false\n");
        var source = WriteSource(harness);

        var result = harness.Run("--data-root", harness.DataRoot, "import", source);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("asr.enabled", result.Error);
    }

    [Fact]
    public void Import_UnsupportedTier_FailsInsteadOfSilentlyMappingToAnotherProtocol()
    {
        using var harness = CliHarness.Create();
        harness.WriteConfig(ValidProviderConfig);
        var source = WriteSource(harness);

        var result = harness.Run("--data-root", harness.DataRoot, "import", source, "--tier", "turbo");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("turbo", result.Error);
        Assert.Contains("not implemented", result.Error);
    }

    [Fact]
    public void Import_InvalidConfiguration_FailsBeforeProviderConstruction()
    {
        using var harness = CliHarness.Create();
        harness.WriteConfig("[capture]\ndefault_mode = \"hybrid\"\n");
        var source = WriteSource(harness);

        var result = harness.Run("--data-root", harness.DataRoot, "import", source);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("capture.default_mode", result.Error);
        Assert.False(File.Exists(Path.Combine(harness.DataRoot, "meetcap.db")));
    }

    [Fact]
    public void Import_MissingConfigFile_FailsWithTheInitHint()
    {
        using var harness = CliHarness.Create();
        var source = WriteSource(harness);

        var result = harness.Run("--data-root", harness.DataRoot, "import", source);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("config init", result.Error);
    }

    [Fact]
    public void Import_WithoutAFileArgument_IsAParserError()
    {
        using var harness = CliHarness.Create();

        var result = harness.Run("import");

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public void AsrWithoutSubcommand_ReportsUsageError()
    {
        using var harness = CliHarness.Create();

        var result = harness.Run("asr");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("missing subcommand", result.Error);
        Assert.Contains("resume", result.Error);
    }

    [Fact]
    public void AsrResume_WithNothingQueued_ExitsZero()
    {
        using var harness = CliHarness.Create();
        harness.WriteConfig(ValidProviderConfig);

        var result = harness.Run("--data-root", harness.DataRoot, "asr", "resume");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("No ASR jobs need work", result.Output);
    }

    [Fact]
    public void AsrResume_SessionFilter_WithNothingQueued_ExitsZero()
    {
        using var harness = CliHarness.Create();
        harness.WriteConfig(ValidProviderConfig);

        var result = harness.Run("--data-root", harness.DataRoot, "asr", "resume", "--session", "ses_none");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("ses_none", result.Output);
    }

    [Fact]
    public void AsrHelp_ListsResume()
    {
        using var harness = CliHarness.Create();

        var result = harness.Run("asr", "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("resume", result.Output);
    }

    private static string WriteSource(CliHarness harness)
    {
        var path = Path.Combine(harness.DataRoot, "meeting.wav");
        File.WriteAllBytes(path, new byte[64]);
        return path;
    }
}
