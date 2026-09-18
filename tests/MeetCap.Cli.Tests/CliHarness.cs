using System.CommandLine;
using MeetCap.Core.Capture;
using MeetCap.Core.Configuration;
using MeetCap.Persistence.Configuration;
using Xunit;

namespace MeetCap.Cli.Tests;

/// <summary>
/// Shared harness for driving the real <c>meetcap</c> command tree. The harness
/// uses the production entry point with a temporary configuration store and
/// captured output writers, so parsing, validation, help, logging, and redaction
/// are all exercised as shipped.
/// </summary>
/// <remarks>
/// The capture platform is faked: M1 commands are exercised end to end — configuration,
/// SQLite, session directories and chunk files are real — but no audio hardware is
/// required (docs/DEVELOPMENT.md section 7).
/// </remarks>
internal sealed class CliHarness : IDisposable
{
    private readonly string _root;
    private readonly IConfigurationStore _store;

    private CliHarness(string root)
    {
        _root = root;
        ConfigDirectory = Path.Combine(root, "config");
        DataRoot = Path.Combine(root, "data");
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(DataRoot);
        _store = new TomlConfigurationStore(ConfigDirectory);
        Environment = new TestCliEnvironment(ConfigDirectory);

        Platform = new FakeCapturePlatformFactory();
        Platform.Devices.Replace(new CaptureDeviceInfo("mic-default", "Test Microphone", true));
    }

    public static CliHarness Create()
        => new(Path.Combine(Path.GetTempPath(), "meetcap-cli-test-" + Guid.NewGuid().ToString("N")));

    public string ConfigDirectory { get; }

    public string DataRoot { get; }

    public CliEnvironment Environment { get; }

    public FakeCapturePlatformFactory Platform { get; }

    public string ConfigFilePath => _store.ConfigFilePath;

    /// <summary>Runs <c>meetcap</c> with the harness configuration store and captures output.</summary>
    public CliResult Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        // No explicit store: the harness relies on the production store resolution so
        // the one-shot --config-dir override is exercised end to end. The ASR transport is
        // the harness's stand-in, so the command composition, the SQLite queue, the
        // transcript writer, and the Volcengine adapter are all the shipped ones and only
        // the network boundary is replaced (docs/DEVELOPMENT.md section 7).
        var exitCode = Program.Run(
            args,
            Environment,
            configurationStore: null,
            invocationConfiguration: new InvocationConfiguration { Output = output, Error = error },
            platformFactory: Platform,
            asrHttpHandler: AsrHttp);

