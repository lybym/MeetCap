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

    [Fact]
    public void AsrResume_RunsTheCleanupPassOverTheDurableQueue()
    {
        // Issue #29's cleanup half at the CLI seam. A terminal job that still owns a staged object
        // is not work the queue drain can see, so `asr resume` finishes it through a separate
        // cleanup pass (AsrCommand.ResumeAsync). This seeds two such jobs — one this provider owns
        // and one it does not — and asserts on the pass's own ownership rule: the provider-owned
        // job is driven to the object-storage boundary, the foreign one is left exactly as it was.
        // A missing cleanup pass leaves both rows identical, which is how this test goes red.
        //
        // This environment has no [asr.tos] and no TOS service, so the owned job's delete stops at
        // the configuration check rather than reaching TOS. That is asserted too: the attempt is
        // visible as retained durable debt and a failed-but-not-fatal cleanup, never as a job
        // failure. The successful delete itself is covered by the provider and processor suites,
        // which own the transport boundary.
        using var harness = CliHarness.Create();
        harness.WriteConfig(ValidProviderConfig);
        SeedCleanupDebt(harness, "job_owned", provider: "volcengine");
        SeedCleanupDebt(harness, "job_foreign", provider: "other");

        var result = harness.Run("--data-root", harness.DataRoot, "asr", "resume");

        Assert.Equal(0, result.ExitCode);

        // The pass reached the provider-owned job and drove a release attempt; the attempt failed
        // on configuration, so the debt survives for the next run instead of being forgotten.
        Assert.Equal("succeeded", ReadJobStatus(harness, "job_owned"));
        Assert.Equal(1, ReadCleanupPending(harness, "job_owned"));

        // The ownership rule the pass applies: a job another provider staged is that provider's
        // cleanup to do, so nothing here may touch it.
        Assert.Equal("succeeded", ReadJobStatus(harness, "job_foreign"));
        Assert.Equal(1, ReadCleanupPending(harness, "job_foreign"));
    }

    [Fact]
    public void AsrResume_SessionWithNoDebtAtAll_StillReportsNothingToDo()
    {
        // The other side of the same decision: with a terminal job that owes no cleanup and no
        // resumable work, there is nothing for either pass, so the operator is told so rather than
        // being given a silent success.
        using var harness = CliHarness.Create();
        harness.WriteConfig(ValidProviderConfig);
        SeedCleanupDebt(harness, "job_settled", provider: "volcengine", pending: 0);

        var result = harness.Run("--data-root", harness.DataRoot, "asr", "resume");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("No ASR jobs need work", result.Output);
    }

    /// <summary>
    /// Seeds a succeeded TOS job whose object has not been released, which is the durable state a
    /// process that died before its delete leaves behind (<c>docs/DATA_MODEL.md</c> section 6.2).
    /// </summary>
    private static void SeedCleanupDebt(
        CliHarness harness,
        string jobId,
        string provider,
        int pending = 1)
    {
        var db = Path.Combine(harness.DataRoot, "meetcap.db");
        new MeetCap.Persistence.Storage.MeetCapDatabase(db).EnsureMigrated();

        using var c = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = db,
                Pooling = false,
            }.ToString());
        c.Open();

        using (var session = new Microsoft.Data.Sqlite.SqliteCommand(
                   "INSERT OR IGNORE INTO sessions (id, title, mode, source_type, status, created_at, updated_at) " +
                   "VALUES ('ses_1', 'Imported', 'import', 'import', 'COMPLETED', @now, @now)",
                   c))
        {
            session.Parameters.AddWithValue("@now", "2026-09-15T00:00:00Z");
            session.ExecuteNonQuery();
        }

        using var job = new Microsoft.Data.Sqlite.SqliteCommand(
            "INSERT INTO asr_jobs (id, session_id, source, tier, provider, input_artifact, status, " +
            "provider_request_id, audio_transport, tos_bucket, tos_object_key, tos_cleanup_pending, " +
            "created_at, updated_at) " +
            "VALUES (@id, 'ses_1', 'import', 'standard', @provider, " +
            "'audio/import/normalized.wav', 'succeeded', @req, 'tos', 'meetcap-asr', @key, @pending, @now, @now)",
            c);
        job.Parameters.AddWithValue("@id", jobId);
        job.Parameters.AddWithValue("@provider", provider);
        job.Parameters.AddWithValue("@req", "req-" + jobId);
        job.Parameters.AddWithValue("@key", "meetcap-asr/ab/2026/09/19/" + jobId + ".wav");
        job.Parameters.AddWithValue("@pending", pending);
        job.Parameters.AddWithValue("@now", "2026-09-15T00:00:00Z");
        job.ExecuteNonQuery();
    }

    private static string? ReadJobStatus(CliHarness harness, string jobId) =>
        ReadScalar(harness, $"SELECT status FROM asr_jobs WHERE id = '{jobId}'") as string;

    private static long ReadCleanupPending(CliHarness harness, string jobId) =>
        Convert.ToInt64(ReadScalar(harness, $"SELECT tos_cleanup_pending FROM asr_jobs WHERE id = '{jobId}'"));

    private static object? ReadScalar(CliHarness harness, string sql)
    {
        using var c = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(harness.DataRoot, "meetcap.db"),
                Pooling = false,
            }.ToString());
        c.Open();
        using var cmd = new Microsoft.Data.Sqlite.SqliteCommand(sql, c);
        var value = cmd.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    private static string WriteSource(CliHarness harness)
    {
        var path = Path.Combine(harness.DataRoot, "meeting.wav");
        File.WriteAllBytes(path, new byte[64]);
        return path;
    }
}
