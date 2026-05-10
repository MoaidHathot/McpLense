using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Auth.Discovery;

public class OAuthTokenCacheTests
{
    private static string NewTempDir(string label)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcplense-tokens-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task ResolveCacheKey_NullName_DerivesStableHashFromResource()
    {
        var first = IOAuthTokenCache.ResolveCacheKey(null, "https://example.com/mcp");
        var second = IOAuthTokenCache.ResolveCacheKey(null, "https://example.com/mcp");
        var different = IOAuthTokenCache.ResolveCacheKey(null, "https://other.example.com/mcp");

        first.ShouldStartWith("resource-");
        first.Length.ShouldBe("resource-".Length + 16);
        first.ShouldBe(second);
        first.ShouldNotBe(different);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData("plain-name", "plain-name")]
    [InlineData("MyCache", "MyCache")]
    [InlineData("with spaces", "with-spaces")]
    [InlineData("path/with/slashes", "path-with-slashes")]
    [InlineData("../../etc", "etc")]
    [InlineData("dots.are.fine", "dots.are.fine")]
    public void ResolveCacheKey_ExplicitName_IsSlugified(string raw, string expected)
    {
        IOAuthTokenCache.ResolveCacheKey(raw, "https://anything").ShouldBe(expected);
    }

    [Fact]
    public async Task SaveLoad_RoundtripsEntry()
    {
        var dir = NewTempDir("roundtrip");
        try
        {
            var cache = new OAuthTokenCache(dir, useDpapi: false);
            var entry = new OAuthCacheEntry(
                ClientId: "cid",
                AccessToken: "access",
                TokenEndpoint: "https://idp/token",
                RedirectUri: "http://127.0.0.1:5050/callback",
                Issuer: "https://idp",
                ClientSecret: "secret",
                RefreshToken: "rtoken",
                ExpiresAt: DateTimeOffset.Parse("2030-01-01T00:00:00Z"),
                Scope: "mcp.read mcp.write",
                ResourceUri: "https://example.com/mcp",
                RegistrationEndpoint: "https://idp/register");

            await cache.SaveAsync("key1", entry, CancellationToken.None);
            var loaded = await cache.LoadAsync("key1", CancellationToken.None);

            loaded.ShouldBe(entry);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Load_MissingFile_ReturnsNull()
    {
        var dir = NewTempDir("missing");
        try
        {
            var cache = new OAuthTokenCache(dir, useDpapi: false);

            var loaded = await cache.LoadAsync("nope", CancellationToken.None);

            loaded.ShouldBeNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Load_CorruptFile_ReturnsNull()
    {
        var dir = NewTempDir("corrupt");
        try
        {
            var cache = new OAuthTokenCache(dir, useDpapi: false);
            await File.WriteAllTextAsync(Path.Combine(dir, "bad.json"), "{ this is not valid json");

            var loaded = await cache.LoadAsync("bad", CancellationToken.None);

            loaded.ShouldBeNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Delete_Existing_ReturnsTrueAndRemovesFile()
    {
        var dir = NewTempDir("delete");
        try
        {
            var cache = new OAuthTokenCache(dir, useDpapi: false);
            var entry = new OAuthCacheEntry("cid", "access", "https://idp/t", "http://cb");
            await cache.SaveAsync("zap", entry, CancellationToken.None);

            var removed = await cache.DeleteAsync("zap", CancellationToken.None);

            removed.ShouldBeTrue();
            File.Exists(Path.Combine(dir, "zap.json")).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Delete_Missing_ReturnsFalse()
    {
        var dir = NewTempDir("delete-missing");
        try
        {
            var cache = new OAuthTokenCache(dir, useDpapi: false);

            var removed = await cache.DeleteAsync("ghost", CancellationToken.None);

            removed.ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Save_OverwritesExistingEntry()
    {
        var dir = NewTempDir("overwrite");
        try
        {
            var cache = new OAuthTokenCache(dir, useDpapi: false);
            await cache.SaveAsync("k", new OAuthCacheEntry("cid", "old", "https://idp/t", "http://cb"), CancellationToken.None);
            await cache.SaveAsync("k", new OAuthCacheEntry("cid", "new", "https://idp/t", "http://cb"), CancellationToken.None);

            var loaded = await cache.LoadAsync("k", CancellationToken.None);

            loaded!.AccessToken.ShouldBe("new");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
