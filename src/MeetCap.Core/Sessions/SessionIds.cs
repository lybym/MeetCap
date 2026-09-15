namespace MeetCap.Core.Sessions;

using System.Globalization;
using System.Security.Cryptography;

/// <summary>
/// Session identifiers. The format is sortable, filesystem-safe and self-describing:
/// <c>ses_yyyyMMddTHHmmssZ_xxxxxxxx</c>.
/// </summary>
public static class SessionIds
{
    public const string Prefix = "ses_";

    private const int EntropyHexChars = 8;

    /// <summary>Creates an id for a session that starts at <paramref name="utcNow"/>.</summary>
    public static string Create(DateTimeOffset utcNow)
    {
        Span<byte> entropy = stackalloc byte[EntropyHexChars / 2];
        RandomNumberGenerator.Fill(entropy);
        return Create(utcNow, entropy);
    }

    /// <summary>
    /// Deterministic variant used by tests: the caller supplies the entropy bytes
    /// that would otherwise be random.
    /// </summary>
    public static string Create(DateTimeOffset utcNow, ReadOnlySpan<byte> entropy)
    {
        var stamp = utcNow.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        return Prefix + stamp + "_" + Convert.ToHexString(entropy).ToLowerInvariant();
    }

    /// <summary>Whether <paramref name="value"/> is shaped like a MeetCap session id.</summary>
    public static bool IsValid(string? value)
    {
        const int stampLength = 16; // yyyyMMddTHHmmssZ

        if (value is null || value.Length != Prefix.Length + stampLength + 1 + EntropyHexChars)
        {
            return false;
        }

        if (!value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var body = value.AsSpan(Prefix.Length);
        if (body[stampLength] != '_')
        {
            return false;
        }

        for (var i = 0; i < stampLength; i++)
        {
            var c = body[i];
            var isDigit = c is >= '0' and <= '9';
            var isStampLetter = c is 'T' or 'Z';
            if (!isDigit && !isStampLetter)
            {
                return false;
            }
        }

        foreach (var c in body[(stampLength + 1)..])
        {
            if (c is not (>= '0' and <= '9') && c is not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
