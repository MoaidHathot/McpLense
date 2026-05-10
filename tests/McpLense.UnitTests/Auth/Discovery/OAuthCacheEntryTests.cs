using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Auth.Discovery;

public class OAuthCacheEntryTests
{
    private static OAuthCacheEntry NewEntry(DateTimeOffset? expiresAt) =>
        new(
            ClientId: "cid",
            AccessToken: "token",
            TokenEndpoint: "https://idp/token",
            RedirectUri: "http://127.0.0.1:5050/callback",
            ExpiresAt: expiresAt);

    [Fact]
    public void IsExpired_NoExpiry_ReturnsFalse()
    {
        var entry = NewEntry(expiresAt: null);

        entry.IsExpired(TimeSpan.FromSeconds(60)).ShouldBeFalse();
    }

    [Fact]
    public void IsExpired_BeforeExpiry_ReturnsFalse()
    {
        var now = DateTimeOffset.Parse("2024-01-01T12:00:00Z");
        var entry = NewEntry(now.AddMinutes(10));

        entry.IsExpired(TimeSpan.FromSeconds(60), now).ShouldBeFalse();
    }

    [Fact]
    public void IsExpired_WithinSkew_ReturnsTrue()
    {
        var now = DateTimeOffset.Parse("2024-01-01T12:00:00Z");
        // Token expires 30s in the future but skew is 60s -> treated as expired.
        var entry = NewEntry(now.AddSeconds(30));

        entry.IsExpired(TimeSpan.FromSeconds(60), now).ShouldBeTrue();
    }

    [Fact]
    public void IsExpired_AlreadyPast_ReturnsTrue()
    {
        var now = DateTimeOffset.Parse("2024-01-01T12:00:00Z");
        var entry = NewEntry(now.AddSeconds(-1));

        entry.IsExpired(TimeSpan.Zero, now).ShouldBeTrue();
    }

    [Fact]
    public void RecordEquality_AcrossInstances_Match()
    {
        var entry1 = new OAuthCacheEntry("cid", "token", "https://idp/token", "http://localhost/cb");
        var entry2 = new OAuthCacheEntry("cid", "token", "https://idp/token", "http://localhost/cb");

        entry1.ShouldBe(entry2);
    }
}
