using MeetCap.Core.Configuration;
using MeetCap.Core.Secrets;
using Xunit;

namespace MeetCap.Core.Tests.Secrets;

public class SecretRedactorTests
{
    [Fact]
    public void GetSecretValues_ContainsCredentialValue()
    {
        var secrets = SecretRedactor.GetSecretValues(ConfigurationDefaults.Default());
        Assert.Contains("env:MEETCAP_VOLCENGINE_ACCESS_TOKEN", secrets);
    }

    [Fact]
    public void Redact_ReplacesSecretValueWithMask()
    {
        var secrets = SecretRedactor.GetSecretValues(ConfigurationDefaults.Default());
        var text = "credential = \"env:MEETCAP_VOLCENGINE_ACCESS_TOKEN\"";
        var redacted = SecretRedactor.Redact(text, secrets);
        Assert.DoesNotContain("MEETCAP_VOLCENGINE_ACCESS_TOKEN", redacted);
        Assert.Contains("***", redacted);
    }

    [Fact]
    public void Redact_NoSecrets_ReturnsTextUnchanged()
    {
        var empty = new HashSet<string>();
        Assert.Equal("hello world", SecretRedactor.Redact("hello world", empty));
    }

    [Fact]
    public void Redact_NullText_ReturnsEmpty()
        => Assert.Equal(string.Empty, SecretRedactor.Redact(null!, new HashSet<string>()));

    [Fact]
    public void Redact_SecretAppearsMultipleTimes_AllMasked()
    {
        var secrets = new HashSet<string> { "supersecret" };
        var redacted = SecretRedactor.Redact("a=supersecret b=supersecret", secrets);
        Assert.DoesNotContain("supersecret", redacted);
        Assert.Equal(2, redacted.Split("***", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void GetSecretValues_EmptyCredential_NotTreatedAsSecret()
    {
        var c = ConfigurationDefaults.Default();
        c.Asr.Volcengine.Credential = string.Empty;
        Assert.Empty(SecretRedactor.GetSecretValues(c));
    }

    [Fact]
    public void Registry_UpdateFrom_PopulatesFromConfig()
    {
        var registry = new SecretRegistry();
        Assert.Empty(registry.Values);
        registry.UpdateFrom(ConfigurationDefaults.Default());
        Assert.Contains("env:MEETCAP_VOLCENGINE_ACCESS_TOKEN", registry.Values);
    }
}
