using System.CommandLine;
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
internal sealed class CliHarness : IDisposable
{
    private readonly string _root;

    private CliHarness(string root)
    {
        _root = root;
        ConfigDirectory = Path.Combine(root, "config");
        DataRoot = Path.Combine(root, "data");
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(DataRoot);
        Store = new TomlConfigurationStore(ConfigDirectory);
        Environment = new TestCliEnvironment(ConfigDirectory);
    }

    public static CliHarness Create() => new(Path.Combine(Path.GetTempPath(), "meetcap-cli-test-" + Guid.NewGuid().ToString("N")));

    public string ConfigDirectory { get; }

    public string DataRoot { get; }

    public IConfigurationStore Store { get; }

    public CliEnvironment Environment { get; }

    public string ConfigFilePath => Store.ConfigFilePath;

    /// <summary>Runs <c>meetcap</c> with the harness config directory and captures output.</summary>
    public CliResult Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = Program.Run(
            args,
            Environment,
            Store,
            new InvocationConfiguration { Output = output, Error = error });

        // Read the captured text before the writers are disposed; the command tree
        // writes through these instances and Run returns after the pipeline flushed.
        var captured = new CliResult(exitCode, output.ToString(), error.ToString());
        return captured;
    }

    public void WriteConfig(string toml) => File.WriteAllText(ConfigFilePath, toml);

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
