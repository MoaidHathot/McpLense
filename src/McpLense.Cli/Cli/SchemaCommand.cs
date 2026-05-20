using System.Reflection;
using McpLense.Diagnostics;

namespace McpLense;

/// <summary>
/// Handler for <c>mcplense schema</c>. Emits the embedded JSON Schema for
/// <c>McpLense.Config.json</c> so editors (VS Code's <c>json.schemas</c>, JetBrains, etc.)
/// and AI agents can validate user-authored config without round-tripping README prose.
/// </summary>
/// <remarks>
/// The schema lives as an embedded resource alongside this file so it ships inside the
/// single CLI binary - no install-time disk write, no dependency on <c>docs/</c> being
/// present at runtime. The same JSON is also checked into <c>docs/schema/</c> for
/// browsability + CI consumers who want a stable URL.
/// </remarks>
internal static class SchemaCommand
{
    private const string ResourceName = "McpLense.Cli.Cli.mcplense-config.schema.json";

    public static async Task<int> RunAsync(ParsedCommand command)
    {
        var assembly = typeof(SchemaCommand).Assembly;
        await using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            // Defensive: surfaces the missing-embedded-resource case clearly during dev
            // rather than printing an empty schema. Should never trip in a packaged build.
            McpLenseLog.Write($"error: embedded resource '{ResourceName}' is missing from the assembly. Did the .csproj forget the EmbeddedResource entry?");
            return 1;
        }

        using var reader = new StreamReader(stream);
        var schema = await reader.ReadToEndAsync().ConfigureAwait(false);

        var output = command.Arguments?["output"]?.ToString();
        if (!string.IsNullOrEmpty(output))
        {
            await File.WriteAllTextAsync(output, schema).ConfigureAwait(false);
            McpLenseLog.Write($"wrote schema to {output}");
            return 0;
        }

        Console.WriteLine(schema);
        return 0;
    }
}
