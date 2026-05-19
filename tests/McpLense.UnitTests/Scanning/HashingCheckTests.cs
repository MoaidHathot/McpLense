using System.Text.Json.Nodes;
using McpLense.Scanning.Checks;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Scanning;

public class HashingCheckTests
{
    [Fact]
    public void CanonicalJson_IsOrderInsensitive()
    {
        var a = JsonNode.Parse("""{"a": 1, "b": [{"y": 2, "x": 1}]}""");
        var b = JsonNode.Parse("""{"b": [{"x": 1, "y": 2}], "a": 1}""");

        HashingCheck.CanonicalJson(a).ShouldBe(HashingCheck.CanonicalJson(b));
    }

    [Fact]
    public void CanonicalJson_DetectsValueDifferences()
    {
        var a = JsonNode.Parse("""{"a": 1}""");
        var b = JsonNode.Parse("""{"a": 2}""");

        HashingCheck.CanonicalJson(a).ShouldNotBe(HashingCheck.CanonicalJson(b));
    }

    [Fact]
    public void Hash_IsStableAcrossEqualInputs()
    {
        var a = HashingCheck.Hash("hello");
        var b = HashingCheck.Hash("hello");
        a.ShouldBe(b);
        a.Length.ShouldBe(64); // SHA-256 hex
    }

    [Fact]
    public void Hash_ChangesWithInput()
    {
        HashingCheck.Hash("hello").ShouldNotBe(HashingCheck.Hash("world"));
    }
}
