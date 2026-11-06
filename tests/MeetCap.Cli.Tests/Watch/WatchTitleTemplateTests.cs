using MeetCap.Cli.Commands.Watch;
using Xunit;

namespace MeetCap.Cli.Tests.Watch;

public class WatchTitleTemplateTests
{
    [Fact]
    public void AppToken_ExpandsToProcessName()
    {
        Assert.Equal("CloudVideo", WatchTitleTemplate.Render("{app}", "CloudVideo", DateTimeOffset.Now));
    }

    [Fact]
    public void StartToken_WithFormat_ExpandsToFormattedLocalDate()
    {
        // Noon UTC keeps the calendar date stable across every real time zone, so the
        // date-only assertion is timezone-independent.
        var start = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

        var rendered = WatchTitleTemplate.Render("{app} {start:yyyy-MM-dd}", "WeMeetApp", start);

        Assert.Equal($"WeMeetApp {start.LocalDateTime:yyyy-MM-dd}", rendered);
    }

    [Fact]
    public void StartToken_WithoutFormat_UsesDefaultFormat()
    {
        var start = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

        var rendered = WatchTitleTemplate.Render("{app} {start}", "CloudVideo", start);

        Assert.Equal($"CloudVideo {start.LocalDateTime:yyyy-MM-dd HH-mm-ss}", rendered);
    }

    [Fact]
    public void MultipleTokens_AllExpanded()
    {
        var start = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

        var rendered = WatchTitleTemplate.Render("{app}-{start:yyyy}", "WeMeetApp", start);

        Assert.Equal($"WeMeetApp-{start.LocalDateTime:yyyy}", rendered);
    }

    [Fact]
    public void UnknownToken_LeftIntact()
    {
        // A misspelled placeholder is visible rather than silently dropped.
        var rendered = WatchTitleTemplate.Render("{app} {bogus}", "CloudVideo", DateTimeOffset.Now);
        Assert.Equal("CloudVideo {bogus}", rendered);
    }

    [Fact]
    public void MalformedFormat_FallsBackToDefault()
    {
        var start = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

        // A whitespace-only format string falls back to the default format rather than
        // producing an empty or confusing title.
        var rendered = WatchTitleTemplate.Render("{app} {start:   }", "CloudVideo", start);

        Assert.Equal($"CloudVideo {start.LocalDateTime:yyyy-MM-dd HH-mm-ss}", rendered);
    }

    [Fact]
    public void EmptyTemplate_ReturnsAppName()
    {
        Assert.Equal("CloudVideo", WatchTitleTemplate.Render("", "CloudVideo", DateTimeOffset.Now));
    }

    [Fact]
    public void UnclosedBrace_EmittedVerbatim()
    {
        var rendered = WatchTitleTemplate.Render("{app} trailing {unclosed", "CloudVideo", DateTimeOffset.Now);
        Assert.Equal("CloudVideo trailing {unclosed", rendered);
    }
}
