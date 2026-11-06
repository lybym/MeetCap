namespace MeetCap.Cli.Commands.Watch;

using System.Globalization;
using System.Text;

/// <summary>
/// Renders the <c>watch.title_template</c> into a session title. Supports two tokens:
/// <list type="bullet">
/// <item><c>{app}</c> — the triggering process name.</item>
/// <item><c>{start:format}</c> / <c>{start}</c> — the trigger observation time, formatted
/// with a .NET date format string (e.g. <c>yyyy-MM-dd HH-mm-ss</c>).</item>
/// </list>
/// Unknown tokens are left intact so a typo never silently drops part of a title.
/// </summary>
internal static class WatchTitleTemplate
{
    private const string AppToken = "app";
    private const string StartToken = "start";
    private const string DefaultStartFormat = "yyyy-MM-dd HH-mm-ss";

    public static string Render(string template, string app, DateTimeOffset start)
    {
        if (string.IsNullOrEmpty(template))
        {
            return app;
        }

        var builder = new StringBuilder(template.Length + 16);
        var i = 0;
        while (i < template.Length)
        {
            var open = template.IndexOf('{', i);
            if (open < 0)
            {
                builder.Append(template, i, template.Length - i);
                break;
            }

            // Copy the literal text before the token.
            if (open > i)
            {
                builder.Append(template, i, open - i);
            }

            var close = template.IndexOf('}', open + 1);
            if (close < 0)
            {
                // No closing brace: emit the rest verbatim and stop.
                builder.Append(template, open, template.Length - open);
                break;
            }

            var token = template.Substring(open + 1, close - open - 1);
            builder.Append(RenderToken(token, app, start));
            i = close + 1;
        }

        return builder.ToString();
    }

    private static string RenderToken(string token, string app, DateTimeOffset start)
    {
        if (string.Equals(token, AppToken, StringComparison.Ordinal))
        {
            return app;
        }

        if (token == StartToken)
        {
            return FormatStart(start, DefaultStartFormat);
        }

        if (token.StartsWith(StartToken + ":", StringComparison.Ordinal))
        {
            return FormatStart(start, token[(StartToken.Length + 1)..]);
        }

        // An unrecognized token is left intact rather than dropped, so a misspelled
        // placeholder is visible in the title instead of silently disappearing.
        return "{" + token + "}";
    }

    private static string FormatStart(DateTimeOffset start, string format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            format = DefaultStartFormat;
        }

        try
        {
            // The template's date format is a local wall-clock format (e.g. a filename-safe
            // timestamp), so it is applied to the local representation of the observation time.
            return start.LocalDateTime.ToString(format, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            // A malformed format string must not crash the watcher; fall back to the default.
            return start.LocalDateTime.ToString(DefaultStartFormat, CultureInfo.InvariantCulture);
        }
    }
}
