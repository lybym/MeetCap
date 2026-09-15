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
            Assert.Equal("env:MEETCAP_VOLCENGINE_ACCESS_TOKEN", load.Configuration.Asr.Volcengine.Credential);
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
    public void ToToml_Serialized_ThenRedact_MasksCredential()
    {
        var dir = NewDir();
        try
        {
            var store = new TomlConfigurationStore(dir);
            store.WriteDefault(overwrite: false);
            var load = store.Load();

            var toml = store.ToToml(load.Configuration);
            Assert.Contains("env:MEETCAP_VOLCENGINE_ACCESS_TOKEN", toml);

            var secrets = SecretRedactor.GetSecretValues(load.Configuration);
            var redacted = SecretRedactor.Redact(toml, secrets);
            Assert.DoesNotContain("env:MEETCAP_VOLCENGINE_ACCESS_TOKEN", redacted);
            Assert.Contains("***", redacted);
        }
        finally
        {
            Cleanup(dir);
        }
    }
}