        // Capture the text after Run returns; the harness owns the writers and the
        // production pipeline does not write once Run has completed.
        return new CliResult(exitCode, output.ToString(), error.ToString());
    }

    /// <summary>Provider transport stand-in, installed by <see cref="WriteLiveAsrConfig"/>.</summary>
    public ScriptedAsrHttpHandler AsrHttp { get; } = new();

    public void WriteConfig(string toml) => File.WriteAllText(ConfigFilePath, toml);

    /// <summary>Writes a capture-oriented configuration pointing at the temporary data root.</summary>
    /// <remarks>
    /// ASR is disabled by default so the M1/M2 capture tests exercise recording alone. A test
    /// that wants the M4 live transcription path uses <see cref="WriteLiveAsrConfig"/>, which
    /// enables it and installs the provider stand-in.
    /// </remarks>
    public void WriteCaptureConfig(
        int chunkSeconds = 2,
        double minimumFreeSpaceGb = 1,
        string microphoneDeviceId = "default")
        => WriteConfig(
            "config_version = 1\n" +
            "\n" +
            "[app]\n" +
            "default_title = \"Untitled Meeting\"\n" +
            "\n" +
            "[capture]\n" +
            "default_mode = \"offline\"\n" +
            $"chunk_seconds = {chunkSeconds}\n" +
            "buffer_seconds = 5\n" +
            "flush_interval_ms = 200\n" +
            "\n" +
            "[capture.offline]\n" +
            $"microphone_device_id = \"{microphoneDeviceId}\"\n" +
            "\n" +
            "[storage]\n" +
            $"data_root = '{DataRoot}'\n" +
            $"minimum_free_space_gb = {minimumFreeSpaceGb}\n" +
            "\n" +
            "[asr]\n" +
            "enabled = false\n");

    /// <summary>
    /// Writes an online (dual-track) capture configuration pointing at the temporary data
    /// root, so a test can exercise the M5 microphone + loopback path through the real
    /// command tree (docs/ROADMAP.md M5).
    /// </summary>
    public void WriteOnlineCaptureConfig(
        int chunkSeconds = 1,
        string loopbackMode = "system",
        string renderDeviceId = "default",
        string processName = "",
        double minimumFreeSpaceGb = 1)
        => WriteConfig(
            "config_version = 1\n" +
            "\n" +
            "[app]\n" +
            "default_title = \"Untitled Meeting\"\n" +
            "\n" +
            "[capture]\n" +
            "default_mode = \"online\"\n" +
            $"chunk_seconds = {chunkSeconds}\n" +
            "buffer_seconds = 5\n" +
            "flush_interval_ms = 200\n" +
            "\n" +
            "[capture.online]\n" +
            "microphone_device_id = \"default\"\n" +
            $"loopback_mode = \"{loopbackMode}\"\n" +
            $"render_device_id = \"{renderDeviceId}\"\n" +
            $"process_name = \"{processName}\"\n" +
            "\n" +
            "[storage]\n" +
            $"data_root = '{DataRoot}'\n" +
            $"minimum_free_space_gb = {minimumFreeSpaceGb}\n" +
            "\n" +
            "[asr]\n" +
            "enabled = false\n");

    /// <summary>
    /// Writes a capture configuration with M4 live file-ASR enabled, and installs a scripted
    /// provider stand-in so no test reaches the real Volcengine service.
    /// </summary>
    public void WriteLiveAsrConfig(
        int chunkSeconds = 1,
        int fileBatchSeconds = 3,
        double minimumFreeSpaceGb = 1,
        int pollTimeoutSeconds = 900)
    {
        WriteConfig(
            "config_version = 1\n" +
            "\n" +
            "[app]\n" +
            "default_title = \"Untitled Meeting\"\n" +
            "\n" +
            "[capture]\n" +
            "default_mode = \"offline\"\n" +
            $"chunk_seconds = {chunkSeconds}\n" +
            "buffer_seconds = 5\n" +
            "flush_interval_ms = 200\n" +
            "\n" +
            "[capture.offline]\n" +
            "microphone_device_id = \"default\"\n" +
            "\n" +
            "[storage]\n" +
            $"data_root = '{DataRoot}'\n" +
            $"minimum_free_space_gb = {minimumFreeSpaceGb}\n" +
            "\n" +
            "[asr]\n" +
            "enabled = true\n" +
            "strategy = \"file\"\n" +
            $"file_batch_seconds = {fileBatchSeconds}\n" +
            "streaming_enabled = false\n" +
            "\n" +
            "[asr.volcengine]\n" +
            // A literal key is accepted by the secret resolver, so the harness never needs a
            // real environment variable. The adapter is the shipped one; only the HTTP
            // transport is replaced.
            "api_key = \"test-api-key\"\n" +
            "request_speaker_info = true\n" +
            "poll_interval_seconds = 1\n" +
            $"poll_timeout_seconds = {pollTimeoutSeconds}\n");

        AsrHttp.Reset();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
        catch (IOException)
        {
            // Temporary test directories are best-effort cleanup.
        }
    }

    /// <summary>Pins the config directory instead of reading <c>%APPDATA%</c>.</summary>
    private sealed class TestCliEnvironment : CliEnvironment
    {
        private readonly string _configDirectory;

        public TestCliEnvironment(string configDirectory) => _configDirectory = configDirectory;

        public override string DefaultConfigDirectory => _configDirectory;
    }
}

/// <summary>Captured result of one CLI invocation.</summary>
internal sealed record CliResult(int ExitCode, string Output, string Error);
