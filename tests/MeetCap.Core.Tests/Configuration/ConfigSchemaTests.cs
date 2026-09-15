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
    public void IsValidLeafKey_RejectsPreIssueNineFlatSpeakerKeys()
    {
        // The speaker matching policy moved under [speakers.identity]; the flat keys
        // must not stay valid, or a user's old value would be silently ignored.
        Assert.False(ConfigSchema.IsValidLeafKey("speakers.match_threshold"));
        Assert.False(ConfigSchema.IsValidLeafKey("speakers.match_margin"));
        Assert.False(ConfigSchema.IsValidLeafKey("speakers.provider"));
    }

    [Fact]
    public void ValidLeafKeys_CoversAllDocumentedSections()
    {
        var keys = ConfigSchema.ValidLeafKeys;
        Assert.True(keys.Count >= 39);
        Assert.Contains("asr.volcengine.credential", keys);
        Assert.Contains("asr.volcengine.request_speaker_info", keys);
        Assert.Contains("capture.online.process_name", keys);
        Assert.Contains("speakers.identity.provider", keys);
        Assert.Contains("speakers.identity.match_threshold", keys);
        Assert.Contains("speakers.identity.sample_max_seconds", keys);
        Assert.Contains("speakers.sherpa_onnx.model", keys);
        Assert.Contains("transcript.include_speaker_labels", keys);
        Assert.Contains("retention.automatic_delete", keys);
    }
}
