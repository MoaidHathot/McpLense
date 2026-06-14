using System.Text.Json.Nodes;
using McpLense;
using Shouldly;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace McpLense.UnitTests.Interactive;

/// <summary>
/// Tests for <see cref="ArgumentElicitor"/>. The schema-parsing / coercion / template-variable /
/// equivalent-command helpers are pure and asserted directly; the <c>Elicit*</c> flows are
/// driven through a scripted <see cref="TestConsole"/>.
/// </summary>
public class ArgumentElicitorTests
{
    private static TestConsole NewConsole()
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Profile.Width = 200;
        return console;
    }

    private static JsonNode Schema(string json) => JsonNode.Parse(json)!;

    // ---------- PlanProperties ----------

    [Fact]
    public void PlanProperties_Null_ReturnsEmpty()
        => ArgumentElicitor.PlanProperties(null).Count.ShouldBe(0);

    [Fact]
    public void PlanProperties_NoPropertiesBlock_ReturnsEmpty()
        => ArgumentElicitor.PlanProperties(Schema("""{ "type": "object" }""")).Count.ShouldBe(0);

    [Fact]
    public void PlanProperties_ParsesTypeRequiredDefaultEnumDescription()
    {
        var properties = ArgumentElicitor.PlanProperties(Schema("""
        {
          "type": "object",
          "properties": {
            "message": { "type": "string", "description": "the message" },
            "count":   { "type": "integer", "default": 3 },
            "mode":    { "enum": ["x", "y"] }
          },
          "required": ["message"]
        }
        """));

        properties.Count.ShouldBe(3);

        properties[0].Name.ShouldBe("message");
        properties[0].Type.ShouldBe("string");
        properties[0].Required.ShouldBeTrue();
        properties[0].HasDefault.ShouldBeFalse();
        properties[0].Description.ShouldBe("the message");

        properties[1].Name.ShouldBe("count");
        properties[1].Type.ShouldBe("integer");
        properties[1].Required.ShouldBeFalse();
        properties[1].HasDefault.ShouldBeTrue();
        properties[1].Default!.ToJsonString().ShouldBe("3");

        properties[2].Name.ShouldBe("mode");
        properties[2].Type.ShouldBeNull();
        properties[2].EnumValues!.Count.ShouldBe(2);
    }

    [Fact]
    public void PlanProperties_TypeArray_PicksFirstNonNullType()
    {
        var properties = ArgumentElicitor.PlanProperties(Schema("""
        { "type": "object", "properties": { "x": { "type": ["null", "string"] } } }
        """));

        properties[0].Type.ShouldBe("string");
    }

    // ---------- CoerceScalar ----------

    [Theory]
    [InlineData("5", "integer", "5")]
    [InlineData("true", "boolean", "true")]
    [InlineData("[1,2]", "array", "[1,2]")]
    [InlineData("{\"a\":1}", "object", "{\"a\":1}")]
    public void CoerceScalar_TypedInputs_ProduceMatchingJson(string raw, string type, string expectedJson)
        => ArgumentElicitor.CoerceScalar(raw, type)!.ToJsonString().ShouldBe(expectedJson);

    [Fact]
    public void CoerceScalar_String_KeepsLiteralText()
        => ArgumentElicitor.CoerceScalar("hello", "string")!.GetValue<string>().ShouldBe("hello");

    [Fact]
    public void CoerceScalar_NumberType_ParsesInvariant()
        => ArgumentElicitor.CoerceScalar("3.5", "number")!.GetValue<double>().ShouldBe(3.5);

    [Fact]
    public void CoerceScalar_NoType_FallsBackToJsonThenString()
    {
        ArgumentElicitor.CoerceScalar("42", type: null)!.ToJsonString().ShouldBe("42");
        ArgumentElicitor.CoerceScalar("plain words", type: null)!.GetValue<string>().ShouldBe("plain words");
    }

    [Fact]
    public void CoerceScalar_BadInteger_Throws()
        => Should.Throw<FormatException>(() => ArgumentElicitor.CoerceScalar("notanint", "integer"));

    [Fact]
    public void CoerceScalar_ObjectExpectedButArrayGiven_Throws()
        => Should.Throw<Exception>(() => ArgumentElicitor.CoerceScalar("[1,2]", "object"));

    // ---------- DefaultToEditString ----------

    [Fact]
    public void DefaultToEditString_String_IsBare()
        => ArgumentElicitor.DefaultToEditString(JsonValue.Create("dark")).ShouldBe("dark");

    [Fact]
    public void DefaultToEditString_Number_IsLiteral()
        => ArgumentElicitor.DefaultToEditString(JsonValue.Create(5)).ShouldBe("5");

    [Fact]
    public void DefaultToEditString_Array_IsJson()
        => ArgumentElicitor.DefaultToEditString(JsonNode.Parse("[1,2]")).ShouldBe("[1,2]");

    [Fact]
    public void DefaultToEditString_Null_IsEmpty()
        => ArgumentElicitor.DefaultToEditString(null).ShouldBe(string.Empty);

    // ---------- ExtractTemplateVariables ----------

    [Theory]
    [InlineData("docs://articles/{id}", new[] { "id" })]
    [InlineData("db://{table}/{id}", new[] { "table", "id" })]
    [InlineData("search{?q,lang}", new[] { "q", "lang" })]
    [InlineData("x://{+path}/{id}", new[] { "path", "id" })]
    [InlineData("config://app/settings", new string[0])]
    [InlineData("{id}/{id}", new[] { "id" })]
    public void ExtractTemplateVariables_HandlesOperatorsAndDistinctness(string template, string[] expected)
        => ArgumentElicitor.ExtractTemplateVariables(template).ShouldBe(expected);

    // ---------- BuildEquivalentCommand ----------

    [Fact]
    public void BuildEquivalentCommand_Http_IncludesUrlAndArgs()
    {
        var args = new JsonObject { ["message"] = "hi" };
        ArgumentElicitor.BuildEquivalentCommand("call", "Echo", args, "http", "https://x/mcp")
            .ShouldBe("mcplense call Echo https://x/mcp --args '{\"message\":\"hi\"}'");
    }

    [Fact]
    public void BuildEquivalentCommand_Stdio_AppendsCommandAfterArgs()
    {
        var args = new JsonObject { ["a"] = 1 };
        ArgumentElicitor.BuildEquivalentCommand("call", "Echo", args, "stdio", "dotnet exec foo.dll")
            .ShouldBe("mcplense call Echo --args '{\"a\":1}' -- dotnet exec foo.dll");
    }

    [Fact]
    public void BuildEquivalentCommand_NoArgs_OmitsArgsFlag()
        => ArgumentElicitor.BuildEquivalentCommand("read", "config://x", arguments: null, "http", "https://x/mcp")
            .ShouldBe("mcplense read config://x https://x/mcp");

    // ---------- Elicit flows (scripted console) ----------

    [Fact]
    public void ElicitToolArguments_NoSchema_ReturnsEmpty()
        => ArgumentElicitor.ElicitToolArguments(NewConsole(), inputSchema: null).Count.ShouldBe(0);

    [Fact]
    public void ElicitToolArguments_RequiredString_OptionalSkipped()
    {
        var console = NewConsole();
        console.Input.PushTextWithEnter("hello");   // message (required)
        console.Input.PushTextWithEnter(string.Empty); // note (optional) -> skipped

        var result = ArgumentElicitor.ElicitToolArguments(console, Schema("""
        {
          "type": "object",
          "properties": { "message": { "type": "string" }, "note": { "type": "string" } },
          "required": ["message"]
        }
        """));

        result["message"]!.GetValue<string>().ShouldBe("hello");
        result.ContainsKey("note").ShouldBeFalse();
    }

    [Fact]
    public void ElicitToolArguments_IntegerDefault_AcceptedWithEnter()
    {
        var console = NewConsole();
        console.Input.PushTextWithEnter(string.Empty); // accept the shown default

        var result = ArgumentElicitor.ElicitToolArguments(console, Schema("""
        { "type": "object", "properties": { "count": { "type": "integer", "default": 7 } } }
        """));

        result["count"]!.ToJsonString().ShouldBe("7");
    }

    [Fact]
    public void ElicitToolArguments_IntegerDefault_CanBeOverridden()
    {
        var console = NewConsole();
        console.Input.PushTextWithEnter("9");

        var result = ArgumentElicitor.ElicitToolArguments(console, Schema("""
        { "type": "object", "properties": { "count": { "type": "integer", "default": 7 } } }
        """));

        result["count"]!.ToJsonString().ShouldBe("9");
    }

    [Fact]
    public void ElicitToolArguments_Enum_SelectsValue()
    {
        var console = NewConsole();
        console.Input.PushKey(ConsoleKey.DownArrow); // move to second choice "b"
        console.Input.PushKey(ConsoleKey.Enter);

        var result = ArgumentElicitor.ElicitToolArguments(console, Schema("""
        { "type": "object", "properties": { "mode": { "enum": ["a", "b", "c"] } }, "required": ["mode"] }
        """));

        result["mode"]!.GetValue<string>().ShouldBe("b");
    }

    [Fact]
    public void ElicitToolArguments_BooleanDefaultTrue_AcceptedWithEnter()
    {
        var console = NewConsole();
        console.Input.PushKey(ConsoleKey.Enter); // accept confirm default (true)

        var result = ArgumentElicitor.ElicitToolArguments(console, Schema("""
        { "type": "object", "properties": { "flag": { "type": "boolean", "default": true } } }
        """));

        result["flag"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task ElicitPromptArguments_RequiredAndOptional()
    {
        var console = NewConsole();
        console.Input.PushTextWithEnter("csharp");      // language (required)
        console.Input.PushTextWithEnter(string.Empty);  // code (optional) -> skipped

        var result = await ArgumentElicitor.ElicitPromptArgumentsAsync(console,
        [
            new PromptArgumentInfo("language", "Programming language", true),
            new PromptArgumentInfo("code", "Code to review", false)
        ]);

        result["language"]!.GetValue<string>().ShouldBe("csharp");
        result.ContainsKey("code").ShouldBeFalse();
    }

    [Fact]
    public async Task ElicitPromptArguments_WithCompletions_OffersSuggestions()
    {
        var console = NewConsole();
        console.Input.PushKey(ConsoleKey.Enter); // accept first suggestion "csharp"

        var result = await ArgumentElicitor.ElicitPromptArgumentsAsync(console,
            [new PromptArgumentInfo("language", null, true)],
            new FakeCompletions("csharp", "python"));

        result["language"]!.GetValue<string>().ShouldBe("csharp");
    }

    [Fact]
    public async Task ElicitPromptArguments_WithCompletions_CustomValueFallsBackToText()
    {
        var console = NewConsole();
        console.Input.PushKey(ConsoleKey.DownArrow); // a -> b
        console.Input.PushKey(ConsoleKey.DownArrow); // b -> (enter a custom value)
        console.Input.PushKey(ConsoleKey.Enter);
        console.Input.PushTextWithEnter("zzz");

        var result = await ArgumentElicitor.ElicitPromptArgumentsAsync(console,
            [new PromptArgumentInfo("language", null, true)],
            new FakeCompletions("a", "b"));

        result["language"]!.GetValue<string>().ShouldBe("zzz");
    }

    [Fact]
    public async Task ElicitTemplateVariables_PromptsForEachVariable()
    {
        var console = NewConsole();
        console.Input.PushTextWithEnter("42");

        var result = await ArgumentElicitor.ElicitTemplateVariablesAsync(console, "docs://articles/{id}");

        result["id"]!.GetValue<string>().ShouldBe("42");
    }

    [Fact]
    public async Task ElicitTemplateVariables_WithCompletions_OffersSuggestions()
    {
        var console = NewConsole();
        console.Input.PushKey(ConsoleKey.DownArrow); // 1 -> 2
        console.Input.PushKey(ConsoleKey.Enter);

        var result = await ArgumentElicitor.ElicitTemplateVariablesAsync(console, "docs://articles/{id}", new FakeCompletions("1", "2"));

        result["id"]!.GetValue<string>().ShouldBe("2");
    }

    [Fact]
    public async Task ElicitTemplateVariables_NoVariables_ReturnsEmpty()
        => (await ArgumentElicitor.ElicitTemplateVariablesAsync(NewConsole(), "config://app/settings")).Count.ShouldBe(0);

    private sealed class FakeCompletions(params string[] values) : ICompletionSource
    {
        public Task<IReadOnlyList<string>> CompleteAsync(string argumentName, string partialValue, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>(values);
    }
}
