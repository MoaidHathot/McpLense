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
# Inspect a public remote MCP server (positional URL)
mcplense inspect https://mcp.context7.com/mcp

# List its tools as JSON
mcplense tools https://mcp.context7.com/mcp --format json

# Open the TUI against an mcp.json config (stdio servers)
mcplense tui --config mcp.json
```

## Commands

```text
mcplense inspect [<url>]
mcplense tui
mcplense tools [<url>]
mcplense resources [<url>]
mcplense prompts [<url>]
mcplense call <tool-name> [<url>]
mcplense read <uri-or-template> [<url>]
mcplense prompt <prompt-name> [<url>]
```

## Targets

You can point `mcplense` at an MCP server in three ways: a positional URL (or
`--url`), a `--config` file (stdio MCPs only), or a stdio command (`--command`
or `-- <cmd ...>`).

### Positional URL (HTTP MCPs)

```bash
mcplense inspect https://localhost:3000/mcp
mcplense inspect https://localhost:3000/mcp --transport streamable-http
mcplense inspect https://localhost:3000/sse --transport sse
mcplense inspect https://localhost:3000/mcp --header Authorization="Bearer token"
```

`--url <url>` works as the long form. HTTP MCP servers can no longer be defined
inside `--config` files; their auth lives in profile files (see
[Authentication](#authentication)) and the URL is passed positionally.

### Config file (stdio MCPs only)

```json
{
  "mcpServers": {
    "everything": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-everything"]
    }
  }
}
```

Or the array form:

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

`--transport` is ignored for stdio targets.

## Remote MCP servers

`mcplense` can talk to any publicly reachable MCP server over HTTPS:

```bash
# auto-detect transport
mcplense inspect https://mcp.context7.com/mcp --format json

# call resolve-library-id
mcplense call resolve-library-id https://mcp.context7.com/mcp \
  --args '{"libraryName":"spectre.console"}'
```

### Headers

Pass `--header NAME=VALUE` once per header. Quoting differs by shell:

PowerShell (Windows / pwsh):

```powershell
$env:CTX7_TOKEN = "your-token-here"
mcplense inspect https://mcp.context7.com/mcp `
  --header "Authorization=Bearer $env:CTX7_TOKEN"
```

bash / zsh:

```bash
export CTX7_TOKEN=your-token-here
mcplense inspect https://mcp.context7.com/mcp \
  --header "Authorization=Bearer $CTX7_TOKEN"
