using System;
using McpLense;
using McpLense.Scanning.TargetResolution;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Scanning.TargetResolution;

public class UrlGlobTests
{
    [Theory]
    [InlineData("https://example.com/mcp", "https://example.com/mcp", true)]
    [InlineData("https://example.com/mcp", "https://example.com/mcp/", false)] // path is anchored; trailing slash matters
    [InlineData("https://example.com/mcp", "https://example.com/other", false)]
    [InlineData("https://example.com/mcp", "http://example.com/mcp", false)] // scheme mismatch
    public void Literal_pattern_matches_exact_url(string pattern, string url, bool expected)
    {
        UrlGlob.Compile(pattern).IsMatch(new Uri(url)).ShouldBe(expected);
    }

    [Theory]
    [InlineData("https://*.example.com/mcp", "https://api.example.com/mcp", true)]
    [InlineData("https://*.example.com/mcp", "https://api.staging.example.com/mcp", false)] // * is single label
    [InlineData("https://*.example.com/mcp", "https://example.com/mcp", false)] // no leading subdomain
    public void Single_star_matches_one_host_label(string pattern, string url, bool expected)
    {
        UrlGlob.Compile(pattern).IsMatch(new Uri(url)).ShouldBe(expected);
    }

    [Theory]
    [InlineData("https://example.com/*", "https://example.com/mcp", true)]
    [InlineData("https://example.com/*", "https://example.com/a/b", false)] // * is single segment
    [InlineData("https://example.com/**", "https://example.com/mcp", true)]
    [InlineData("https://example.com/**", "https://example.com/a/b/c", true)]
    public void Path_star_vs_doublestar(string pattern, string url, bool expected)
    {
        UrlGlob.Compile(pattern).IsMatch(new Uri(url)).ShouldBe(expected);
    }

    [Theory]
    [InlineData("https://example.com/MCP", "https://example.com/MCP", true)]
    [InlineData("https://example.com/MCP", "https://example.com/mcp", false)]
    [InlineData("https://Example.COM/mcp", "https://example.com/mcp", true)] // host case-insensitive
    [InlineData("HTTPS://example.com/mcp", "https://example.com/mcp", true)] // scheme case-insensitive
    public void Case_sensitivity_split_host_vs_path(string pattern, string url, bool expected)
    {
        UrlGlob.Compile(pattern).IsMatch(new Uri(url)).ShouldBe(expected);
    }

    [Theory]
    [InlineData("https://example.com/abc?", "https://example.com/abcd", true)] // ? matches exactly one
    [InlineData("https://example.com/abc?", "https://example.com/abc", false)] // missing the one char
    [InlineData("https://example.com/abc?", "https://example.com/abcde", false)] // extra char
    public void Question_mark_matches_one_char(string pattern, string url, bool expected)
    {
        UrlGlob.Compile(pattern).IsMatch(new Uri(url)).ShouldBe(expected);
    }

    [Fact]
    public void Query_string_is_ignored_for_matching()
    {
        var glob = UrlGlob.Compile("https://example.com/mcp");
        glob.IsMatch(new Uri("https://example.com/mcp?x=1")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("example.com/mcp")] // missing scheme
    [InlineData("https://")] // empty host
    public void Compile_rejects_malformed_patterns(string pattern)
    {
        Should.Throw<UserInputException>(() => UrlGlob.Compile(pattern));
    }

    [Fact]
    public void TryCompile_returns_false_with_diagnostic_on_failure()
    {
        UrlGlob.TryCompile("nope", out var glob, out var error).ShouldBeFalse();
        glob.ShouldBeNull();
        error.ShouldNotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("https://example.com:8443/mcp", "https://example.com:8443/mcp", true)]
    [InlineData("https://example.com:8443/mcp", "https://example.com/mcp", false)] // default port differs
    [InlineData("https://example.com:443/mcp", "https://example.com:443/mcp", true)]
    public void Port_is_part_of_host_match(string pattern, string url, bool expected)
    {
        UrlGlob.Compile(pattern).IsMatch(new Uri(url)).ShouldBe(expected);
    }
}
