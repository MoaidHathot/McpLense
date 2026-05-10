using System.Security.Cryptography;
using System.Text;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Auth.Discovery;

public class PkceHelperTests
{
    [Fact]
    public void Method_IsS256()
    {
        PkceHelper.Method.ShouldBe("S256");
    }

    [Fact]
    public void Generate_ProducesUrlSafeVerifier()
    {
        var pair = PkceHelper.Generate();

        pair.Verifier.ShouldNotBeNullOrEmpty();
        // 32 random bytes URL-safe base64 encoded with no padding -> 43 chars.
        pair.Verifier.Length.ShouldBe(43);
        pair.Verifier.ShouldMatch("^[A-Za-z0-9_-]+$");
    }

    [Fact]
    public void Generate_ChallengeIsSha256OfVerifier()
    {
        var pair = PkceHelper.Generate();

        // Code challenge MUST be the URL-safe base64 SHA256 of the ASCII verifier.
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(pair.Verifier));
        var expected = PkceHelper.Base64Url(hash);

        pair.Challenge.ShouldBe(expected);
        pair.Challenge.ShouldMatch("^[A-Za-z0-9_-]+$");
    }

    [Fact]
    public void Generate_TwoCalls_ProduceDistinctVerifiers()
    {
        var first = PkceHelper.Generate();
        var second = PkceHelper.Generate();

        first.Verifier.ShouldNotBe(second.Verifier);
    }

    [Fact]
    public void Base64Url_StripsPaddingAndUsesUrlSafeAlphabet()
    {
        var bytes = new byte[] { 0xFB, 0xEF, 0xFE };

        var encoded = PkceHelper.Base64Url(bytes);

        encoded.ShouldNotContain("=");
        encoded.ShouldNotContain("+");
        encoded.ShouldNotContain("/");
    }
}
