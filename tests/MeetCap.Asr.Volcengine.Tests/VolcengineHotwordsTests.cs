using MeetCap.Core.Asr;
using MeetCap.Core.Configuration;
using Xunit;

namespace MeetCap.Asr.Volcengine.Tests;

public class VolcengineHotwordsTests
{
    [Fact]
    public void Resolve_DefaultWeights_Deduplicates_AndInlineWinsOverFile()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        try
        {
            File.WriteAllText(file, "# meeting vocabulary\n结对子|6\n\n海康大华|2\n省战客\n");
            var result = VolcengineHotwords.Resolve(new[] { "海康大华|9" }, file);
            Assert.Equal(new[]
            {
                new VolcengineHotword("海康大华", 9),
                new VolcengineHotword("结对子", 6),
                new VolcengineHotword("省战客", 4),
            }, result.Words);
            Assert.Empty(result.Warnings);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("海康大华|0", "weight")]
    [InlineData("海康大华|11", "weight")]
    [InlineData("海康大华|x", "weight")]
    [InlineData("海康大华|2|3", "supported")]
    [InlineData("一二三四五六七八九十甲", "10 characters")]
    [InlineData("海康-大华", "supported")]
    [InlineData("1234", "digit-only")]
    public void Resolve_RejectsInvalidEntry(string entry, string expected)
    {
        var error = Assert.Throws<AsrConfigurationException>(
            () => VolcengineHotwords.Resolve(new[] { entry }, ""));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        Assert.Contains("hotwords[0]", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_RejectsMissingOrRelativeFile()
    {
        var relative = Assert.Throws<AsrConfigurationException>(
            () => VolcengineHotwords.Resolve(Array.Empty<string>(), "hotwords.txt"));
        Assert.Contains("absolute path", relative.Message, StringComparison.Ordinal);

        var missing = Assert.Throws<AsrConfigurationException>(
            () => VolcengineHotwords.Resolve(Array.Empty<string>(),
                Path.Combine(Path.GetTempPath(), "missing-hotwords-" + Guid.NewGuid() + ".txt")));
        Assert.Contains("Cannot read", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_TruncatesAboveFiveThousandDistinctWordsAndWarns()
    {
        // The cap counts distinct terms by input order; a repeated word is ignored and
        // does not consume a slot. The list is capped (not rejected) and a warning is
        // produced so the truncation is observable rather than silent.
        var entries = Enumerable.Range(0, 5001)
            .Select(i => "词" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        var result = VolcengineHotwords.Resolve(entries, "");

        Assert.Equal(VolcengineHotwords.MaxWords, result.Words.Count);
        // The first 5000 by input order are retained; the 5001st is dropped.
        Assert.Equal("词0", result.Words[0].Word);
        Assert.Equal("词4999", result.Words[^1].Word);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("5000", warning, StringComparison.Ordinal);
        Assert.Contains("truncated", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_DuplicatesDoNotConsumeCapSlots()
    {
        // 5000 distinct words plus many repeats of the first one stay within the cap and
        // produce no truncation warning: only distinct terms count.
        var distinct = Enumerable.Range(0, 5000)
            .Select(i => "词" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var entries = distinct.Concat(Enumerable.Repeat("词0", 100)).ToArray();
        var result = VolcengineHotwords.Resolve(entries, "");

        Assert.Equal(VolcengineHotwords.MaxWords, result.Words.Count);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void BuildOptions_UsesEffectiveConfigAndResolvesInlineHotwords()
    {
        var config = ConfigurationDefaults.Default();
        config.Asr.Volcengine.Hotwords = new[] { "海康大华|9", "省战客" };
        var options = VolcengineAsrProviderFactory.BuildOptions(config, _ => "fake-key");
        Assert.Equal(new[]
        {
            new VolcengineHotword("海康大华", 9),
            new VolcengineHotword("省战客", 4),
        }, options.Hotwords);
        Assert.Empty(options.HotwordWarnings);
    }
}
