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

Inspect MCP servers from a positional URL, a config file, or a stdio command.

Usage
  mcplense inspect <url> [common-options]
  mcplense inspect [target-options] [common-options]
  mcplense tui [target-options] [common-options]
  mcplense tools [target-options] [common-options]
  mcplense resources [target-options] [common-options]
  mcplense prompts [target-options] [common-options]
  mcplense call <tool-name> [<url>] [target-options] [common-options] [--args <json>]
  mcplense read <uri-or-template> [<url>] [target-options] [common-options] [--args <json>]
  mcplense prompt <prompt-name> [<url>] [target-options] [common-options] [--args <json>]
  mcplense login   {--all | --profile <name> | <url>} [--profiles <path>] [common-options]
  mcplense logout  {--all | --profile <name> | <url>} [--profiles <path>] [common-options]
  mcplense help
  mcplense version

Target Options
  Positional URL is the canonical way to point at an HTTP MCP server. Use --url for the
  long form, or --command (or '-- <command ...>') for stdio MCPs. --config loads stdio
  servers from a JSON file (HTTP servers are no longer supported in --config files).

  --config <path>              Load one or more stdio MCP servers from a JSON config file.
                               Repeat to merge multiple config files; duplicate server names
                               across files raise an error.
  --server <name>              Filter --config servers by name. Repeat as needed.

  --url <url>                  Connect to an HTTP MCP endpoint (alternative to positional URL).
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

Authentication (auth profiles)
  Auth profiles describe HOW to authenticate, decoupled from any specific URL. The same
  profile can service many MCP servers (every Agent365 MCP under a tenant, every GitHub
  MCP for one account, etc.).

  Profile files are auto-discovered from:
    $XDG_CONFIG_HOME/McpLense/McpLense.Profiles.json   (or %APPDATA%\McpLense\... on Windows
                                                         when XDG_CONFIG_HOME is unset, or
                                                         ~/.config/McpLense\... on Unix)
    $XDG_CONFIG_HOME/McpLense/profiles/*.json          (multiple per-profile files, merged)

  --profiles <path>            Load profile entries from a specific file (overrides defaults).
                               Repeat to merge multiple profile files; duplicate profile
                               names across files raise an error.
  --profile <name>             Force a specific loaded profile by name.
                               Supports environment expansion ('env:VAR', '${VAR}').
  --try-all                    Walk every loaded profile sequentially. Currently only valid
                               with --login.

  Profile auto-pick (when --profile is omitted):
    1. Probe the URL for RFC 9728 'WWW-Authenticate' metadata. If absent, connect plain.
    2. Otherwise filter loaded profiles by advertised scopes.
    3. Pick the unique profile that already has a cached account.
    4. If multiple cached candidates remain, error and ask for --profile.
    5. If exactly one candidate remains (cached or not), use it.

  Ad-hoc CLI auth (limited to Bearer):
    --auth bearer              Send a static Authorization: Bearer <token> header.
    --auth-token <value>       Bearer token paired with '--auth bearer'. Supports
                               environment expansion:
                                 - 'env:VAR'           (whole-string)
                                 - '${VAR}' / '${VAR:-default}'  (substring)
  --no-auth                    Suppress all authentication (HTTP and stdio).

  Top-level login / logout commands:
    mcplense login --all                Log in to every loaded profile (skip already-cached).
    mcplense login --profile <name>     Force interactive login for one profile.
    mcplense login <url>                Resolve URL via auto-pick, then log in to the matched profile.
    mcplense logout {--all | --profile <name> | <url>}
                                        Mirror semantics for sign-out.

  Profile file shape:
    {
      "authProfiles": [
        {
          "name": "agent365",
          "auth": {
            "type": "interactive-browser",
            "clientId": "env:VSCODE_CLIENT_ID",
            "tenantId": "env:CORP_TENANT_ID",
            "scopes": ["${VSCODE_AUDIENCE}/.default"]
          }
        },
        {
          "name": "github",
          "auth": { "type": "bearer", "token": "env:GITHUB_TOKEN" }
        },
        {
          "name": "self-hosted-mcp",
          "auth": {
            "type": "oauth",
            "scopes": ["mcp.read", "mcp.write"]
          }
        }
      ]
    }

  Stdio config file shape (for --config; auth fields rejected):
    {
      "mcpServers": {
        "everything": {
          "command": "npx",
          "args": ["-y", "@modelcontextprotocol/server-everything"]
        }
      }
    }

  Microsoft 365 / Entra ID (interactive-browser):
    - Use 'auth.type: interactive-browser' for Microsoft 365 / Entra-protected MCPs.
      Tokens are acquired via MSAL using a public-client GUID you already have access to
      (e.g. the VS Code client 'aebc6443-996d-45c2-90f0-388ff96faa56') and persisted in
      the OS credential store (DPAPI on Windows, libsecret on Linux, Keychain on macOS).
    - Each profile gets its own MSAL cache (named after the profile) by default. Set
      'cacheName: \"mcp-proxy\"' on the profile to share with the mcp-proxy tool.

Common Options
  --format <text|json|dumpify> Output format. Default: text.
  --timeout <seconds>          Per-server timeout. Default: 30.
  --progress [true|false]      Show live tool-call progress. Default: true for call.
  -h, --help                   Show help.

Environment-variable expansion
  Every string in profile files, --config files, and CLI auth flags is environment-expanded:
    env:VAR              whole-string form
    ${VAR}               substring form
    ${VAR:-default}      substring form with default
  Use '$$' for a literal '$'.

Examples
  mcplense inspect https://localhost:3000/mcp --format json
  mcplense inspect https://api.example.com/mcp --auth bearer --auth-token env:API_TOKEN
  mcplense inspect https://agent365.svc.cloud.microsoft/.../mcp_MailTools \
                   --profiles samples/agent365.json --profile agent365
  mcplense login --all
  mcplense login --profile agent365
  mcplense login https://agent365.svc.cloud.microsoft/.../mcp_MailTools
  mcplense logout --profile agent365
  mcplense inspect --config mcp.json
  mcplense tools   --config mcp.json --server everything
  mcplense call echo --url https://localhost:3000/mcp --args '{"message":"hello"}'
  mcplense inspect -- npx -y @modelcontextprotocol/server-everything
""";
}
