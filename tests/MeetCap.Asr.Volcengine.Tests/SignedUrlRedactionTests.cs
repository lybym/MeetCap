using Xunit;

namespace MeetCap.Asr.Volcengine.Tests;

/// <summary>
/// Unit tests for the spellings of a presigned URL the adapter masks.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam review finding P1-1 asked for: a presigned URL is the whole credential for a
/// private object, and the provider can quote it back in an error body. The provider-level tests
/// assert the end-to-end behaviour against a scripted HTTP handler; these assert the pure function
/// underneath, including the shapes that behaviour test does not reach.
/// </para>
/// <para>
/// No credentials are used: the values here are obviously fake and no request is made
/// (<c>docs/DEVELOPMENT.md</c> section 7).
/// </para>
/// </remarks>
public class SignedUrlRedactionTests
{
    private const string Url =
        "https://meetcap-asr.tos-cn-beijing.volces.com/meetcap-asr/ab/2026/09/19/job_1.wav" +
        "?X-Tos-Algorithm=TOS4-HMAC-SHA256&X-Tos-Date=20260919T101500Z&X-Tos-Expires=21600" +
        "&X-Tos-Signature=deadbeefcafef00d";

    private const string Query =
        "X-Tos-Algorithm=TOS4-HMAC-SHA256&X-Tos-Date=20260919T101500Z&X-Tos-Expires=21600" +
        "&X-Tos-Signature=deadbeefcafef00d";

    [Fact]
    public void VariantsOf_ContainsTheCredentialUnderEverySchemeAndSeparatorSpelling()
    {
        var variants = SignedUrlRedaction.VariantsOf(Url);

        // The URL as sent, and the same URL under the other scheme: `http` / `https` is the one
        // part a re-serializing hop is most likely to rewrite, and it identifies the same object.
        Assert.Contains(Url, variants);
        Assert.Contains(Url.Replace("https://", "http://", StringComparison.Ordinal), variants);

        // The query with and without its `?`, because a provider may quote the parameter list on
        // its own, and because the `?` is not part of the credential.
        Assert.Contains("?" + Query, variants);
        Assert.Contains(Query, variants);

        // The JSON spelling of the same list: a body that escapes `&` to `\u0026` contains none of
        // the strings above, so without this variant its signature would survive the mask.
        Assert.Contains(Query.Replace("&", "\\u0026", StringComparison.Ordinal), variants);
    }

    [Fact]
    public void VariantsOf_OrdersLongestFirstSoNoIntermediateReplacementLeavesATail()
    {
        // The query is a suffix of the URL, so replacing the shorter spelling first would consume
        // the tail and leave the head of a longer spelling pointing at a string that no longer
        // exists. Ordering longest-first is what makes the caller's sequential replace total.
        var variants = SignedUrlRedaction.VariantsOf(Url);

        var ordered = variants.OrderByDescending(v => v.Length).ToArray();
        Assert.Equal(ordered, variants);
        Assert.Equal(Url, variants[0]);
    }

    [Fact]
    public void VariantsOf_DoesNotMaskTheHostOrTheObjectPath()
    {
        // Neither is a credential: the durable tos_bucket / tos_object_key columns already carry
        // them, and the release message names them so an operator can find the object. Masking
        // them would cost the diagnosis without removing any secret.
        var variants = SignedUrlRedaction.VariantsOf(Url);

        Assert.DoesNotContain(
            "meetcap-asr.tos-cn-beijing.volces.com/meetcap-asr/ab/2026/09/19/job_1.wav",
            variants);
        Assert.DoesNotContain("meetcap-asr/ab/2026/09/19/job_1.wav", variants);
    }

    [Theory]
    // A URL the adapter could not have minted but that still carries a signature: the whole string
    // is the only thing worth replacing, so it is the only variant.
    [InlineData("not-a-url-but-still-a-secret")]
    [InlineData("https://host/key.wav?X-Tos-Signature=deadbeef")]
    [InlineData("/relative/path?X-Tos-Signature=deadbeef")]
    public void VariantsOf_AlwaysContainsTheInputItself(string url)
    {
        Assert.Contains(url, SignedUrlRedaction.VariantsOf(url));
    }

    [Fact]
    public void VariantsOf_AUrlWithoutAQueryYieldsTheUrlAndItsOtherScheme()
    {
        // Nothing to split, so nothing may be invented. The object is still named, and the two
        // schemes are the only two spellings a TOS endpoint can be addressed with.
        var variants = SignedUrlRedaction.VariantsOf("https://host/key.wav");

        Assert.Equal(2, variants.Count);
        Assert.Contains("https://host/key.wav", variants);
        Assert.Contains("http://host/key.wav", variants);
    }

    [Fact]
    public void VariantsOf_RejectsABlankUrlRatherThanReturningAnEmptySecretSet()
    {
        // A blank entry in the scrub set would be a silently useless mask (string.Replace with an
        // empty search string throws), so it is rejected here instead of at the call site.
        Assert.Throws<ArgumentException>(() => SignedUrlRedaction.VariantsOf(string.Empty));
        Assert.Throws<ArgumentException>(() => SignedUrlRedaction.VariantsOf("   "));
    }

    [Fact]
    public void EveryVariantOfARealUrlCarriesSomeOfTheCredential()
    {
        // Guards against a variant that is all host and no query: masking such a string would
        // remove the object's identity from a message without removing the signature that opens
        // it, which is the worst of both. Each variant must therefore either be the URL itself or
        // contain a signed query parameter.
        foreach (var variant in SignedUrlRedaction.VariantsOf(Url))
        {
            var carriesCredential = string.Equals(variant, Url, StringComparison.Ordinal)
                || variant.Contains("X-Tos-Signature", StringComparison.Ordinal)
                || variant.Contains("\\u0026X-Tos-Signature", StringComparison.Ordinal);
            Assert.True(carriesCredential, $"variant carries no credential: {variant}");
        }
    }
}
