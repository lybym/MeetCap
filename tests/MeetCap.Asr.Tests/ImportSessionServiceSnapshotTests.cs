using System.Text.Encodings.Web;
using System.Text.Json;
using MeetCap.Asr.Importing;
using MeetCap.Core.Configuration;
using Xunit;

namespace MeetCap.Asr.Tests;

/// <summary>
/// The effective-configuration snapshot is persisted (it is the session's
/// <c>config_snapshot</c>), so these tests pin the guarantee that no API key reaches it in
/// any serialized form — not only verbatim, but also in whatever escaping the JSON writer
/// chose (issue #26, docs/CONFIGURATION.md rule 7 and section 12).
/// </summary>
public class ImportSessionServiceSnapshotTests
{
    /// <summary>
    /// A literal key assembled from every character class the JSON writer rewrites: the
    /// default encoder escapes <c>+</c> and every non-ASCII character as <c>\uXXXX</c>, and
    /// JSON itself has to escape <c>"</c> and <c>\</c>. Redacting the serialized text
    /// therefore has to match a representation that no longer contains the key's own bytes,
    /// which is exactly the hole these tests exist for.
    /// </summary>
    private const string EscapingKey = "AA+BB/CC==\"D\\E中F";

    private const string Redacted = "***";

    [Fact]
    public void SnapshotJson_RedactsALiteralKeyMadeOfCharactersTheJsonWriterEscapes()
    {
        var json = SnapshotOf(EscapingKey);

        // The value a consumer reads back out of sessions.config_snapshot. Asserting on the
        // *decoded* value is what makes this independent of the writer's escaping: the
        // serializer's default encoder renders this key as "AA\u002BBB/CC==\u0022D\\E\u4E2DF",
        // which contains none of the key's own bytes and so defeats a literal search.
        Assert.Equal(Redacted, ReadApiKey(json));

        // Neither the key nor the escaped form the default encoder produces may appear.
        Assert.DoesNotContain(EscapingKey, json, StringComparison.Ordinal);
        Assert.DoesNotContain(JavaScriptEncoder.Default.Encode(EscapingKey), json, StringComparison.Ordinal);
        Assert.All(StringValuesOf(json), value => Assert.DoesNotContain("AA+BB", value, StringComparison.Ordinal));
    }

    [Fact]
    public void SnapshotJson_RedactsTheEnvironmentReferenceAndKeepsTheRestOfTheSnapshot()
    {
        // The default api_key is an env: reference: not a credential itself, but it is the
        // value the configuration stores, so it is redacted like any other secret-bearing
        // value while the diagnostic content of the snapshot is preserved.
        var configuration = new MeetCapConfiguration();
        configuration.App.DefaultTitle = "Weekly sync";
        configuration.Asr.Volcengine.ApiKey = "env:MEETCAP_TEST_API_KEY";

        var json = ImportSessionService.SnapshotJson(configuration);

        Assert.Equal(Redacted, ReadApiKey(json));
        Assert.Contains("\"default_title\":\"Weekly sync\"", json, StringComparison.Ordinal);
        Assert.Contains("\"config_version\"", json, StringComparison.Ordinal);
        Assert.Contains("\"strategy\":\"file\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotJson_WithAnEmptyKey_KeepsTheVolcengineSection()
    {
        var configuration = new MeetCapConfiguration();
        configuration.Asr.Volcengine.ApiKey = string.Empty;

        var json = ImportSessionService.SnapshotJson(configuration);

        Assert.Equal(string.Empty, ReadApiKey(json));
        Assert.Contains("\"volcengine\"", json, StringComparison.Ordinal);
        Assert.Contains("\"http_timeout_seconds\":30", json, StringComparison.Ordinal);
    }

    private static string SnapshotOf(string apiKey)
    {
        var configuration = new MeetCapConfiguration();
        configuration.Asr.Volcengine.ApiKey = apiKey;
        return ImportSessionService.SnapshotJson(configuration);
    }

    private static string? ReadApiKey(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement
            .GetProperty("asr")
            .GetProperty("volcengine")
            .GetProperty("api_key")
            .GetString();
    }

    private static List<string> StringValuesOf(string json)
    {
        using var document = JsonDocument.Parse(json);
        var values = new List<string>();
        CollectStrings(document.RootElement, values);
        return values;
    }

    private static void CollectStrings(JsonElement element, List<string> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    CollectStrings(property.Value, values);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectStrings(item, values);
                }

                break;
            case JsonValueKind.String:
                values.Add(element.GetString() ?? string.Empty);
                break;
        }
    }
}