```

For richer authentication, use auth profiles (recommended) or the `--auth bearer`
ad-hoc shortcut. See [Authentication](#authentication) below.

## Authentication

`mcplense` uses **auth profiles**: named, reusable authentication recipes that
describe HOW to authenticate, decoupled from any specific URL. The same profile
services every MCP server it can authenticate to (every Agent365 MCP under your
tenant, every GitHub MCP for one account, etc.) without per-server duplication.

| Kind                  | How tokens are sourced                                                                                  |
| --------------------- | ------------------------------------------------------------------------------------------------------- |
| `bearer`              | Static token from the profile, or `--auth-token` (env-expandable).                                      |
| `oauth`               | MCP-spec OAuth 2.1 with discovery (RFC 9728/8414), PKCE (RFC 7636), and Dynamic Client Registration.    |
| `interactive-browser` | Microsoft Entra ID via MSAL/`Azure.Identity` with OS-protected token cache. For Microsoft 365/Agent365. |

Stdio (process) targets never carry authentication.

### Profile files

Profile files contain ONLY `authProfiles` &mdash; no URLs, no server names. The
URL is always passed positionally on the command line.

```json
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
    }
  ]
}
```

### Auto-discovery (XDG paths)

McpLense auto-loads profile files from your config home:

| Source                                                                | Loaded? |
| --------------------------------------------------------------------- | ------- |
| `$XDG_CONFIG_HOME/McpLense/McpLense.Profiles.json`                    | yes     |
| `$XDG_CONFIG_HOME/McpLense/profiles/*.json` (alphabetised)            | yes     |
| Windows fallback when `XDG_CONFIG_HOME` unset: `%APPDATA%\McpLense\…` | yes     |
| Unix fallback when `XDG_CONFIG_HOME` unset: `~/.config/McpLense/…`    | yes     |

Profiles from all loaded files are merged. Duplicate names across files raise
an error showing both source paths.

Use `--profiles <path>` to load a specific file (overrides auto-discovery is
NOT performed when `--profiles` is given). Use `--profile <name>` to force a
specific loaded profile.

### Profile auto-pick

When you run `mcplense inspect <url>` (no `--profile`), McpLense:

1. Probes the URL for an RFC 9728 `WWW-Authenticate` header. If absent, connects
   plain (the server doesn't appear to need auth).
2. Filters loaded profiles by advertised scopes (when the probe surfaced any).
3. Picks the unique profile that already has a cached account.
4. If multiple cached candidates remain &rarr; errors and asks for `--profile`.
5. If exactly one candidate remains (cached or not) &rarr; uses it. The runtime
   triggers interactive auth on first request.

### Ad-hoc Bearer (no profile required)

For one-off Bearer connections, the CLI shortcut still works:

```bash
mcplense inspect https://example.com/mcp --auth bearer --auth-token env:REMOTE_TOKEN
```

OAuth and `interactive-browser` are profile-only on the CLI. To use them
ad-hoc, drop a small profile file in your config home (or pass `--profiles
./my-profile.json`) and refer to it via `--profile <name>`.

### Environment-variable expansion

Every string value in profile files, `--config` files, and the auth-related CLI
flags accepts these forms:

| Form               | Meaning                                                                                          |
| ------------------ | ------------------------------------------------------------------------------------------------ |
| `env:VAR`          | Whole-string only. Errors when `VAR` is unset.                                                   |
| `${VAR}`           | Substring. Errors when `VAR` is unset (empty string is preserved).                               |
| `${VAR:-default}`  | Substring with default. Uses `default` when `VAR` is unset **or** empty (bash `:-` semantics).   |
| `$$`               | Literal `$`.                                                                                     |

Errors include the JSON path (e.g. `authProfiles[agent365].auth.clientId`) or
the CLI flag name.

### CLI flags (auth surface)

| Flag                   | Purpose                                                                |
| ---------------------- | ---------------------------------------------------------------------- |
| `--profiles <path>`    | Load profile entries from a specific file (overrides XDG defaults).    |
| `--profile <name>`     | Force a specific loaded profile by name. Env-expandable.               |
| `--try-all`            | Walk every loaded profile sequentially. Currently `--login`-only.      |
| `--auth bearer`        | Send a static `Authorization: Bearer <token>` header.                  |
| `--auth-token <value>` | Bearer token paired with `--auth bearer`. Env-expandable.              |
| `--no-auth`            | Suppress all authentication.                                           |
| `--login`              | Run the auth flow once for the resolved profile, cache, and exit.      |
| `--logout`             | Clear cached tokens for the resolved profile and exit.                 |

`--login` and `--logout` reuse the same target/profile resolution as the
underlying command. They will move to top-level `mcplense login` / `mcplense
logout` commands in a future release.

### Microsoft 365 / Entra ID (interactive browser)

For Microsoft 365 and Entra-protected MCP servers (Agent365, Graph-backed
tools, internal corporate APIs), use `auth.type: interactive-browser`. McpLense
delegates the sign-in to MSAL via `Azure.Identity.InteractiveBrowserCredential`,
which means:

- **No app registration required** if you piggy-back on a Microsoft first-party
  public client GUID. The VS Code client
  `aebc6443-996d-45c2-90f0-388ff96faa56` is pre-trusted for Microsoft services
  and is the recommended starting point.
- **OS-protected token cache**. Tokens are stored under
  `%LOCALAPPDATA%\.IdentityService\<cacheName>` on Windows (DPAPI), the
  freedesktop secret service on Linux (with a `chmod 600` plain-file fallback),
  or Keychain on macOS. `cacheName` defaults to the profile's `name`.
- **Cache-sharing with mcp-proxy**. Set `cacheName: "mcp-proxy"` on the
  profile to share the MSAL cache with the
  [mcp-proxy](https://github.com/anomalyco/mcp-proxy) tool.
- **Correct loopback redirect.** MSAL handles Entra's `http://localhost`-only
  loopback exception transparently.

`scopes` follows the Entra `<application-id-uri>/.default` convention: it asks
Entra to issue an access token carrying every statically-consented permission
for the target resource. `tenantId` is optional &mdash; omit it to default to
`common`, which accepts any work/school/personal account.

A worked example lives in [`samples/agent365.json`](samples/agent365.json):

```bash
$env:VSCODE_CLIENT_ID = 'aebc6443-996d-45c2-90f0-388ff96faa56'
$env:CORP_TENANT_ID   = '<your-tenant-guid-or-common>'
$env:VSCODE_AUDIENCE  = '<agent365-application-id-uri>'

mcplense inspect https://agent365.svc.cloud.microsoft/.../mcp_MailTools `
  --profiles samples/agent365.json --profile agent365 --login

# Subsequent commands re-use the cached token automatically:
mcplense tools https://agent365.svc.cloud.microsoft/.../mcp_MailTools `
  --profiles samples/agent365.json
```

### Azure AD via out-of-band tokens (legacy)

If you prefer to mint tokens with `az account get-access-token` (or any other
mechanism) and feed them in as static bearer values, the bearer path still
works:

```bash
$env:AAD_TOKEN = (az account get-access-token --resource api://my-mcp-app `
  --query accessToken -o tsv)

mcplense inspect https://my-mcp-app.example.com/mcp `
  --auth bearer --auth-token env:AAD_TOKEN
```

### OAuth (MCP spec) auth

For MCP servers that publish [Protected Resource Metadata
(RFC 9728)](https://www.rfc-editor.org/rfc/rfc9728), `mcplense` performs the
full OAuth 2.1 + PKCE + Dynamic Client Registration dance for you. Zero MSAL,
zero Azure SDK &mdash; only the BCL. Define an `oauth` profile:

```json
{
  "authProfiles": [
    {
      "name": "self-hosted-mcp",
      "auth": {
        "type": "oauth",
        "scopes": ["mcp.read", "mcp.write"]
      }
    }
  ]
}
```

For servers that don't publish PRM, you can wire endpoints in by hand:

```json
{
  "authProfiles": [
    {
      "name": "manual-oauth",
      "auth": {
        "type": "oauth",
        "scopes": ["mcp.read"],
        "issuer": "https://login.example.com/",
        "authorizationEndpoint": "https://login.example.com/oauth2/authorize",
        "tokenEndpoint": "https://login.example.com/oauth2/token",
        "clientId": "env:OAUTH_CLIENT_ID"
      }
    }
  ]
}
```

#### Authorization Server Metadata discovery

Once `mcplense` knows the issuer (from PRM or `auth.issuer`), it locates the
authorization and token endpoints by trying three well-known URLs in order:

1. **RFC 8414 strict path-insert** &mdash; `{issuer_origin}/.well-known/oauth-authorization-server{issuer_path}`.
2. **RFC 8414 path-append variant** &mdash; `{issuer}/.well-known/oauth-authorization-server`.
3. **OIDC Discovery 1.0** &mdash; `{issuer}/.well-known/openid-configuration`. Per RFC 8414 §5,
   OIDC documents are a superset of ASM for the fields `mcplense` consumes; this fallback covers
   OIDC-only authorization servers, notably **Microsoft Entra ID v2.0**.

A 404 (or any other non-2xx) on a given form falls through to the next form. A 2xx response with
malformed JSON or missing `authorization_endpoint`/`token_endpoint` stops the ladder and surfaces
the failure. For Microsoft Entra ID and other Microsoft-first-party services, prefer the
`interactive-browser` auth kind &mdash; it bypasses RFC 7591 (which Entra doesn't implement) and
handles the loopback exception correctly out of the box.

#### Token cache

OAuth tokens (and any DCR-issued client credentials) are cached per-resource so
subsequent runs reuse them without re-prompting:

| OS         | Location                                                          | Encryption                         |
| ---------- | ----------------------------------------------------------------- | ---------------------------------- |
| Windows    | `%LOCALAPPDATA%\McpLense\tokens\<name>.bin`                       | DPAPI (`CurrentUser`)              |
| Linux      | `${XDG_DATA_HOME:-~/.local/share}/mcplense/tokens/<name>.json`    | Plain JSON, `chmod 600`            |
| macOS      | `~/Library/Application Support/McpLense/tokens/<name>.json`       | Plain JSON, `chmod 600`            |

`interactive-browser` profiles use the MSAL cache (named after the profile by
default) instead, stored under `%LOCALAPPDATA%\.IdentityService\<cacheName>` on
Windows and equivalent OS-protected stores on Linux/macOS.

#### Headless / CI environments

Set `MCPLENSE_NO_BROWSER=1` to skip the browser launch and print the
authorization URL to stderr instead. Combine with `ssh -L` port forwarding to
complete the loopback redirect from a remote workstation.

Set `MCPLENSE_NO_INTERACTIVE_FLOW=1` to disable the runtime browser fallback
entirely. A missing or expired token then surfaces as a clear error instructing
you to run `--login` on a workstation. This is the recommended posture for CI
runners.

## Examples

Inspect all servers in a (stdio) config:

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

List tools as JSON for a remote HTTP MCP:

```bash
mcplense tools https://mcp.context7.com/mcp --format json
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

Resolve a prompt against a remote HTTP MCP:

```bash
mcplense prompt code_review https://localhost:3000/mcp --args '{"language":"csharp","code":"Console.WriteLine(1);"}'
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
