# McpLense

[![CI](https://github.com/MoaidHathot/McpLense/actions/workflows/ci.yml/badge.svg)](https://github.com/MoaidHathot/McpLense/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/McpLense.svg?logo=nuget)](https://www.nuget.org/packages/McpLense)
[![Downloads](https://img.shields.io/nuget/dt/McpLense.svg?logo=nuget)](https://www.nuget.org/packages/McpLense)
[![License: Unlicense](https://img.shields.io/badge/license-Unlicense-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](global.json)

`McpLense` is a `.NET tool` for debugging Model Context Protocol (MCP) servers.

It can:

- inspect one or many servers in a single run
- browse them in an interactive TUI
- list tools, resources, prompts, and resource templates
- call tools, read resources, and resolve prompts
- connect from a config file, an HTTP/SSE URL, or a stdio command
- emit output as `text`, `json`, or `dumpify`
- show live progress for tool calls when servers emit progress notifications

## Install

From NuGet:

```bash
dotnet tool install --global McpLense
```

From a local package while developing:

```bash
dotnet pack src/McpLense -c Release
dotnet tool install --global --add-source ./src/McpLense/bin/Release McpLense
```

## Quick start

```bash
# Inspect a public remote MCP server (context7) over streamable-http
mcplense inspect --url https://mcp.context7.com/mcp

# List its tools as JSON
mcplense tools --url https://mcp.context7.com/mcp --format json

# Open the TUI against an mcp.json config
mcplense tui --config mcp.json
```

## Commands

```text
mcplense inspect
mcplense tui
mcplense tools
mcplense resources
mcplense prompts
mcplense call <tool-name>
mcplense read <uri-or-template>
mcplense prompt <prompt-name>
```

## Targets

You can point `mcplense` at a server in three different ways: a config file, a URL, or a stdio command.

### Config file

Works with the common `mcpServers` shape:

```json
{
  "mcpServers": {
    "everything": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-everything"]
    },
    "remote": {
      "url": "https://example.com/mcp",
      "transport": "streamable-http",
      "headers": {
        "Authorization": "Bearer token"
      }
    }
  }
}
```

It also supports a custom `servers` array:

```json
{
  "servers": [
    {
      "name": "filesystem",
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-filesystem", "./src"],
      "cwd": ".",
      "env": {
        "NODE_ENV": "development"
      }
    }
  ]
}
```

### URL

```bash
mcplense inspect --url https://localhost:3000/mcp
mcplense inspect --url https://localhost:3000/mcp --transport streamable-http
mcplense inspect --url https://localhost:3000/sse --transport sse
mcplense inspect --url https://localhost:3000/mcp --header Authorization="Bearer token"
```

### Stdio command

```bash
mcplense inspect --command npx --command-arg -y --command-arg @modelcontextprotocol/server-everything
```

Or use `--` to pass the server command through directly:

```bash
mcplense inspect -- npx -y @modelcontextprotocol/server-everything
```

## Transports

`--transport` selects how `mcplense` talks to a URL-based MCP server.

| Flag                      | Use when                                                     | Example URL                          |
| ------------------------- | ------------------------------------------------------------ | ------------------------------------ |
| `auto` (default)          | You don't know; the SDK will negotiate.                      | `https://host/mcp`                   |
| `streamable-http`         | Server speaks the modern Streamable HTTP MCP transport.      | `https://host/mcp`                   |
| `sse`                     | Server only exposes the legacy Server-Sent Events transport. | `https://host/sse`                   |

`--transport` is ignored for stdio targets and config-file entries that already declare their transport.

## Remote MCP servers

`mcplense` can talk to any publicly reachable MCP server over HTTPS. For example, the [context7](https://context7.com) public MCP endpoint:

```bash
# auto-detect transport
mcplense inspect --url https://mcp.context7.com/mcp --format json

# call resolve-library-id
mcplense call resolve-library-id \
  --url https://mcp.context7.com/mcp \
  --args '{"libraryName":"spectre.console"}'
```

### Headers and authentication

Pass `--header NAME=VALUE` once per header. Quoting differs by shell:

PowerShell (Windows / pwsh):

```powershell
$env:CTX7_TOKEN = "your-token-here"
mcplense inspect --url https://mcp.context7.com/mcp `
  --header "Authorization=Bearer $env:CTX7_TOKEN"
```

bash / zsh:

```bash
export CTX7_TOKEN=your-token-here
mcplense inspect --url https://mcp.context7.com/mcp \
  --header "Authorization=Bearer $CTX7_TOKEN"
```

In a config file, headers go under the server entry:

```json
{
  "mcpServers": {
    "context7": {
      "url": "https://mcp.context7.com/mcp",
      "transport": "streamable-http",
      "headers": {
        "Authorization": "Bearer ${CTX7_TOKEN}"
      }
    }
  }
}
```

For richer authentication, use the dedicated `auth` block (config) or `--auth` flags
(CLI). See [Authentication](#authentication) below.

## Authentication

`mcplense` supports two authentication kinds for HTTP/SSE servers:

| Kind     | Status    | How tokens are sourced                                      |
| -------- | --------- | ----------------------------------------------------------- |
| `bearer` | Available | Static token from config or `--auth-token` (env-expandable) |
| `oauth`  | Available | MCP-spec OAuth 2.1 with discovery (RFC 9728/8414), PKCE (RFC 7636), and Dynamic Client Registration (RFC 7591) |

Stdio (process) targets do not accept authentication; an `auth` block on a stdio
server raises an error unless `--no-auth` is supplied.

### Environment-variable expansion

All string values in config files (and the value of `--auth-token`) accept these
forms:

| Form               | Meaning                                                                 |
| ------------------ | ----------------------------------------------------------------------- |
| `env:VAR`          | Whole-string only. Errors when `VAR` is unset.                          |
| `${VAR}`           | Substring. Errors when `VAR` is unset (empty string is preserved).      |
| `${VAR:-default}`  | Substring with default. Uses `default` when `VAR` is unset **or** empty (bash `:-` semantics). |
| `$$`               | Literal `$`.                                                            |

Errors include the JSON path (e.g. `servers.remote.auth.token`) or the CLI flag
name.

### Bearer auth via config

```json
{
  "mcpServers": {
    "remote": {
      "url": "https://example.com/mcp",
      "transport": "streamable-http",
      "auth": {
        "type": "bearer",
        "token": "${REMOTE_TOKEN}"
      }
    }
  }
}
```

Setting both an `auth` block and a literal `Authorization` header on the same
server is a hard error.

### Bearer auth via CLI

```bash
# Token straight from an env var
mcplense inspect --url https://example.com/mcp \
  --auth bearer --auth-token env:REMOTE_TOKEN

# Inline (not recommended for shared shells)
mcplense inspect --url https://example.com/mcp \
  --auth bearer --auth-token "eyJhbGciOi..."
```

### Precedence and overrides

- `--auth <type>` **replaces** the config `auth` block entirely; you must
  re-supply `--auth-token` (and any future kind-specific flags).
- Without `--auth`, individual flags such as `--auth-token` overlay the matching
  field in the config block. They error out if no `auth` block exists for that
  server.
- `--no-auth` suppresses authentication on every server in the run, both HTTP
  and stdio. Other auth flags are accepted but ignored.
- With `--config`, auth flags apply to **every** HTTP server in the file. Stdio
  servers are skipped (they would otherwise error).

### Azure AD / other token sources

Slice A intentionally does not bundle MSAL or `Azure.Identity`. To use Entra ID
tokens today, mint them out-of-band and feed them in:

```bash
$env:AAD_TOKEN = (az account get-access-token --resource api://my-mcp-app `
  --query accessToken -o tsv)

mcplense inspect --url https://my-mcp-app.example.com/mcp `
  --auth bearer --auth-token env:AAD_TOKEN
```

### OAuth (MCP spec) auth

For MCP servers that publish [Protected Resource Metadata
(RFC 9728)](https://www.rfc-editor.org/rfc/rfc9728), `mcplense` performs the
full OAuth 2.1 + PKCE + Dynamic Client Registration dance for you. Zero MSAL,
zero Azure SDK — only the BCL.

```json
{
  "mcpServers": {
    "remote": {
      "url": "https://api.example.com/mcp",
      "transport": "streamable-http",
      "auth": {
        "type": "oauth",
        "scopes": ["mcp.read", "mcp.write"]
      }
    }
  }
}
```

For servers that don't publish PRM, you can wire endpoints in by hand:

```json
{
  "mcpServers": {
    "manual": {
      "url": "https://api.example.com/mcp",
      "auth": {
        "type": "oauth",
        "scopes": ["mcp.read"],
        "issuer": "https://login.example.com/",
        "authorizationEndpoint": "https://login.example.com/oauth2/authorize",
        "tokenEndpoint": "https://login.example.com/oauth2/token",
        "clientId": "env:OAUTH_CLIENT_ID"
      }
    }
  }
}
```

#### Authorization Server Metadata discovery

Once `mcplense` knows the issuer (from PRM or `auth.issuer`), it locates the
authorization and token endpoints by trying three well-known URLs in order:

1. **RFC 8414 strict path-insert** &mdash; `{issuer_origin}/.well-known/oauth-authorization-server{issuer_path}`.
2. **RFC 8414 path-append variant** &mdash; `{issuer}/.well-known/oauth-authorization-server` (used by some
   identity providers that did append-style instead of insert).
3. **OIDC Discovery 1.0** &mdash; `{issuer}/.well-known/openid-configuration`. Per RFC 8414 §5,
   OIDC documents are a superset of ASM for the fields `mcplense` consumes; this fallback covers
   OIDC-only authorization servers, notably **Microsoft Entra ID v2.0**.

A 404 (or any other non-2xx) on a given form falls through to the next form. A 2xx response with
malformed JSON or missing `authorization_endpoint`/`token_endpoint` stops the ladder and surfaces
the failure &mdash; the server clearly meant to respond at that URL. If all three forms exhaust, the
error message lists every URL attempted with its status to ease diagnosis.

> **Worked Microsoft Entra ID example:** see [`samples/agent365.json`](samples/agent365.json) for a
> full config that connects to Microsoft Agent365 via Entra ID. Entra does not implement RFC 7591
> Dynamic Client Registration, so you must register a public client in your tenant's Entra portal
> manually and supply its application (client) ID via `env:AGENT365_CLIENT_ID`.

#### CLI flags

| Flag                   | Purpose                                                                |
| ---------------------- | ---------------------------------------------------------------------- |
| `--scope <s>`          | OAuth scope to request (repeatable). Env-expandable.                   |
| `--redirect-uri <uri>` | Override loopback redirect URI (defaults to a free port on `127.0.0.1`).|
| `--token-cache-name`   | Override token cache key. Defaults to a stable hash of the resource URI.|
| `--login`              | Run the OAuth flow once, cache the token, then exit.                   |
| `--logout`             | Delete cached OAuth tokens for the resolved server(s) and exit.        |

`--login` and `--logout` reuse the same target resolution as the actual command
they're attached to, so you can put any command in front:

```bash
# Warm the cache before running headless commands.
mcplense inspect --url https://api.example.com/mcp --auth oauth --scope mcp.read --login

# Sign out.
mcplense inspect --url https://api.example.com/mcp --auth oauth --scope mcp.read --logout
```

#### Token cache

Tokens (and any DCR-issued client credentials) are cached per-resource so
subsequent runs reuse them without re-prompting:

| OS         | Location                                                          | Encryption                         |
| ---------- | ----------------------------------------------------------------- | ---------------------------------- |
| Windows    | `%LOCALAPPDATA%\McpLense\tokens\<name>.bin`                       | DPAPI (`CurrentUser`)              |
| Linux      | `${XDG_DATA_HOME:-~/.local/share}/mcplense/tokens/<name>.json`    | Plain JSON, `chmod 600`            |
| macOS      | `~/Library/Application Support/McpLense/tokens/<name>.json`       | Plain JSON, `chmod 600`            |

#### Headless / CI environments

Set `MCPLENSE_NO_BROWSER=1` to skip the browser launch and print the
authorization URL to stderr instead. Combine with `ssh -L` port forwarding to
complete the loopback redirect from a remote workstation.

Set `MCPLENSE_NO_INTERACTIVE_FLOW=1` to disable the runtime browser fallback
entirely. A missing or expired token then surfaces as a clear error instructing
you to run `--login` on a workstation. This is the recommended posture for CI
runners.

## Examples

Inspect all servers in a config:

```bash
mcplense inspect --config mcp.json
```

Open the interactive TUI:

```bash
mcplense tui --config mcp.json
```

Inspect one server from a config:

```bash
mcplense inspect --config mcp.json --server everything
```

List tools as JSON:

```bash
mcplense tools --config mcp.json --server everything --format json
```

List tools as Dumpify text:

```bash
mcplense tools --config mcp.json --server everything --format dumpify
```

Call a tool:

```bash
mcplense call echo --config mcp.json --server everything --args '{"message":"hello"}'
```

Call a tool and show progress events:

```bash
mcplense call trigger-long-running-operation --args '{"duration":5,"steps":5}' -- npx -y @modelcontextprotocol/server-everything
```

Read a resource template:

```bash
mcplense read docs://articles/{id} --config mcp.json --server docs --args '{"id":"getting-started"}'
```

Resolve a prompt:

```bash
mcplense prompt code_review --url https://localhost:3000/mcp --args '{"language":"csharp","code":"Console.WriteLine(1);"}'
```

Use Dumpify output:

```bash
mcplense inspect --config mcp.json --format dumpify
```

## `dotnet run` examples

Run from the repo root using `--project src/McpLense`, or `cd` into `src/McpLense` first.

Inspect a stdio MCP server and keep text output:

```bash
dotnet run --project src/McpLense -- inspect -- npx -y @modelcontextprotocol/server-everything
```

Inspect the same server as JSON:

```bash
dotnet run --project src/McpLense -- inspect --format json -- npx -y @modelcontextprotocol/server-everything
```

Inspect the same server with Dumpify output:

```bash
dotnet run --project src/McpLense -- inspect --format dumpify -- npx -y @modelcontextprotocol/server-everything
```

View only tools:

```bash
dotnet run --project src/McpLense -- tools --format json -- npx -y @modelcontextprotocol/server-everything
```

Open the TUI directly:

```bash
dotnet run --project src/McpLense -- tui -- npx -y @modelcontextprotocol/server-everything
```

If you have a config file instead:

```bash
dotnet run --project src/McpLense -- inspect --config mcp.json --server everything --format json
```

## Notes

- `inspect`, `tools`, `resources`, and `prompts` can run against multiple config servers at once.
- `call`, `read`, and `prompt` require exactly one selected server.
- `call` enables live progress output by default; use `--progress false` to disable it.
- Exit code is non-zero if any requested server fails or if a tool call reports `isError: true`.

## Project layout

```text
McpLense/
├─ src/
│  ├─ McpLense.slnx                 # Solution (XML format)
│  └─ McpLense/                     # CLI / TUI / MCP integration
│     ├─ Cli/                       # Argument parsing
│     ├─ Tui/                       # Spectre.Console TUI
│     ├─ Mcp/                       # Executor + target resolver
│     ├─ Output/                    # text / json / dumpify renderers
│     └─ Models/                    # Reports, parsed commands, options
├─ tests/
│  ├─ McpLense.UnitTests/           # In-process unit tests (no I/O)
│  ├─ McpLense.IntegrationTests/    # Real stdio + in-process HTTP MCP server
│  ├─ McpLense.E2ETests/            # Subprocess CLI tests + public MCP smoke
│  ├─ McpLense.TestServer/          # Stdio MCP test server
│  ├─ McpLense.TestServer.Shared/   # Tools/resources/prompts shared by both
│  └─ McpLense.TestHttpServer/      # HTTP/SSE MCP test server
├─ Directory.Build.props
├─ Directory.Packages.props         # Central package management
├─ NuGet.config                     # Pinned to nuget.org
├─ global.json                      # Pinned .NET SDK
├─ coverlet.runsettings             # Coverage settings
└─ .github/workflows/ci.yml         # Build + test + coverage matrix
```

## Contributing

Build and test locally:

```bash
dotnet build src/McpLense.slnx -c Release
dotnet test  src/McpLense.slnx -c Release
```

The `McpLense.E2ETests` project includes optional smoke tests that hit a public remote MCP server (currently [context7](https://mcp.context7.com)). They are skipped by default. To enable them:

PowerShell:

```powershell
$env:MCPLENSE_PUBLIC_SMOKE = "1"
dotnet test src/McpLense.slnx -c Release --filter "FullyQualifiedName~PublicMcpSmokeTests"
```

bash:

```bash
MCPLENSE_PUBLIC_SMOKE=1 dotnet test src/McpLense.slnx -c Release \
  --filter "FullyQualifiedName~PublicMcpSmokeTests"
```

Coverage is collected via `coverlet.runsettings` and rendered as an HTML report on Linux CI runs (uploaded as the `coverage-report-html` artifact, with a Markdown summary in the GitHub Actions job summary).

## License

[Unlicense](LICENSE).
