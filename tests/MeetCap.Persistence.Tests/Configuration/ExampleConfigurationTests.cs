using MeetCap.Core.Configuration;
using MeetCap.Persistence.Configuration;
using Tomlyn;
using Tomlyn.Model;
using Xunit;

namespace MeetCap.Persistence.Tests.Configuration;

/// <summary>
/// <c>config.example.toml</c> is the shipped, user-visible statement of the
/// configuration contract (docs/CONFIGURATION.md, docs/DEVELOPMENT.md section 9).
/// These tests fail whenever the shipped example and the Core schema drift apart in
/// either direction, which is the failure mode issue #9 is most exposed to: the
/// speaker diarization/identity and loopback keys were specified in the docs before
/// any milestone implemented them.
/// </summary>
public class ExampleConfigurationTests
{
    [Fact]
    public void ExampleConfig_LoadsWithoutUnknownKeysAndValidates()
    {
        var dir = NewDir();
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "config.toml"), File.ReadAllText(ExampleConfigPath()));

            var store = new TomlConfigurationStore(dir);
            var load = store.Load();

            Assert.Null(load.LoadError);
            Assert.Empty(load.UnknownKeys);

            var validation = ConfigurationValidator.Validate(load.Configuration, load.UnknownKeys);
            Assert.True(validation.IsValid, string.Join(" | ", validation.Errors));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void ExampleConfig_DocumentsExactlyTheSchemaKeys()
    {
        var exampleKeys = LeafKeys(File.ReadAllText(ExampleConfigPath()));

        var missingFromExample = ConfigSchema.ValidLeafKeys
            .Except(exampleKeys)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        var absentFromSchema = exampleKeys
            .Except(ConfigSchema.ValidLeafKeys)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            missingFromExample.Length == 0,
            "config.example.toml does not document: " + string.Join(", ", missingFromExample));
        Assert.True(
            absentFromSchema.Length == 0,
            "config.example.toml documents keys outside the schema: " + string.Join(", ", absentFromSchema));
    }

    [Fact]
    public void ExampleConfig_LoadsTheSpeakerIdentitySectionsIntoTheModel()
    {
        var dir = NewDir();
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "config.toml"), File.ReadAllText(ExampleConfigPath()));

            var config = new TomlConfigurationStore(dir).Load().Configuration;

            // The shipped example must be loadable as the contract, not only parseable.
            Assert.Equal("sherpa_onnx_3dspeaker", config.Speakers.Identity.Provider);
            Assert.Equal(0.82, config.Speakers.Identity.MatchThreshold);
            Assert.Equal(5, config.Speakers.Identity.SampleMinSeconds);
            Assert.Equal(15, config.Speakers.Identity.SampleMaxSeconds);
            Assert.Equal(
                "3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx",
                config.Speakers.SherpaOnnx.Model);
            Assert.True(config.Asr.Volcengine.RequestSpeakerInfo);
            Assert.True(config.Transcript.IncludeSpeakerLabels);
            Assert.Equal("system", config.Capture.Online.LoopbackMode);
            Assert.Equal(string.Empty, config.Capture.Online.ProcessName);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void LegacyFlatSpeakerKeys_AreReportedAsUnknownInsteadOfSilentlyIgnored()
    {
        var dir = NewDir();
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, "config.toml"),
                "config_version = 1\n\n[speakers]\nprovider = \"local\"\nmatch_threshold = 0.5\n");

            var load = new TomlConfigurationStore(dir).Load();

            // Pre-#9 flat keys are never dropped silently: they surface as unknown keys
            // so `meetcap config validate` can tell the user the value is not in effect.
            Assert.Contains("speakers.provider", load.UnknownKeys);
            Assert.Contains("speakers.match_threshold", load.UnknownKeys);

            var validation = ConfigurationValidator.Validate(load.Configuration, load.UnknownKeys);
            Assert.True(validation.IsValid);
            Assert.Contains(validation.Warnings, w => w.Contains("speakers.match_threshold"));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static string ExampleConfigPath()
        => Path.Combine(RepositoryRoot(), "config.example.toml");

    private static HashSet<string> LeafKeys(string toml)
    {
        var root = (TomlTable)TomlSerializer.Deserialize(toml, typeof(TomlTable))!;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        Walk(root, string.Empty, keys);
        return keys;
    }

    private static void Walk(TomlTable table, string prefix, HashSet<string> keys)
    {
        foreach (var pair in table)
        {
            var path = prefix.Length == 0 ? pair.Key : prefix + "." + pair.Key;
            if (pair.Value is TomlTable sub)
            {
                Walk(sub, path, keys);
            }
            else
            {
                keys.Add(path);
            }
        }
    }

    private static string NewDir()
        => Path.Combine(Path.GetTempPath(), "meetcap-example-test-" + Guid.NewGuid().ToString("N"));

    private static void Cleanup(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>Locates the repository root by walking up to the solution file.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MeetCap.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root (MeetCap.slnx) above {AppContext.BaseDirectory}.");
    }
}
