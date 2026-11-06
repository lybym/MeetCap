namespace MeetCap.Asr.Volcengine;

using System.Globalization;
using System.Text;
using MeetCap.Core.Asr;

/// <summary>One resolved request-level Seed-ASR hotword and its provider boost weight.</summary>
public sealed record VolcengineHotword(string Word, int Weight);

/// <summary>
/// The resolved inline hotword list plus advisory warnings produced while building it
/// (for example, that the merged list exceeded the 5,000-word cap and was truncated).
/// </summary>
internal sealed record VolcengineHotwordResolution(
    IReadOnlyList<VolcengineHotword> Words,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Loads console-compatible word|N entries once, before a provider can submit audio.
/// The per-request corpus is reproducible even if a hotword file later changes.
/// </summary>
internal static class VolcengineHotwords
{
    internal const int MaxWords = 5000;
    internal const int DefaultWeight = 4;

    internal static VolcengineHotwordResolution Resolve(
        IReadOnlyList<string>? inline,
        string? filePath)
    {
        var words = new List<VolcengineHotword>();
        var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;

        void Add(string entry, string source)
        {
            var hotword = Parse(entry, source);
            // The explicit inline array takes precedence over file entries.
            if (!seen.Add(hotword.Word))
            {
                return;
            }

            if (words.Count >= MaxWords)
            {
                // Cap the merged distinct list at 5,000 by input order and warn,
                // rather than rejecting the whole deployment: a long per-meeting
                // vocabulary is a valid operator choice, and the warning makes the
                // truncation observable instead of silently dropping the tail.
                truncated = true;
                return;
            }

            words.Add(hotword);
        }

        if (inline is not null)
        {
            for (var i = 0; i < inline.Count; i++)
            {
                Add(inline[i], $"asr.volcengine.hotwords[{i}]");
            }
        }

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            if (!Path.IsPathFullyQualified(filePath))
            {
                throw new AsrConfigurationException(
                    "asr.volcengine.hotwords_file must be an absolute path; " +
                    "the CLI working directory is not a stable hotword-file root.");
            }

            try
            {
                var lineNumber = 0;
                foreach (var line in File.ReadLines(filePath, new UTF8Encoding(false, true)))
                {
                    lineNumber++;
                    if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                    {
                        continue;
                    }

                    Add(line, $"asr.volcengine.hotwords_file line {lineNumber}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                throw new AsrConfigurationException(
                    $"Cannot read asr.volcengine.hotwords_file at '{filePath}': {ex.GetType().Name}. " +
                    "Verify the path, file encoding (UTF-8), and read permission.");
            }
        }

        if (truncated)
        {
            warnings.Add(
                "asr.volcengine inline hotwords exceeded 5000 distinct terms; " +
                "the merged list was truncated to the first 5000 by input order. " +
                "Later duplicates and the tail of the list were dropped. Review the " +
                "configuration so the retained words are the intended ones.");
        }

        return new VolcengineHotwordResolution(words, warnings);
    }

    private static VolcengineHotword Parse(string? entry, string source)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            throw Invalid(source, "the word must not be empty");
        }

        var trimmed = entry.Trim();
        var separator = trimmed.LastIndexOf('|');
        var word = separator < 0 ? trimmed : trimmed[..separator].Trim();
        var weight = DefaultWeight;

        if (separator >= 0)
        {
            var weightText = trimmed[(separator + 1)..];
            if (!int.TryParse(weightText, NumberStyles.None, CultureInfo.InvariantCulture, out weight)
                || weight is < 1 or > 10)
            {
                throw Invalid(source, "the weight must be an integer from 1 to 10 (word|N)");
            }
        }

        if (word.Length == 0)
        {
            throw Invalid(source, "the word must not be empty");
        }

        var count = 0;
        var anyLetter = false;
        foreach (var rune in word.EnumerateRunes())
        {
            count++;
            if (Rune.IsLetter(rune))
            {
                anyLetter = true;
            }
            else if (!Rune.IsDigit(rune) && rune.Value != ' ')
            {
                throw Invalid(source, "only letters, digits and ordinary spaces are supported");
            }
        }

        if (count > 10)
        {
            throw Invalid(source, "a hotword must contain no more than 10 characters");
        }

        if (!anyLetter)
        {
            throw Invalid(source, "digit-only hotwords must use Chinese character forms");
        }

        return new VolcengineHotword(word, weight);
    }

    private static AsrConfigurationException Invalid(string source, string reason) =>
        new($"Invalid {source}: {reason}. Fix the entry before submitting file ASR.");
}
