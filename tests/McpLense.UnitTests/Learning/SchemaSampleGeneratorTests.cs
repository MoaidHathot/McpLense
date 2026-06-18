using System.Text.Json.Nodes;
using McpLense.Learning;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Learning;

public class SchemaSampleGeneratorTests
{
    private static JsonNode Gen(string schemaJson) => SchemaSampleGenerator.Generate(JsonNode.Parse(schemaJson));

    [Fact]
    public void Generate_ObjectWithTypedProps_FillsByType()
    {
        var sample = Gen("""
        {"type":"object","properties":{
          "name":{"type":"string"},
          "count":{"type":"integer"},
          "ratio":{"type":"number"},
          "on":{"type":"boolean"}
        }}
        """);

        sample["name"]!.GetValue<string>().ShouldBe("");
        sample["count"]!.GetValue<int>().ShouldBe(0);
        sample["ratio"]!.GetValue<int>().ShouldBe(0);
        sample["on"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void Generate_HonorsDefault()
        => Gen("""{"type":"object","properties":{"x":{"type":"integer","default":42}}}""")["x"]!.GetValue<int>().ShouldBe(42);

    [Fact]
    public void Generate_HonorsEnumFirstValue()
        => Gen("""{"type":"object","properties":{"mode":{"type":"string","enum":["fast","slow"]}}}""")["mode"]!.GetValue<string>().ShouldBe("fast");

    [Fact]
    public void Generate_NestedObjectAndArray()
    {
        var sample = Gen("""
        {"type":"object","properties":{
          "tags":{"type":"array","items":{"type":"string"}},
          "nested":{"type":"object","properties":{"id":{"type":"integer"}}}
        }}
        """);

        var tags = sample["tags"]!.AsArray();
        tags.Count.ShouldBe(1);
        tags[0]!.GetValue<string>().ShouldBe("");
        sample["nested"]!["id"]!.GetValue<int>().ShouldBe(0);
    }

    [Fact]
    public void Generate_PropertiesWithoutType_TreatedAsObject()
        => Gen("""{"properties":{"a":{"type":"string"}}}""")["a"]!.GetValue<string>().ShouldBe("");

    [Fact]
    public void Generate_UnionTypePicksNonNull()
        => Gen("""{"type":"object","properties":{"x":{"type":["string","null"]}}}""")["x"]!.GetValue<string>().ShouldBe("");

    [Fact]
    public void Generate_NullOrEmptySchema_YieldsEmptyObject()
    {
        SchemaSampleGenerator.Generate(null).ShouldBeOfType<JsonObject>().Count.ShouldBe(0);
        Gen("{}").ShouldBeOfType<JsonObject>().Count.ShouldBe(0);
    }

    [Fact]
    public void RequiredProperties_AreListedSorted()
        => SchemaSampleGenerator.RequiredProperties(JsonNode.Parse("""{"type":"object","required":["b","a"],"properties":{}}"""))
            .ShouldBe(new[] { "a", "b" });

    [Fact]
    public void Generate_OneOfTakesFirstBranch()
        => Gen("""{"oneOf":[{"type":"string"},{"type":"integer"}]}""").GetValue<string>().ShouldBe("");
}
