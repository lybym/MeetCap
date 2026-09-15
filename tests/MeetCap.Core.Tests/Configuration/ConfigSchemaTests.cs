using MeetCap.Core.Configuration;
using Xunit;

namespace MeetCap.Core.Tests.Configuration;

public class ConfigSchemaTests
{
    [Fact]
    public void IsValidLeafKey_RecognizesKnownRootKey()
        => Assert.True(ConfigSchema.IsValidLeafKey("config_version"));

    [Fact]
    public void IsValidLeafKey_RecognizesNestedKey()
        => Assert.True(ConfigSchema.IsValidLeafKey("capture.online.loopback_mode"));

    [Fact]
    public void IsValidLeafKey_RejectsUnknownKey()
        => Assert.False(ConfigSchema.IsValidLeafKey("capture.offline.bogus_key"));

    [Fact]
    public void ValidLeafKeys_CoversAllDocumentedSections()
    {
        var keys = ConfigSchema.ValidLeafKeys;
        Assert.True(keys.Count >= 39);
        Assert.Contains("asr.volcengine.credential", keys);
        Assert.Contains("speakers.match_threshold", keys);
        Assert.Contains("retention.automatic_delete", keys);
    }
}
