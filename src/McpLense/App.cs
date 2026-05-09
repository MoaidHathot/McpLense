using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dumpify;

namespace McpLense;

internal static class App
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var command = CommandLineParser.Parse(args);

            if (command.Command is AppCommand.Help)
            {
                Console.WriteLine(CommandLineHelp.Text);
                return 0;
            }

            if (command.Command is AppCommand.Version)
            {
                Console.WriteLine(GetVersion());
                return 0;
            }

            if (command.Command is AppCommand.Tui)
            {
                return await TuiApp.RunAsync(command);
            }

            var result = await McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None);
            WriteOutput(command.Format, result.Payload);
            return result.HasErrors ? 1 : 0;
        }
        catch (UserInputException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(CommandLineHelp.Text);
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Operation timed out.");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static void WriteOutput(OutputFormat format, object payload)
    {
        Console.WriteLine(OutputRenderer.Render(format, payload, JsonOptions));
    }

    private static string GetVersion()
    {
        var assembly = typeof(App).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }
}

internal static class CommandLineHelp
{
    public const string Text = """
mcplense

Inspect MCP servers from a config file, a URL, or a stdio command.

Usage
  mcplense inspect [target-options] [common-options]
  mcplense tui [target-options] [common-options]
  mcplense tools [target-options] [common-options]
  mcplense resources [target-options] [common-options]
  mcplense prompts [target-options] [common-options]
  mcplense call <tool-name> [target-options] [common-options] [--args <json>]
  mcplense read <uri-or-template> [target-options] [common-options] [--args <json>]
  mcplense prompt <prompt-name> [target-options] [common-options] [--args <json>]
  mcplense help
  mcplense version

Target Options
  --config <path>              Load one or more servers from a JSON config file.
  --server <name>              Filter config servers by name. Repeat as needed.

  --url <url>                  Connect to an HTTP MCP endpoint.
  --transport <auto|streamable-http|sse>
                               HTTP transport mode. Default: auto.
  --header <name=value>        HTTP header. Repeat as needed.

  --command <command>          Launch a stdio MCP server directly.
  --command-arg <value>        Argument for --command. Repeat as needed.
  --cwd <path>                 Working directory for stdio targets.
  --env <name=value>           Environment variable for stdio targets.
  --name <value>               Display name for direct targets.

  A stdio target can also be passed after --.
  Example: mcplense inspect -- npx -y @modelcontextprotocol/server-everything

Common Options
  --format <text|json|dumpify> Output format. Default: text.
  --timeout <seconds>          Per-server timeout. Default: 30.
  --progress [true|false]      Show live tool-call progress. Default: true for call.
  -h, --help                   Show help.

Config Shapes
  Supports common MCP config files such as:

  {
    "mcpServers": {
      "everything": {
        "command": "npx",
        "args": ["-y", "@modelcontextprotocol/server-everything"]
      }
    }
  }

  Or an array/object of custom server definitions:

  {
    "servers": [
      {
        "name": "remote",
        "url": "https://example.com/mcp",
        "transport": "streamable-http",
        "headers": {
          "Authorization": "Bearer ..."
        }
      }
    ]
  }

Examples
  mcplense inspect --config mcp.json
  mcplense tui --config mcp.json
  mcplense tools --config mcp.json --server everything
  mcplense inspect --url https://localhost:3000/mcp --format json
  mcplense call echo --url https://localhost:3000/mcp --args '{"message":"hello"}'
  dotnet run -- inspect --format dumpify -- npx -y @modelcontextprotocol/server-everything
  dotnet run -- tools --format json -- npx -y @modelcontextprotocol/server-everything
  mcplense inspect -- npx -y @modelcontextprotocol/server-everything
""";
}
