namespace MeetCap.Persistence.Storage;

using Microsoft.Data.Sqlite;

/// <summary>
/// Small helpers shared by the repositories: parameter lists and the canonical
/// storage format for timestamps.
/// </summary>
internal static class SqlList
{
    /// <summary>Builds <c>@p0, @p1, ...</c> for an <c>IN</c> clause.</summary>
    public static string Placeholders(int count)
        => string.Join(", ", Enumerable.Range(0, count).Select(i => "@p" + i));
}

internal static class SqlListParameters
{
    /// <summary>Binds a string list to the placeholders produced by <see cref="SqlList.Placeholders"/>.</summary>
    public static void Add(SqliteCommand command, IReadOnlyList<string> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            command.Parameters.AddWithValue("@p" + i, values[i]);
        }
    }
}

/// <summary>
/// Canonical text format for timestamps stored in SQLite. Round-trippable and
/// human-readable, matching the examples in docs/DATA_MODEL.md.
/// </summary>
internal static class SqlTimestamp
{
    public static string Format(DateTimeOffset value)
        => value.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture);

    public static string? Format(DateTimeOffset? value)
        => value is null ? null : Format(value.Value);

    public static DateTimeOffset? Parse(string? value)
        => string.IsNullOrEmpty(value)
            ? null
            : DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);

    public static DateTimeOffset ParseRequired(string value)
        => Parse(value) ?? throw new InvalidOperationException("Stored timestamp was empty.");
}
