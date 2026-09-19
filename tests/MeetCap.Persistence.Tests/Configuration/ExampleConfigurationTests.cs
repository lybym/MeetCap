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

            // Issue #34: the recovery window is part of the shipped contract, not only of the
            // schema, so the example must carry the same default the model does.
            Assert.Equal(20, config.Capture.DeviceRecoverySeconds);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void ExampleConfig_LoadsANonDefaultDeviceRecoveryWindowIntoTheModel()
    {
        var dir = NewDir();
        try
        {
            Directory.CreateDirectory(dir);
            var toml = File.ReadAllText(ExampleConfigPath())
                .Replace("device_recovery_seconds = 20", "device_recovery_seconds = 7", StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(dir, "config.toml"), toml);

            var config = new TomlConfigurationStore(dir).Load().Configuration;

            // The shipped example carries the model default (20), so asserting on it alone cannot
            // fail even if the mapping were dropped entirely — snake_case deserialization is
            // generic and a missing binding would land on the default. A value that is not the
            // default is what makes the user-facing knob a checked contract rather than a
            // restatement of the default (docs/CONFIGURATION.md section 6). That the parsed
            // window also reaches the derived retry budget is covered where the derivation
            // lives, in tests/MeetCap.AudioPipeline.Tests/DeviceRecoveryWindowTests.cs.
            Assert.Equal(7, config.Capture.DeviceRecoverySeconds);
            Assert.NotEqual(20, config.Capture.DeviceRecoverySeconds);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void LegacyFlatSpeakerKeys_BlockValidationWithMigrationInstructions()
    {
        var dir = NewDir();
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, "config.toml"),
                "config_version = 1\n\n[speakers]\nprovider = \"local\"\nmatch_threshold = 0.5\nmatch_margin = 0.1\n");

            var load = new TomlConfigurationStore(dir).Load();

            // Pre-#9 flat keys are detected by the persistence layer, then promoted to
            // validation errors. A v1 configuration must not pass validation while its
            // speaker policy quietly falls back to new defaults.
            Assert.Contains("speakers.provider", load.UnknownKeys);
            Assert.Contains("speakers.match_threshold", load.UnknownKeys);
            Assert.Contains("speakers.match_margin", load.UnknownKeys);

            var validation = ConfigurationValidator.Validate(load.Configuration, load.UnknownKeys);
            Assert.False(validation.IsValid);
            Assert.Contains(
                validation.Errors,
                error => error ==
                    "Legacy configuration key 'speakers.provider' is not supported by this config layout. " +
                    "Remove it and set [speakers.identity] provider = \"sherpa_onnx_3dspeaker\" " +
                    "after confirming that identity provider is appropriate for your deployment.");
            Assert.Contains(
                validation.Errors,
                error => error ==
                    "Legacy configuration key 'speakers.match_threshold' is not supported by this config layout. " +
                    "Move its value to [speakers.identity] match_threshold.");
            Assert.Contains(
                validation.Errors,
                error => error ==
                    "Legacy configuration key 'speakers.match_margin' is not supported by this config layout. " +
                    "Move its value to [speakers.identity] match_margin.");
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void LegacyVolcengineProviderKeys_BlockValidationWithMigrationInstructions()
    {
        var dir = NewDir();
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, "config.toml"),
                "config_version = 1\n\n" +
                "[asr]\nservice_tier = \"idle\"\n\n" +
                "[asr.volcengine]\napp_id = \"123\"\ncredential = \"env:OLD\"\nresource_id = \"volc.bigasr.auc\"\n");

            var load = new TomlConfigurationStore(dir).Load();

            // The removed provider keys are detected by the persistence layer (they are no
            // longer in the schema), then promoted to blocking validation errors by Core. A v1
            // configuration must not pass validation while its provider settings quietly fall
            // back to the new defaults (docs/CONFIGURATION.md section 13, issue #26).
            Assert.Contains("asr.service_tier", load.UnknownKeys);
            Assert.Contains("asr.volcengine.app_id", load.UnknownKeys);
            Assert.Contains("asr.volcengine.credential", load.UnknownKeys);
            Assert.Contains("asr.volcengine.resource_id", load.UnknownKeys);

            var validation = ConfigurationValidator.Validate(load.Configuration, load.UnknownKeys);

            Assert.False(validation.IsValid);
            Assert.All(
                new[]
                {
                    "asr.service_tier",
                    "asr.volcengine.app_id",
                    "asr.volcengine.credential",
                    "asr.volcengine.resource_id",
                },
                key => Assert.Contains(
                    validation.Errors,
                    error => error.StartsWith(
                        $"Legacy configuration key '{key}' is not supported by this config layout.",
                        StringComparison.Ordinal)));
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
