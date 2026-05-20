using System.Text.Json;
using McpLense;
using Shouldly;
using Xunit;

namespace McpLense.UnitTests.Cli;

/// <summary>
/// Tests for the <c>mcplense schema</c> verb: parser shape, embedded resource presence,
/// and JSON Schema well-formedness. The schema is shipped as a stable contract for editor
/// validation; these tests guard that contract.
/// </summary>
public class SchemaCommandTests
{
    [Fact]
    public void Parse_SchemaBare_DefaultsToConfigKind()
    {
        var parsed = CommandLineParser.Parse(["schema"]);

        parsed.Command.ShouldBe(AppCommand.Schema);
        parsed.Subject.ShouldBe("config");
        parsed.Format.ShouldBe(OutputFormat.Json);
    }

    [Fact]
    public void Parse_SchemaWithKind_RoundTrips()
    {
        var parsed = CommandLineParser.Parse(["schema", "config"]);

        parsed.Command.ShouldBe(AppCommand.Schema);
        parsed.Subject.ShouldBe("config");
        parsed.Arguments!["kind"]!.ToString().ShouldBe("config");
    }

    [Fact]
    public void Parse_SchemaUnknownKind_Throws()
    {
        Should.Throw<UserInputException>(() => CommandLineParser.Parse(["schema", "bogus"]));
    }

    [Fact]
    public void Parse_SchemaWithOutput_CapturesPath()
    {
        var parsed = CommandLineParser.Parse(["schema", "config", "--output", "out.json"]);

        parsed.Arguments!["output"]!.ToString().ShouldBe("out.json");
    }

    [Fact]
    public void Parse_SchemaWithOutputEquals_CapturesPath()
    {
        var parsed = CommandLineParser.Parse(["schema", "--output=out.json"]);

        parsed.Arguments!["output"]!.ToString().ShouldBe("out.json");
    }

    [Fact]
    public void Parse_SchemaUnknownOption_Throws()
    {
        Should.Throw<UserInputException>(() => CommandLineParser.Parse(["schema", "--bogus"]));
    }

    [Fact]
    public void EmbeddedSchemaResource_IsValidJsonSchemaShape()
    {
        // The CLI ships the schema as an embedded resource named relative to the project's
        // root namespace + file path. Reflecting on a CLI type pulls in the right assembly.
        var assembly = typeof(SchemaCommand).Assembly;
        using var stream = assembly.GetManifestResourceStream("McpLense.Cli.Cli.mcplense-config.schema.json");
        stream.ShouldNotBeNull("schema resource must be embedded; check McpLense.Cli.csproj <EmbeddedResource>");

        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        text.ShouldNotBeNullOrEmpty();

        // Round-trip parse + spot-check the required draft-2020-12 marker + the four top-level keys.
        using var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("$schema").GetString()!.ShouldContain("draft/2020-12/schema");
        doc.RootElement.GetProperty("title").GetString().ShouldBe("McpLense.Config.json");
        doc.RootElement.GetProperty("properties").GetProperty("authProfiles").ValueKind.ShouldBe(JsonValueKind.Object);
        doc.RootElement.GetProperty("properties").GetProperty("targets").ValueKind.ShouldBe(JsonValueKind.Object);
        doc.RootElement.GetProperty("properties").GetProperty("targetPatterns").ValueKind.ShouldBe(JsonValueKind.Object);
        doc.RootElement.GetProperty("properties").GetProperty("scan").ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public async Task RunAsync_WithOutputPath_WritesFile()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"mcplense-schema-{Guid.NewGuid():N}.json");
        try
        {
            var parsed = CommandLineParser.Parse(["schema", "config", "--output", temp]);
            var exit = await SchemaCommand.RunAsync(parsed);
            exit.ShouldBe(0);
            File.Exists(temp).ShouldBeTrue();
            (await File.ReadAllTextAsync(temp)).ShouldContain("McpLense.Config.json");
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
