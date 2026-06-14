using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dumpify;
using McpLense.Diagnostics;
using Spectre.Console;

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

            if (command.Command is AppCommand.Schema)
            {
                return await SchemaCommand.RunAsync(command);
            }

            if (command.Interactive && command.Command is AppCommand.Call or AppCommand.Read or AppCommand.Prompt)
            {
                command = await InteractivePrep.FillAsync(command, AnsiConsole.Console, JsonOptions, CancellationToken.None);
            }

            var result = await McpExecutor.ExecuteAsync(command, JsonOptions, CancellationToken.None);
            WriteOutput(command.Format, result.Payload);
            return result.HasErrors ? 1 : 0;
        }
        catch (UserInputException ex)
        {
            McpLenseLog.Write(ex.Message);
            McpLenseLog.Write("Run 'mcplense help' for usage.");
            return 1;
        }
        catch (McpLenseAuthException ex)
        {
            McpLenseLog.Write($"Authentication error: {ex.Message}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            McpLenseLog.Write("Operation timed out.");
            return 1;
        }
        catch (Exception ex)
        {
            McpLenseLog.Write($"{ex.GetType().Name}: {ex.Message}");
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
  mcplense inspect <url|@target> [common-options]
  mcplense inspect [target-options] [common-options]
  mcplense scan      [<url|@target>] [target-options] [common-options]
  mcplense auth-scan [<url|@target>] [target-options] [common-options]
  mcplense observe   [<url|@target>] [target-options] [common-options]
  mcplense fetch-resource <uri-or-template> [<url|@target>] [target-options] [common-options]
  mcplense diff <baseline-before> <baseline-after>
  mcplense schema [config] [--output <path>]
  mcplense tui [target-options] [common-options]
  mcplense tools [target-options] [common-options]
  mcplense resources [target-options] [common-options]
  mcplense prompts [target-options] [common-options]
  mcplense call <tool-name> [<url|@target>] [target-options] [common-options] [--args <json> | --interactive]
  mcplense read <uri-or-template> [<url|@target>] [target-options] [common-options] [--args <json> | --interactive]
  mcplense prompt <prompt-name> [<url|@target>] [target-options] [common-options] [--args <json> | --interactive]
  mcplense login   {--all | --profile <name> | <url>} [--profiles <path>] [common-options]
  mcplense logout  {--all | --profile <name> | <url>} [--profiles <path>] [common-options]
  mcplense help
  mcplense version

Target Options
  Positional argument is one of:
    <url>          Absolute http(s) URL.
    @<target>      Named target reference. Looked up against `targets[].name` in your
                   McpLense.Config.json file. URL, headers, profile and transport come
                   from the config entry.

  --url <url>                  Connect to an HTTP MCP endpoint (alternative to positional URL).
  --transport <auto|streamable-http|sse>
                               HTTP transport mode. Default: auto.
  --header <name=value>        HTTP header. Repeat as needed. Most-specific layer of the
                               overlay (overrides config-level headers per key).

  --config <path>              Load one or more stdio MCP servers from a JSON config file.
                               Repeat to merge multiple config files; duplicate server names
                               across files raise an error.
  --server <name>              Filter --config servers by name. Repeat as needed.

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
    4. Tiebreaker: when multiple candidates remain, prefer the higher-priority kind.
       Default ranks (high -> low): azure-cli > interactive-browser > oauth > bearer.
       Override per-profile with the JSON 'priority' field (higher = preferred).
    5. If still tied at the same effective priority, error and ask for --profile.

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
          "name": "agent365-cli",
          "auth": {
            "type": "azure-cli",
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

  Microsoft 365 / Entra ID (azure-cli):
    - Use 'auth.type: azure-cli' to delegate token acquisition to the Azure CLI. McpLense
      shells out to 'az account get-access-token --resource <scope>' using the user's
      existing 'az login' session. No browser pop, no MSAL cache, no clientId needed.
    - Requires the Azure CLI installed and on PATH, and a prior 'az login'. Ideal for
      headless / SSH / CI environments where 'az login --use-device-code' or a service
      principal session is already configured.
    - Switch tenant with 'az account set --subscription <id>' or via the profile's
      tenantId field (optional; defaults to the CLI's current tenant).

Common Options
  --format <text|json|jsonl|dumpify>
                               Output format. Default: text. 'jsonl' (alias 'ndjson') emits
                               one JSON document per line: a header line, one server line
                               per scanned server, and a trailer. Use for fleet-scale
                               stream-reading.
  --timeout <seconds>          Per-server timeout. Default: 30.
  --progress [true|false]      Show live tool-call progress. Default: true for call.
  -i, --interactive            Prompt for arguments interactively (call / read / prompt) instead
                               of passing --args. Reads the tool's input schema / the prompt's
                               arguments / the URI-template variables and asks for each value,
                               pre-filling any declared default (press Enter to accept it).
  -h, --help                   Show help.

Auth scanning (`mcplense auth-scan`)
  `auth-scan` is the minimal, read-only discovery command: classify how each target
  authenticates (anonymous, RFC 9728 OAuth, Bearer-without-metadata, non-Bearer challenge,
  unknown) and - when one or more profiles are loaded - report which profile(s) actually
  open an MCP session against the server. Never writes anywhere; useful as a fast
  per-server check.

  Default behaviour: every loaded profile is tried in source order. Override with:
    --profile <name>           Try only this single profile.
    --classify-only            Skip profile attempts entirely; emit only the classification
                               block (status, RFC 9728 metadata, scopes_supported, ...).
                               Scan-specific synonym of --no-auth; both produce the same
                               auth-scan output.
    --no-auth                  Same effect as --classify-only for auth-scan, but also strips
                               inline auth on every other command (inspect / tools / ...).

  Works on:
    mcplense auth-scan https://server.example/mcp
    mcplense auth-scan https://server.example/mcp --classify-only
    mcplense auth-scan https://server.example/mcp --profiles ./agent365.json --profile agent365

Full audit (`mcplense scan`)
  `scan` runs the full IScanCheck pipeline against the target(s). Every check publishes its
  output under `checks.<id>` in the report. Designed for cataloguing a fleet of MCP servers
  and feeding the output to downstream policy / risk tooling. Fact-only - the scanner
  extracts data; consumers classify.

  Scope:
    --classify-only            Skip profile attempts AND skip enumeration that depends on
                               them. The auth block is still emitted.
    --check-authorization-servers
                               Opt in to fetching every advertised authorization-server
                               metadata document (RFC 8414 / OIDC discovery).
    --enable <check-id>        Force-enable a check. Repeatable. Overrides config + default.
    --disable <check-id>       Force-disable a check. Repeatable. Overrides config + default.
    --baseline <path>          After running the scan, write the resulting JSON report to
                               <path>. If <path> is a directory, the file goes under
                               <path>/<host>/<UTC-timestamp>.json. Default base directory
                               (when config doesn't override) is the current directory.
    --diff <baseline-path>     After running the scan, diff against the JSON baseline at
                               <path> and emit the structural diff instead of the full
                               scan report.
    --scan-plugin <path>       Load IScanCheck implementations from an external .NET
                               assembly (or every *.dll in a directory). Repeatable.
                               Plugins compile against the McpLense package and are
                               loaded into an isolated AssemblyLoadContext that shares
                               only the host's McpLense assembly. A plugin check whose
                               Id matches a built-in replaces the built-in.
    --targets-from <path>      Read scan targets from a plain-text file (one URL or
                               @name per line; blank / '#'-prefixed lines ignored).
                               Repeatable. Lets McpLense own the parallelism for
                               fleet-scale scans instead of forking one process per
                               server. Combine with --parallel-servers.
    --http-only                Drop stdio targets after resolution. Useful when --config
                               mixes stdio + HTTP and you only want the HTTP fleet.
    --default-scope <value>    Fallback OAuth scope used by profiles when (a) the profile
                               didn't pin a non-default scope and (b) RFC 9728 PRM didn't
                               advertise one. Designed for Entra / AAD-backed MCPs that
                               don't speak PRM. Example: 'api://my-aad-app/.default'.

  Built-in checks (per `IScanCheck.Id` - configurable via scan.checks.<id> in the config file):
    auth                         RFC 9728 classification + profile attempts.
    transport                    Unauthenticated probe: status, headers, TLS leaf cert.
    tlsChain                     TLS intermediate chain + validation outcome.
    authenticatedHeaders         Same headers shape but from an authenticated GET.
    corsPreflight                OPTIONS preflight against the MCP URL with synthetic Origin.
    authorizationServers         Per-AS RFC 8414 fields (opt-in via flag or config).
    dcrEndpoint                  RFC 7591 DCR endpoint surface (opt-in via config).
    serverInfo                   Implementation name / version / title / description / icons.
    protocol                     Negotiated version + full capabilities block + verbatim
                                 server instructions + sessionId.
    tools                        Per-tool: name, description, schemas, annotations, missing
                                 annotations, schemaFingerprint (parameter counts,
                                 type histogram, format list, AdditionalProperties, etc.).
    prompts                      Per-prompt: name, description, arguments, icons, _meta.
    resources                    Per-resource: uri + scheme, name, mimeType, description,
                                 size, annotations, icons. Plus a top-level scheme histogram.
    stdio                        Resolved command / args / cwd / env (stdio targets only).
    behavior.callNonExistentTool Calls a tool the server doesn't expose; captures response.
    behavior.serverInitiated     Holds session open and observes inbound server messages.
                                 Opt-in via config. Configurable duration + advertised
                                 client capabilities.
    metrics                      Per text field: char/line/url/markdown/control-char counts.
    hashing                      Per-tool / per-prompt / per-resource contentHash + a
                                 top-level serverFingerprint. Powers the diff engine.

  Works on:
    mcplense scan https://server.example/mcp
    mcplense scan https://server.example/mcp --check-authorization-servers
    mcplense scan https://server.example/mcp --enable behavior.serverInitiated
    mcplense scan https://server.example/mcp --baseline ./baselines/
    mcplense scan https://server.example/mcp --diff ./baselines/server.example/old.json
    mcplense scan https://server.example/mcp --profiles ./agent365.json --classify-only

Observation (`mcplense observe`)
  Single-check shortcut: runs ONLY the auth + behavior.serverInitiated checks against the
  target. Use --timeout to control the observation duration (the configured
  observationDurationSeconds is honoured first; --timeout caps it).

Resource fetch (`mcplense fetch-resource`)
  Drill-down: opens a session and reads the named resource verbatim. Same as
  `mcplense read <uri>`, kept as a named command for clarity in tooling pipelines.

Diff (`mcplense diff`)
  Pure file-to-file structural diff: takes two baseline JSON files written by previous
  scans and emits the differences. No network.

Interactive explorer (`mcplense tui`)
  Keyboard-driven TUI to browse a server's tools / resources / resource templates / prompts
  (with per-section search + persistent bookmarks) AND invoke them. Drill into a tool and pick
  "Call tool": McpLense reads its input schema and prompts for each argument (required marked
  `*`, declared defaults pre-filled - press Enter to accept, or type to override), echoes the
  equivalent `mcplense call ... --args` line, then runs it and shows the result. The same
  applies to reading resources / resource templates (URI-template variables are elicited) and
  getting prompts. For one-shot use without the TUI, add `--interactive` to call / read / prompt.

Configuration file (`McpLense.Config.json`)
  Single JSON file auto-discovered from `$XDG_CONFIG_HOME/McpLense/McpLense.Config.json` or
  `%APPDATA%/McpLense/McpLense.Config.json`. May also live in the legacy filename
  `McpLense.Profiles.json` - both are read. Top-level keys:
    authProfiles    (array) - auth profile definitions (unchanged from earlier releases).
    targets         (array) - per-URL header / profile / transport / timeout binding.
    targetPatterns  (array) - URL-glob overlays that apply across many MCPs.
    scan            (object) - scan-pipeline configuration.

  Per-target `targets[]` shape:
    {
      "targets": [
        {
          "name":   "ec-foo",
          "url":    "https://example.ec.com/foo/mcp",
          "headers": {
            "x-mcp-ec-organization": "myorg",
            "x-mcp-ec-project":      "myproj",
            "x-mcp-ec-repository":   "${MCPLENSE_EC_REPO:-default}"
          },
          "scope":          "All",
          "profile":        "agent365",
          "transport":      "streamable-http",
          "timeoutSeconds": 90,
          "disabledChecks": ["corsPreflight"]
        }
      ]
    }
  Reference the target on the CLI with `@ec-foo`. Headers also apply automatically
  when you scan the matching URL.

  Pattern overlay `targetPatterns[]` shape (URL-glob, least-specific layer):
    {
      "targetPatterns": [
        {
          "match":   "https://*.ec.com/**",
          "headers": { "x-mcp-ec-organization": "default-org" },
          "scope":   "All"
        }
      ]
    }

  Glob syntax: `*` = one host label OR one path segment, `**` = any sequence
  including `/`, `?` = one char. Host part is case-insensitive; path part is
  case-sensitive. The scheme separator `://` is required.

  Scope (`All` is the default):
    All      Headers ride along with the MCP session AND the same-origin probes
             (transport probe, CORS preflight, authenticated-headers, DCR endpoint).
    Session  Headers only ride with the MCP session; probes go out bare.
    Cross-origin fetches (e.g. authorization-server metadata on a different host)
    NEVER receive MCP-server headers, regardless of scope.

  Precedence (low -> high): pattern -> target -> CLI flag. Last-write-wins per
  header key; per-other-field "later non-null wins". CLI `--disable` is UNIONED
  with any per-target / per-pattern `disabledChecks`.

  `scan` block shape:
    {
      "scan": {
        "checks": {
          "auth":                   { "enabled": true },
          "behavior.serverInitiated": {
            "enabled": false,
            "observationDurationSeconds": 2,
            "advertiseCapabilities": ["sampling", "elicitation", "roots", "listChanged"]
          },
          "metrics": {
            "enabled": true,
            "urlExtractionFields": ["serverInstructions", "toolDescription", "promptDescription"]
          }
        },
        "output": {
          "baselineDir": "./baselines"
        }
      }
    }
  Config-file enable/disable wins over the check's IsEnabledByDefault. CLI --enable /
  --disable flags win over both.
    behavior                   callNonExistentTool: verbatim JSON-RPC response (code /
                               message / data) when we call a tool name the server doesn't
                               expose. Useful for spotting information leakage.
    stdio                      (only for stdio targets) resolved command line, args, cwd,
                               environment.

  Works on:
    mcplense scan https://server.example/mcp
    mcplense scan @ec-foo
    mcplense scan https://server.example/mcp --check-authorization-servers
    mcplense scan https://server.example/mcp --profiles ./agent365.json --classify-only
    mcplense scan --config mcp.json

Environment-variable expansion
  Every string in profile files, --config files, and CLI auth flags is environment-expanded:
    env:VAR              whole-string form
    ${VAR}               substring form
    ${VAR:-default}      substring form with default
  Use '$$' for a literal '$'.

  Auto-discovery kill-switch:
    MCPLENSE_NO_PROFILE_AUTO_DISCOVERY=1
      Disable XDG/APPDATA profile auto-discovery. --profiles <path> flags still
      work normally. CI runners and integration test suites should set this so a
      user-side profile can never trigger surprise interactive flows.

Examples
  mcplense inspect https://localhost:3000/mcp --format json
  mcplense inspect https://api.example.com/mcp --auth bearer --auth-token env:API_TOKEN
  mcplense inspect https://agent365.svc.cloud.microsoft/.../mcp_MailTools \
                   --profiles samples/agent365.json --profile agent365
  mcplense scan      https://api.example.com/mcp
  mcplense scan      https://api.example.com/mcp --check-authorization-servers
  mcplense auth-scan https://api.example.com/mcp
  mcplense auth-scan https://api.example.com/mcp --classify-only
  mcplense auth-scan https://agent365.svc.cloud.microsoft/.../mcp_MailTools --no-auth
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
