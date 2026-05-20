using McpLense;
using McpLense.Scanning;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Mcp;

/// <summary>
/// Locks in the wire contract for the consumer-requested first-class accessibility +
/// RFC list. These derivations are pure functions of (classification, details); the
/// pipeline produces them once so every consumer sees the same answer instead of
/// reimplementing the disambiguation themselves.
/// </summary>
public class AuthScannerDerivationTests
{
    [Theory]
    [InlineData(AuthClassifications.Stdio, ServerAccessibility.Accessible)]
    [InlineData(AuthClassifications.Anonymous, ServerAccessibility.Accessible)]
    [InlineData(AuthClassifications.OAuthRfc9728, ServerAccessibility.RequiresAuth)]
    [InlineData(AuthClassifications.OAuthBearerUnannounced, ServerAccessibility.RequiresAuth)]
    [InlineData(AuthClassifications.AuthRequiredUnspecified, ServerAccessibility.RequiresAuth)]
    public void ServerStatus_MapsKnownClassifications(string classification, string expected)
    {
        AuthScanner.DeriveServerStatus(classification, new AuthScanDetails())
            .ShouldBe(expected);
    }

    [Fact]
    public void ServerStatus_404_OnUnknown_IsNotFound()
    {
        AuthScanner.DeriveServerStatus(AuthClassifications.Unknown, new AuthScanDetails(StatusCode: 404))
            .ShouldBe(ServerAccessibility.NotFound);
    }

    [Fact]
    public void ServerStatus_410_OnUnknown_IsNotFound()
    {
        AuthScanner.DeriveServerStatus(AuthClassifications.Unknown, new AuthScanDetails(StatusCode: 410))
            .ShouldBe(ServerAccessibility.NotFound);
    }

    [Fact]
    public void ServerStatus_NoStatusCode_WithProbeError_IsUnreachable()
    {
        AuthScanner.DeriveServerStatus(AuthClassifications.Unknown, new AuthScanDetails(ProbeError: "DNS failure"))
            .ShouldBe(ServerAccessibility.Unreachable);
    }

    [Fact]
    public void ServerStatus_Unknown_NoSignal_IsUnknown()
    {
        AuthScanner.DeriveServerStatus(AuthClassifications.Unknown, new AuthScanDetails())
            .ShouldBe(ServerAccessibility.Unknown);
    }

    [Theory]
    [InlineData(AuthClassifications.Anonymous)]
    [InlineData(AuthClassifications.Stdio)]
    [InlineData(AuthClassifications.AuthRequiredUnspecified)]
    [InlineData(AuthClassifications.Unknown)]
    public void Rfcs_EmptyForNonOAuth(string classification)
    {
        AuthScanner.DeriveRfcs(classification).ShouldBeEmpty();
    }

    [Fact]
    public void Rfcs_Rfc9728_IncludesBaseTrio()
    {
        AuthScanner.DeriveRfcs(AuthClassifications.OAuthRfc9728)
            .ShouldBe(new[] { "RFC 9728", "RFC 6750", "RFC 8414" });
    }

    [Fact]
    public void Rfcs_BearerUnannounced_IsOnlyRfc6750()
    {
        AuthScanner.DeriveRfcs(AuthClassifications.OAuthBearerUnannounced)
            .ShouldBe(new[] { "RFC 6750" });
    }
}
