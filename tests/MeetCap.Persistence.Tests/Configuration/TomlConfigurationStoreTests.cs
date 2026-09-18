using MeetCap.Core.Configuration;
using MeetCap.Core.Secrets;
using MeetCap.Persistence.Configuration;
using Xunit;

namespace MeetCap.Persistence.Tests.Configuration;

public class TomlConfigurationStoreTests
{
    private static string NewDir()
        => Path.Combine(Path.GetTempPath(), "meetcap-test-" + Guid.NewGuid().ToString("N"));

    private static void Cleanup(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void WriteDefault_ThenLoad_RoundTripsDefaults()
    {
        var dir = NewDir();
        try
        {
            var store = new TomlConfigurationStore(dir);
            store.WriteDefault(overwrite: false);

            var load = store.Load();
            Assert.Null(load.LoadError);
            Assert.Equal(1, load.Configuration.ConfigVersion);
            Assert.Equal("offline", load.Configuration.Capture.DefaultMode);
            Assert.Equal(60, load.Configuration.Capture.ChunkSeconds);
            Assert.Equal("env:MEETCAP_VOLCENGINE_API_KEY", load.Configuration.Asr.Volcengine.ApiKey);
            Assert.Equal("%LOCALAPPDATA%\\MeetCap", load.Configuration.Storage.DataRoot);
            Assert.Empty(load.UnknownKeys);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void WriteDefault_RefusesOverwrite_WithoutForce()
    {
        var dir = NewDir();
        try
        {
            var store = new TomlConfigurationStore(dir);
            store.WriteDefault(overwrite: false);
            Assert.Throws<InvalidOperationException>(() => store.WriteDefault(overwrite: false));
            // with force it succeeds
            store.WriteDefault(overwrite: true);
            Assert.True(store.Exists());
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaultsAndLoadError()
    {
        var store = new TomlConfigurationStore(NewDir());
        var load = store.Load();
        Assert.NotNull(load.LoadError);
        Assert.Equal(1, load.Configuration.ConfigVersion);
        Assert.Equal("offline", load.Configuration.Capture.DefaultMode);
    }

    [Fact]
    public void Load_UnknownKey_IsReported()
    {
        var dir = NewDir();
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "config.toml"), @"config_version = 1

[capture.offline]
microphone_device_id = ""default""
bogus_key = ""x""
");
            var store = new TomlConfigurationStore(dir);
            var load = store.Load();
            Assert.Null(load.LoadError);
            Assert.Contains("capture.offline.bogus_key", load.UnknownKeys);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void Load_UnknownTopLevelTable_IsReported()
    {
        var dir = NewDir();
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "config.toml"), @"config_version = 1

[unknownsection]
whoops = true
");
            var store = new TomlConfigurationStore(dir);
            var load = store.Load();
            Assert.Null(load.LoadError);
            Assert.Contains(load.UnknownKeys, k => k.StartsWith("unknownsection", StringComparison.Ordinal));
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void Load_InvalidToml_ReturnsLoadError()
    {
        var dir = NewDir();
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "config.toml"), "this is = = not valid toml {{{");
            var store = new TomlConfigurationStore(dir);
            var load = store.Load();
            Assert.NotNull(load.LoadError);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void OneShotOverride_DoesNotRewriteConfigFile()
    {
        var dir = NewDir();
        try
        {
            var store = new TomlConfigurationStore(dir);
            store.WriteDefault(overwrite: false);

            var load = store.Load();
            Assert.Equal(60, load.Configuration.Capture.ChunkSeconds);

            // One-shot CLI override applies in memory only.
            ConfigOverrides.Apply(load.Configuration,
                new Dictionary<string, string> { ["capture.chunk_seconds"] = "30" });
            Assert.Equal(30, load.Configuration.Capture.ChunkSeconds);

            // The persistent file must be unchanged.
            var reload = store.Load();
            Assert.Equal(60, reload.Configuration.Capture.ChunkSeconds);
            Assert.Null(reload.LoadError);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void WriteDefault_EmitsAndReloadsTheSpeakerIdentitySections()
    {
        var dir = NewDir();
        try
        {
            var store = new TomlConfigurationStore(dir);
            store.WriteDefault(overwrite: false);
            var text = File.ReadAllText(store.ConfigFilePath);

            // Issue #9 configuration contract: identity matching and the sherpa-onnx
            // runtime live in their own nested sections, not as flat [speakers] keys.
            Assert.Contains("[speakers.identity]", text);
            Assert.Contains("[speakers.sherpa_onnx]", text);
            Assert.Contains("request_speaker_info = true", text);
            Assert.Contains("process_name = \"\"", text);
            Assert.Contains("include_speaker_labels = true", text);

            var reloaded = store.Load();
            Assert.Null(reloaded.LoadError);
            Assert.Empty(reloaded.UnknownKeys);

            Assert.Equal("sherpa_onnx_3dspeaker", reloaded.Configuration.Speakers.Identity.Provider);
            Assert.Equal(0.82, reloaded.Configuration.Speakers.Identity.MatchThreshold);
            Assert.Equal(0.08, reloaded.Configuration.Speakers.Identity.MatchMargin);
            Assert.Equal(5, reloaded.Configuration.Speakers.Identity.SampleMinSeconds);
            Assert.Equal(15, reloaded.Configuration.Speakers.Identity.SampleMaxSeconds);
            Assert.Equal(
                "3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx",
                reloaded.Configuration.Speakers.SherpaOnnx.Model);
            Assert.True(reloaded.Configuration.Asr.Volcengine.RequestSpeakerInfo);
            Assert.True(reloaded.Configuration.Transcript.IncludeSpeakerLabels);
            Assert.Equal(string.Empty, reloaded.Configuration.Capture.Online.ProcessName);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void Load_ProcessLoopbackConfiguration_RoundTripsProcessName()
    {
        var dir = NewDir();
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "config.toml"), @"config_version = 1

[capture.online]
loopback_mode = ""process""
process_name = ""Teams""
");
            var store = new TomlConfigurationStore(dir);
            var load = store.Load();

            Assert.Null(load.LoadError);
            Assert.Empty(load.UnknownKeys);
            Assert.Equal("process", load.Configuration.Capture.Online.LoopbackMode);
            Assert.Equal("Teams", load.Configuration.Capture.Online.ProcessName);
            Assert.True(ConfigurationValidator.Validate(load.Configuration, load.UnknownKeys).IsValid);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void ToToml_Serialized_ThenRedact_MasksTheApiKey()
    {
        var dir = NewDir();
        try
        {
            var store = new TomlConfigurationStore(dir);
            store.WriteDefault(overwrite: false);
            var load = store.Load();

            var toml = store.ToToml(load.Configuration);
            Assert.Contains("env:MEETCAP_VOLCENGINE_API_KEY", toml);

            var secrets = SecretRedactor.GetSecretValues(load.Configuration);
            var redacted = SecretRedactor.Redact(toml, secrets);
            Assert.DoesNotContain("env:MEETCAP_VOLCENGINE_API_KEY", redacted);
            Assert.Contains("***", redacted);
        }
        finally
        {
            Cleanup(dir);
        }
    }
}
