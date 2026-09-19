using MeetCap.Core.Secrets;
using Xunit;

namespace MeetCap.Core.Tests.Secrets;

/// <summary>
/// Credentials must fail visibly rather than resolving to an empty secret, and the
/// environment lookup is injected so nothing else in the codebase reads ad-hoc
/// environment variables (docs/ARCHITECTURE.md section 18).
/// </summary>
public class CredentialResolverTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] values)
    {
        var map = values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        return name => map.TryGetValue(name, out var value) ? value : null;
    }

    [Fact]
    public void EnvReference_ResolvesThroughTheInjectedLookup()
    {
        var resolved = CredentialResolver.Resolve(
            "env:MEETCAP_VOLCENGINE_API_KEY",
            Env(("MEETCAP_VOLCENGINE_API_KEY", "token-value")));

        Assert.Equal("token-value", resolved);
    }

    [Fact]
    public void MissingEnvironmentVariable_FailsWithTheVariableName()
    {
        var ex = Assert.Throws<CredentialResolutionException>(
            () => CredentialResolver.Resolve("env:MEETCAP_MISSING", Env()));

        Assert.Contains("MEETCAP_MISSING", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not set", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("env:")]
    public void EmptyReference_FailsVisibly(string? reference)
    {
        Assert.Throws<CredentialResolutionException>(() => CredentialResolver.Resolve(reference, Env()));
    }

    [Fact]
    public void CredentialManagerScheme_IsRejectedAsNotImplementedInsteadOfSendingNothing()
    {
        var ex = Assert.Throws<CredentialResolutionException>(
            () => CredentialResolver.Resolve("credman:MeetCap/volcengine", Env()));

        Assert.Contains("not implemented", ex.Message, StringComparison.Ordinal);
        Assert.Contains("env:", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LiteralValue_IsAcceptedForDeploymentsThatCannotUseEnvironmentVariables()
    {
        Assert.Equal("literal-token", CredentialResolver.Resolve("literal-token", Env()));
    }
}
