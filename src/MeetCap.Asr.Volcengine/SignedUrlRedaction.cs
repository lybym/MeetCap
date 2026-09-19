namespace MeetCap.Asr.Volcengine;

/// <summary>
/// Derives the spellings of a presigned URL that a provider error message can realistically
/// quote it in, so the adapter can replace the credential before the message becomes durable.
/// </summary>
/// <remarks>
/// <para>
/// A presigned URL is the whole credential: the object stays private, and the signature in the
/// query string is the only thing that opens it for the life of the signature. Scrubbing it is
/// therefore not a nicety, and matching it only byte-for-byte is not enough — see
/// <see cref="VariantsOf"/> (<c>docs/DATA_MODEL.md</c> section 6.2).
/// </para>
/// <para>
/// This is a pure function over a string so both halves — what is masked and what is left
/// alone — are directly testable.
/// </para>
/// </remarks>
internal static class SignedUrlRedaction
{
    /// <summary>
    /// The spellings of <paramref name="url"/> that must be replaced, credential first so a
    /// replacement never leaves an unmasked tail of an already-replaced string.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three shapes cover what a provider can echo back:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// the URL as it was sent;
    /// </description></item>
    /// <item><description>
    /// the URL under the other scheme, because <c>http</c> / <c>https</c> is the one part a
    /// re-serializing proxy or SDK is most likely to rewrite, and it does not change what the
    /// string identifies;
    /// </description></item>
    /// <item><description>
    /// the query string, with its leading <c>?</c>, without it, and again with each <c>&amp;</c>
    /// written as the JSON escape <c>\u0026</c>. The separators are exactly where a match against
    /// the URL in MeetCap's own spelling stops matching: a provider that quotes only the query, or
    /// that re-escapes it inside a JSON body, would otherwise keep its signature readable.
    /// </description></item>
    /// </list>
    /// <para>
    /// The host and object path are deliberately <em>not</em> masked: neither is a credential, the
    /// durable <c>tos_bucket</c>/<c>tos_object_key</c> columns already carry them, and the release
    /// message names them so an operator can find the object. What identifies and opens the object
    /// is the query's signature, and that is what these variants remove.
    /// </para>
    /// <para>
    /// A URL that does not parse yields just itself: it is still a secret worth replacing, and
    /// guessing at a split would risk masking text that is not the URL.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> VariantsOf(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var variants = new List<string>(6);

        // Longest spelling first: each query form is a suffix of the URL it belongs to, so ordering
        // by length keeps every intermediate step well formed.
        Add(variants, url);

        if (Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            var query = parsed.Query;
            if (!string.IsNullOrEmpty(query))
            {
                var bare = query[1..];
                if (bare.Length > 0)
                {
                    Add(variants, query);
                    Add(variants, bare);
                    // The same list as a JSON body spells it: `&` becomes `\u0026`, so the two
                    // spellings above stop matching there even though the credential is unchanged.
                    Add(variants, JsonEscaped(bare));
                }
            }

            Add(variants, SwapScheme(url, parsed.Scheme));
        }

        return variants
            .OrderByDescending(static variant => variant.Length)
            .ToList();
    }

    /// <summary>
    /// The bare query with every parameter separator written as the JSON escape <c>\u0026</c>.
    /// </summary>
    private static string JsonEscaped(string bareQuery) =>
        bareQuery.Replace("&", "\\u0026", StringComparison.Ordinal);

    private static void Add(List<string> variants, string candidate)
    {
        if (!string.IsNullOrEmpty(candidate)
            && !variants.Contains(candidate, StringComparer.Ordinal))
        {
            variants.Add(candidate);
        }
    }

    /// <summary>
    /// The same URL under <c>http</c> instead of <c>https</c>, or the reverse.
    /// </summary>
    /// <remarks>
    /// Only the two schemes a TOS endpoint can be addressed with are generated; anything else is
    /// returned unchanged so this never invents a string that is not a spelling of the URL.
    /// </remarks>
    private static string SwapScheme(string url, string scheme) =>
        string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? string.Concat(Uri.UriSchemeHttp, url.AsSpan(Uri.UriSchemeHttps.Length))
            : string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                ? string.Concat(Uri.UriSchemeHttps, url.AsSpan(Uri.UriSchemeHttp.Length))
                : url;
}
