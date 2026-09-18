using Xunit;

namespace MeetCap.Cli.Tests;

/// <summary>
/// <c>meetcap import</c> and <c>meetcap asr resume</c> surface (Issue #5, M3; provider
/// contract per issue #26).
/// </summary>
/// <remarks>
/// The success path needs FFmpeg and a Volcengine API key, neither of which exists
/// in CI, so the CLI-level tests cover command surface and the failure ordering that
/// the acceptance criteria call out: an invalid or legacy provider configuration must
/// fail visibly without leaving any session state behind. The end-to-end path is covered
/// by MeetCap.IntegrationTests with the provider boundary mocked, and real Volcengine
/// transcription is explicitly NOT verified (docs/DEVELOPMENT.md section 7).
/// </remarks>
public class ImportAndAsrCommandTests
{
    private const string ValidProviderConfig = """
        config_version = 1

        [asr.volcengine]
        api_key = "literal-test-api-key"
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
    public void ImportHelp_DocumentsFileAndTitleButNoServiceTier()
    {
        using var harness = CliHarness.Create();

        var result = harness.Run("import", "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--title", result.Output);
        // Issue #26 removed the tier selector, so the option must be gone from the surface
        // rather than accepted and ignored.
        Assert.DoesNotContain("--tier", result.Output);
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
    public void Import_EmptyApiKey_FailsWithAnActionableMessageBeforeCreatingSessionState()
    {
        using var harness = CliHarness.Create();
        harness.WriteConfig("config_version = 1\n\n[asr.volcengine]\napi_key = \"\"\n");
        var source = WriteSource(harness);

        var result = harness.Run("--data-root", harness.DataRoot, "import", source);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("credential", result.Error, StringComparison.OrdinalIgnoreCase);

        // Fail-visible, state-clean: no session directory and no database were created.
        Assert.False(Directory.Exists(Path.Combine(harness.DataRoot, "sessions")));
        Assert.False(File.Exists(Path.Combine(harness.DataRoot, "meetcap.db")));
    }

    [Fact]
    public void Import_UnresolvableApiKey_FailsVisiblyAndNamesTheEnvironmentVariable()
    {
        using var harness = CliHarness.Create();
        harness.WriteConfig(
            "config_version = 1\n\n[asr.volcengine]\n" +
            "api_key = \"env:MEETCAP_M3_TEST_MISSING_KEY\"\n");
        var source = WriteSource(harness);

        var result = harness.Run("--data-root", harness.DataRoot, "import", source);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("MEETCAP_M3_TEST_MISSING_KEY", result.Error);
        Assert.Contains("asr.volcengine.api_key", result.Error);
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
    public void Import_RejectsALegacyProviderConfigurationWithAMigrationMessage()
    {
        // A pre-#26 configuration must not be reinterpreted: the legacy AppID/Access Token
        // pair and the configurable resource id are rejected before any session exists.
        using var harness = CliHarness.Create();
        harness.WriteConfig(
            "config_version = 1\n\n" +
            "[asr]\nservice_tier = \"idle\"\n\n" +
            "[asr.volcengine]\n" +
            "app_id = \"test-app-id\"\ncredential = \"literal-test-token\"\nresource_id = \"volc.bigasr.auc\"\n");
        var source = WriteSource(harness);

        var result = harness.Run("--data-root", harness.DataRoot, "import", source);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("asr.volcengine.credential", result.Error);
        Assert.Contains("api_key", result.Error);
        Assert.False(File.Exists(Path.Combine(harness.DataRoot, "meetcap.db")));
        Assert.False(Directory.Exists(Path.Combine(harness.DataRoot, "sessions")));
    }

    [Fact]
    public void Import_TierFlagIsNoLongerACommand_SoPassingItIsAParseError()
    {
        using var harness = CliHarness.Create();
        harness.WriteConfig(ValidProviderConfig);
        var source = WriteSource(harness);

        var result = harness.Run("--data-root", harness.DataRoot, "import", source, "--tier", "turbo");

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(Directory.Exists(Path.Combine(harness.DataRoot, "sessions")));
    }

    [Fact]
    public void ConfigValidate_ReportsTheLegacyProviderKeysAsErrors()
    {
        using var harness = CliHarness.Create();
        harness.WriteConfig(
            "config_version = 1\n\n[asr.volcengine]\ncredential = \"env:MEETCAP_OLD\"\n");

        var result = harness.Run("config", "validate");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Legacy configuration key 'asr.volcengine.credential'", result.Error);
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
