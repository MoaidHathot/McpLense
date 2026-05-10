using System.Collections.Generic;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Auth;

public class EnvironmentExpanderTests
{
    private static EnvironmentExpander With(IDictionary<string, string?> values)
    {
        return new EnvironmentExpander(name => values.TryGetValue(name, out var value) ? value : null);
    }

    [Fact]
    public void Expand_NullInput_ReturnsEmpty()
    {
        var expander = With(new Dictionary<string, string?>());

        expander.Expand(null, "ctx").ShouldBe(string.Empty);
    }

    [Fact]
    public void Expand_NoDollar_ReturnsAsIs()
    {
        var expander = With(new Dictionary<string, string?>());

        expander.Expand("plain text", "ctx").ShouldBe("plain text");
    }

    [Fact]
    public void Expand_EnvPrefix_ReturnsLookup()
    {
        var expander = With(new Dictionary<string, string?> { ["TOKEN"] = "abc" });

        expander.Expand("env:TOKEN", "ctx").ShouldBe("abc");
    }

    [Fact]
    public void Expand_EnvPrefixUnset_Throws()
    {
        var expander = With(new Dictionary<string, string?>());

        var ex = Should.Throw<UserInputException>(() => expander.Expand("env:MISSING", "header.Authorization"));
        ex.Message.ShouldContain("header.Authorization");
        ex.Message.ShouldContain("MISSING");
    }

    [Fact]
    public void Expand_EnvPrefixWithoutName_Throws()
    {
        var expander = With(new Dictionary<string, string?>());

        var ex = Should.Throw<UserInputException>(() => expander.Expand("env:", "ctx"));
        ex.Message.ShouldContain("requires a variable name");
    }

    [Fact]
    public void Expand_EnvPrefixEmptyValue_Preserved()
    {
        var expander = With(new Dictionary<string, string?> { ["E"] = string.Empty });

        expander.Expand("env:E", "ctx").ShouldBe(string.Empty);
    }

    [Fact]
    public void Expand_BraceForm_Substitutes()
    {
        var expander = With(new Dictionary<string, string?> { ["TOKEN"] = "abc" });

        expander.Expand("Bearer ${TOKEN}", "ctx").ShouldBe("Bearer abc");
    }

    [Fact]
    public void Expand_BraceFormUnset_Throws()
    {
        var expander = With(new Dictionary<string, string?>());

        var ex = Should.Throw<UserInputException>(() => expander.Expand("Bearer ${TOKEN}", "servers.x.auth.token"));
        ex.Message.ShouldContain("servers.x.auth.token");
        ex.Message.ShouldContain("TOKEN");
    }

    [Fact]
    public void Expand_BraceFormEmpty_PreservedNoError()
    {
        var expander = With(new Dictionary<string, string?> { ["TOKEN"] = string.Empty });

        expander.Expand("Bearer ${TOKEN}", "ctx").ShouldBe("Bearer ");
    }

    [Fact]
    public void Expand_BraceWithDefault_UsesDefaultWhenUnset()
    {
        var expander = With(new Dictionary<string, string?>());

        expander.Expand("${X:-fallback}", "ctx").ShouldBe("fallback");
    }

    [Fact]
    public void Expand_BraceWithDefault_UsesDefaultWhenEmpty()
    {
        var expander = With(new Dictionary<string, string?> { ["X"] = string.Empty });

        expander.Expand("${X:-fallback}", "ctx").ShouldBe("fallback");
    }

    [Fact]
    public void Expand_BraceWithDefault_UsesValueWhenSet()
    {
        var expander = With(new Dictionary<string, string?> { ["X"] = "real" });

        expander.Expand("${X:-fallback}", "ctx").ShouldBe("real");
    }

    [Fact]
    public void Expand_BraceWithEmptyDefault_AllowedWhenUnset()
    {
        var expander = With(new Dictionary<string, string?>());

        expander.Expand("[${X:-}]", "ctx").ShouldBe("[]");
    }

    [Fact]
    public void Expand_DollarDollar_BecomesLiteralDollar()
    {
        var expander = With(new Dictionary<string, string?>());

        expander.Expand("price: $$5", "ctx").ShouldBe("price: $5");
    }

    [Fact]
    public void Expand_BareDollarPreserved()
    {
        var expander = With(new Dictionary<string, string?>());

        expander.Expand("$ alone", "ctx").ShouldBe("$ alone");
        expander.Expand("end$", "ctx").ShouldBe("end$");
    }

    [Fact]
    public void Expand_UnterminatedBrace_Throws()
    {
        var expander = With(new Dictionary<string, string?> { ["X"] = "y" });

        var ex = Should.Throw<UserInputException>(() => expander.Expand("Bearer ${X", "ctx"));
        ex.Message.ShouldContain("unterminated");
    }

    [Fact]
    public void Expand_EmptyBraceName_Throws()
    {
        var expander = With(new Dictionary<string, string?>());

        var ex = Should.Throw<UserInputException>(() => expander.Expand("${}", "ctx"));
        ex.Message.ShouldContain("requires a variable name");
    }

    [Fact]
    public void Expand_MultipleSubstitutions()
    {
        var expander = With(new Dictionary<string, string?>
        {
            ["A"] = "alpha",
            ["B"] = "beta"
        });

        expander.Expand("${A}-${B}-${C:-gamma}", "ctx").ShouldBe("alpha-beta-gamma");
    }

    [Fact]
    public void Expand_EnvPrefixOnlyAtStart_NotMidString()
    {
        // 'env:VAR' is whole-string only; embedded does not trigger lookup
        var expander = With(new Dictionary<string, string?> { ["VAR"] = "x" });

        expander.Expand("prefix env:VAR", "ctx").ShouldBe("prefix env:VAR");
    }
}
