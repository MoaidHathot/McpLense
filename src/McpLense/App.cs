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
        catch (McpLenseAuthException ex)
        {
            Console.Error.WriteLine($"Authentication error: {ex.Message}");
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

Authentication
  --auth <bearer|oauth|interactive-browser>
                               Auth scheme to use for HTTP/SSE targets.
  --auth-token <value>         Bearer token (only used with '--auth bearer').
                               Supports environment expansion:
                                 - 'env:VAR'           (whole-string)
                                 - '${VAR}' / '${VAR:-default}'  (substring)
  --scope <scope>              OAuth scope to request. Repeat as needed.
                               For interactive-browser auth, use the
                               '<application-id-uri>/.default' Entra shape.
  --redirect-uri <uri>         Loopback redirect URI for the OAuth flow.
                               Defaults to a free port on http://127.0.0.1
                               (OAuth) or http://localhost (interactive-browser).
  --token-cache-name <name>    Override the token cache key. Defaults to a stable
                               hash of the resource URI (OAuth) or 'mcplense'
                               (interactive-browser). Set to 'mcp-proxy' to share
                               the MSAL cache with the mcp-proxy tool.
  --client-id <value>          Pre-registered public-client GUID for OAuth or
                               interactive-browser auth. Required for
                               interactive-browser. Supports environment expansion.
  --tenant-id <value>          Entra tenant id (GUID, domain, or 'common',
                               'organizations', 'consumers'). Only used by
                               interactive-browser auth. Supports environment
                               expansion.
  --no-auth                    Suppress all authentication on every resolved server.

  --login                      Run the OAuth flow once for each resolved HTTP server,
                               cache the resulting token, and exit. The selected
                               command (e.g. 'inspect') is ignored on this path; only
                               the target options matter.
  --logout                     Delete cached OAuth tokens for each resolved HTTP
                               server and exit.

  Auth precedence:
    1. --no-auth wins absolutely (no Authorization header sent anywhere).
    2. --auth <type> replaces any 'auth' block in the config.
    3. --auth-token / --scope / --redirect-uri / --token-cache-name /
       --client-id / --tenant-id overlay individual fields onto the resolved
       auth block.

  OAuth notes:
    - Cached tokens live under '%LOCALAPPDATA%\McpLense\tokens' (Windows, DPAPI)
      or '$XDG_DATA_HOME/mcplense/tokens' (Linux/macOS, chmod 600 JSON).
    - Authorization Server discovery tries (in order): RFC 8414 strict path-insert,
      then the OIDC-style path-append variant, then OIDC openid-configuration.
      The OIDC fallback covers servers like Microsoft Entra ID v2.0 that do not
      publish RFC 8414 metadata.
    - Set MCPLENSE_NO_BROWSER=1 to print the auth URL to stderr instead of
      launching a browser. Useful in headless environments together with
      'ssh -L' port-forwarding for the loopback redirect.
    - Set MCPLENSE_NO_INTERACTIVE_FLOW=1 to forbid the runtime browser fallback
      so a missing/expired token surfaces as an error. Combine with '--login'
      on a workstation to refresh the cache, then re-run headless.

  Microsoft 365 / Entra ID (interactive-browser):
    - Use 'auth.type: interactive-browser' (or '--auth interactive-browser') for
      Microsoft 365 / Entra-protected MCP servers. Tokens are acquired via MSAL
      using a public-client GUID you already have access to (e.g. the VS Code
      client 'aebc6443-996d-45c2-90f0-388ff96faa56') and persisted in the OS
      credential store (DPAPI on Windows, libsecret on Linux, Keychain on macOS).
    - Entra's loopback redirect exception requires 'http://localhost' (any port);
      '127.0.0.1' is rejected. MSAL picks a free localhost port automatically.
    - Setting 'cacheName: mcp-proxy' shares the on-disk MSAL cache with the
      mcp-proxy tool so sign-in flows are pooled across both.

  Config example (per-server):
    {
      "mcpServers": {
        "bearer-server": {
          "url": "https://api.example.com/mcp",
          "auth": { "type": "bearer", "token": "env:API_TOKEN" }
        },
        "oauth-server": {
          "url": "https://api.example.com/mcp",
          "auth": {
            "type": "oauth",
            "scopes": ["mcp.read", "mcp.write"],
            "clientId": "env:OAUTH_CLIENT_ID"
          }
        },
        "m365-server": {
          "url": "https://agent365.svc.cloud.microsoft/.../servers/mcp_MailTools",
          "auth": {
            "type": "interactive-browser",
            "clientId": "env:VSCODE_CLIENT_ID",
            "tenantId": "env:CORP_TENANT_ID",
            "scopes": ["${VSCODE_AUDIENCE}/.default"]
          }
        }
      }
    }

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
        "auth": { "type": "bearer", "token": "env:REMOTE_TOKEN" }
      }
    ]
  }

  Every string in the JSON config is environment-expanded using the same
  '${VAR}', '${VAR:-default}', and 'env:VAR' syntax described above.
  Use '$$' for a literal '$'.

Examples
  mcplense inspect --config mcp.json
  mcplense tui --config mcp.json
  mcplense tools --config mcp.json --server everything
  mcplense inspect --url https://localhost:3000/mcp --format json
  mcplense inspect --url https://api.example.com/mcp --auth bearer --auth-token env:API_TOKEN
  mcplense inspect --url https://api.example.com/mcp --auth oauth --scope mcp.read --login
  mcplense inspect --url https://api.example.com/mcp --auth oauth --scope mcp.read --logout
  mcplense inspect --config samples/agent365.json --server agent365-mailtools --login
  mcplense call echo --url https://localhost:3000/mcp --args '{"message":"hello"}'
  dotnet run -- inspect --format dumpify -- npx -y @modelcontextprotocol/server-everything
  dotnet run -- tools --format json -- npx -y @modelcontextprotocol/server-everything
  mcplense inspect -- npx -y @modelcontextprotocol/server-everything
""";
}
