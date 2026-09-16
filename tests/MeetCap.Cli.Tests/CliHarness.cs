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
        // the one-shot --config-dir override is exercised end to end.
        var exitCode = Program.Run(
            args,
            Environment,
            configurationStore: null,
            invocationConfiguration: new InvocationConfiguration { Output = output, Error = error },
            platformFactory: Platform);

        // Capture the text after Run returns; the harness owns the writers and the
        // production pipeline does not write once Run has completed.
        return new CliResult(exitCode, output.ToString(), error.ToString());
    }

    public void WriteConfig(string toml) => File.WriteAllText(ConfigFilePath, toml);

    /// <summary>Writes a capture-oriented configuration pointing at the temporary data root.</summary>
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
            $"minimum_free_space_gb = {minimumFreeSpaceGb}\n");

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
