# McpLense

[![CI](https://github.com/MoaidHathot/McpLense/actions/workflows/ci.yml/badge.svg)](https://github.com/MoaidHathot/McpLense/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/McpLense.svg?logo=nuget)](https://www.nuget.org/packages/McpLense)
[![Downloads](https://img.shields.io/nuget/dt/McpLense.svg?logo=nuget)](https://www.nuget.org/packages/McpLense)
[![License: Unlicense](https://img.shields.io/badge/license-Unlicense-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](Directory.Build.props)

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

`McpLense` is split into two NuGet packages:

| Package | Audience | How to install |
|---|---|---|
| `McpLense.Cli` | End users running the CLI tool | `dotnet tool install --global McpLense.Cli` |
| `McpLense` | Library consumers / extension authors | Reference via `<PackageReference Include="McpLense" />` |

> **Upgrading from a pre-0.4 install?** Earlier preview releases shipped a `McpLense`
> global tool that also installed an `mcplense.exe` shim. Both packages register the
> same `mcplense` command on PATH, so only one wins. If `mcplense scan` says
> "Unknown command 'scan'", you almost certainly have the old library-only package
> claiming the command. Run `dotnet tool uninstall -g McpLense` first, then
> `dotnet tool install -g McpLense.Cli`.

From a local package while developing:

```bash
# Build + pack both packages (library + CLI) to ./artifacts.
./pack.ps1

# Install the CLI from the local feed:
dotnet tool install --global --add-source ./artifacts McpLense.Cli
```

## Scan + extensibility

`mcplense scan <url>` runs the full `IScanCheck` pipeline: auth classification, TLS,
headers, OAuth metadata, MCP capability surface, tool/prompt/resource enumeration, content
hashing, behaviour probes. Every check publishes its data under `checks.<id>` in the JSON
report; the wire shape is stable.

See `docs/scan-checks.md` for the complete per-check reference and
`docs/security-classification-recipes.md` for jq-based recipes that classify scan output
for downstream policy / risk tooling.

## AI Agent Skill

`McpLense` ships an [Agent Skill](https://agentskills.io/) under `skills/mcplense/`
so any skills-aware AI agent (Claude Code, Claude, Cursor, OpenCode, Goose, Gemini CLI,
OpenHands, GitHub Copilot, Roo Code, Kiro, and others) can discover and use the CLI
without reading the full README.

```bash
# Claude Code (personal install)
mkdir -p ~/.claude/skills
cp -R skills/mcplense ~/.claude/skills/

# Or project-local
mkdir -p .claude/skills
cp -R skills/mcplense .claude/skills/
```

See [`skills/README.md`](skills/README.md) for install paths for every supported client.
The skill teaches agents the command surface (`inspect` / `tools` / `scan` / `diff` /
`call` / `read` / `prompt` / `fetch-resource` / `auth-scan` / `observe`), the unified
`McpLense.Config.json` schema (per-target headers, glob patterns, scope semantics),
profile-based authentication (Bearer, OAuth, Entra interactive-browser, Azure CLI), and
common jq recipes for downstream classification.


### Custom checks via the library

Reference the `McpLense` package and register your own `IScanCheck` either through the
fluent builder or the DI extension:

```csharp
using McpLense.Scanning;

// Fluent
var report = await new ScanPipelineBuilder()
    .AddDefaultChecks()
    .AddCheck<MyCustomCheck>()
    .Build()
    .RunAsync(targets, TimeSpan.FromSeconds(30), CancellationToken.None);

// DI
services.AddMcpLense();
services.AddScanCheck<MyCustomCheck>();
```

A custom check is one type:

```csharp
public sealed class MyCustomCheck : IScanCheck
{
    public string Id => "my.custom-check";
    public IReadOnlyList<string> DependsOn => new[] { "auth" };
    public bool IsEnabledByDefault => false;

    public async Task<CheckOutcome> RunAsync(ScanContext context, CancellationToken ct)
    {
        // Inspect context.Server / context.Profiles / context.AuthOverrides.
        // Optionally open the shared MCP session via context.GetSessionAsync(ct).
        return new CheckOutcome(Ran: true, Data: System.Text.Json.Nodes.JsonNode.Parse("{\"hello\":\"world\"}"), Error: null);
    }
}
```

The pipeline catches every exception thrown by a check, captures it onto the per-check
report entry, and continues with sibling checks. Linked-token timeouts surface as
`"Timed out."` per check.

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
mcplense explain [<url>]                         # plain-language "what is this MCP" summary
mcplense doctor [<url>]                          # staged connectivity triage ("why won't it connect?")
mcplense scan [<url>]                            # fact-only audit pipeline
mcplense analyze [<url>] [--fail-on <severity>]  # scan + severity-rated findings (CI gate)
mcplense login   {--all | --profile <name> | <url>}
mcplense logout  {--all | --profile <name> | <url>}
```

### Findings (`analyze`)

`scan` is deliberately **fact-only** — it records observations and never labels them. `analyze`
runs the same scan, then applies a built-in rule pack (prompt-injection signals in tool
descriptions, anonymous servers exposing destructive tools, open-shape input schemas, weak CORS,
TLS posture, error info-leak, ...) and emits **findings** with severities. Facts and findings stay
in separate documents.

```bash
mcplense analyze https://server.example/mcp                 # human-readable findings
mcplense analyze https://server.example/mcp --fail-on high  # CI gate: non-zero exit at/above high
mcplense scan https://server.example/mcp --findings --format json
```

Rules and severities are configured in `McpLense.Config.json` under the top-level `analysis` block
(`analysis.rules.<id>.enabled` / `.severity`, `analysis.failOn`) - a fleet policy lives in config
rather than CLI flags. See [docs/analysis-rules.md](docs/analysis-rules.md).

### Learning aids

```bash
mcplense explain https://server.example/mcp                 # narrative: identity, auth, what it exposes, findings
mcplense explain https://server.example/mcp --format markdown > server.md   # shareable write-up
mcplense call <tool> https://server.example/mcp --example   # generate a ready-to-edit --args template (no invocation)
```

`explain` runs the scan and narrates it in plain language. `call --example` reads the tool's input
schema and prints a filled-in `--args` example plus the equivalent command, so a first call is
copy-paste-edit. `--format markdown` (alias `md`) renders `explain` / `inspect` / findings as a
shareable Markdown document.

### Debugging aids

```bash
mcplense doctor https://server.example/mcp          # DNS -> TCP -> TLS -> MCP initialize -> auth, with hints
mcplense inspect https://server.example/mcp --trace # log every JSON-RPC request/response to stderr
mcplense inspect https://server.example/mcp --watch 5   # re-run every 5s, flag when the output changes
```

`doctor` walks the connection stage by stage and tells you exactly where it broke (and how to fix
it) - built for "why won't my server connect?". `--trace` logs the HTTP wire traffic (method, URL,
JSON-RPC body, status, timing) to stderr while the command runs. `--watch <seconds>` re-runs any
read-only command on an interval and flags when the rendered output changed - a tight dev loop.

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

Pass `--config <path>` once per file (repeatable; merges across files; duplicate
server names raise an error):

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

### Per-target headers (config file)

Many enterprise MCPs gate access on custom headers (organization id, project id,
repository id, ...). To avoid typing those headers on every CLI call you can declare
them in `McpLense.Config.json` (the same file that holds `authProfiles`):

```jsonc
{
  "targetPatterns": [
    {
      "match":   "https://*.ec.com/**",
      "headers": { "x-mcp-ec-organization": "default-org" }
    }
  ],
  "targets": [
    {
      "name": "ec-foo",
      "url":  "https://example.ec.com/foo/mcp",
      "headers": {
        "x-mcp-ec-organization": "myorg",
        "x-mcp-ec-project":      "myproj",
        "x-mcp-ec-repository":   "${MCPLENSE_EC_REPO:-default}"
      },
      "scope": "All",
      "profile": "agent365",
      "disabledChecks": ["corsPreflight"]
    }
  ]
}
```

A worked example sits at [`samples/targets.json`](samples/targets.json).

Reference a named target on the CLI with `@<name>`:

```bash
mcplense scan @ec-foo                         # auto-resolves URL + headers from config
mcplense inspect @ec-foo --format json
mcplense tools @ec-foo                        # works for every command, not just scan
```

Headers also apply automatically when you scan by URL (auto-resolution by exact URL).
The resolver merges three layers in order, last-write-wins per header key:

1. Any matching `targetPatterns[]` entries (least specific).
2. The matching `targets[]` entry — by `@<name>` OR by exact URL.
3. CLI `--header` flags (most specific).

The overlay applies uniformly across every command that opens an MCP connection
(`scan`, `inspect`, `tools`, `resources`, `prompts`, `call`, `read`, `prompt`,
`fetch-resource`, `auth-scan`, `observe`) — not just `scan`.

Under non-quiet mode the run prints one stderr line summarising what matched:

```
matched: patterns=1 target=ec-foo -> 3 headers, scope=all
```

Add `--verbose` to also see which exact header NAMES are flowing (values are never
echoed - they may carry secrets) and which patterns fired:

```
matched: patterns=1 target=- -> 3 headers, scope=all
matched headers for https://mcp.bluebird-ai.net/: x-mcp-ec-organization, x-mcp-ec-project, x-mcp-ec-repository
matched pattern(s): https://**bluebird**/**
```

By default (`scope: "All"`) the merged headers ride along with **both** the MCP session
**and** every same-origin probe the scanner runs (transport probe, CORS preflight,
authenticated-headers re-probe, DCR endpoint probe). This fixes the previous "gap"
where servers that gated everything behind a custom header returned opaque errors to
the scanner. Set `scope: "Session"` per target to keep the probes bare while the
session still authenticates normally. Cross-origin probes (e.g. authorization-server
metadata on a different host) never receive MCP-server headers, regardless of scope.

See [`docs/scan-checks.md`](docs/scan-checks.md#per-target-configuration) for the full
schema (glob syntax, every field, every precedence rule).

## Authentication

`mcplense` uses **auth profiles**: named, reusable authentication recipes that
describe HOW to authenticate, decoupled from any specific URL. The same profile
services every MCP server it can authenticate to (every Agent365 MCP under your
tenant, every GitHub MCP for one account, etc.) without per-server duplication.

| Kind                  | How tokens are sourced                                                                                  |
| --------------------- | ------------------------------------------------------------------------------------------------------- |
| `bearer`              | Static token from the profile, or `--auth-token` (env-expandable).                                      |
| `oauth`               | MCP-spec OAuth 2.1 with discovery (RFC 9728/8414), PKCE (RFC 7636), and Dynamic Client Registration.    |
| `interactive-browser` | Microsoft Entra ID via MSAL/`Azure.Identity` with OS-protected token cache. First-time login pops a browser. |
| `azure-cli`           | Microsoft Entra ID via the Azure CLI. Delegates to `az account get-access-token --resource <scope>` using the user's existing `az login` session. No browser pop. Ideal for headless / CI. |

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

#### Disabling auto-discovery (`MCPLENSE_NO_PROFILE_AUTO_DISCOVERY`)

Set the env var `MCPLENSE_NO_PROFILE_AUTO_DISCOVERY=1` (or `true`/`yes`/`on`)
to bypass auto-discovery entirely. `--profiles <path>` flags still work; only
the XDG/APPDATA/HOME fallback search is suppressed.

This matters in two scenarios:

- **CI runners**: no user, no browser. Auto-discovery picking up a profile
  whose auth kind is `interactive-browser` would hang the build waiting for
  someone to sign in.
- **Test suites**: the McpLense integration and E2E tests in this repo set
  this var via a `ModuleInitializer` so the developer's user-side profile
  (typically `$XDG_CONFIG_HOME/McpLense/McpLense.Profiles.json`) can never
  leak into a test run and trigger an MSAL browser pop.

### Profile auto-pick

When you run `mcplense inspect <url>` (no `--profile`), McpLense connects
**anonymously first** and only authenticates if the server refuses that attempt
(HTTP 401/403). A server that allows anonymous access is never handed credentials
it didn't ask for; the output records how it actually connected:

```text
auth: anonymous (no credentials sent)
auth: authenticated (profile=agent365-cli, kind=AzureCli)
```

When the anonymous attempt is refused, McpLense falls back to the auto-picked
profile, chosen as follows:

1. Filters loaded profiles by advertised scopes (when the server's RFC 9728
   metadata surfaced any).
2. Picks the unique profile that already has a cached account.
3. **Tiebreaker** — when multiple candidates remain (or none has cached creds
   but multiple plausibly fit), prefer the higher-priority kind:

   | Default rank | Auth kind            | Rationale                                                  |
   | ------------ | -------------------- | ---------------------------------------------------------- |
   | **400 (highest)** | `azure-cli`     | Silent; inherits `az login`; never pops a browser.         |
   | 300          | `interactive-browser`| MSAL; silent when cached; browser pop on first run.        |
   | 200          | `oauth`              | Generic OAuth; full discovery + possible DCR (slow).       |
   | 100 (lowest) | `bearer`             | Static token; rarely conflicts in practice.                |

   Override per-profile with an explicit `priority` JSON field (higher wins):

   ```json
   {
     "name": "agent365-prefer-browser",
     "priority": 500,
     "auth": { "type": "interactive-browser", ... }
   }
   ```

4. If profiles tie at the same effective priority, McpLense still tries
   anonymously; only when the server then demands auth does it ask you to pick
   one with `--profile <name>`. Passing `--profile <name>` explicitly skips the
   anonymous attempt and authenticates directly (you asked to).

### Scope substitution from PRM (Agent365 et al.)

Some servers (notably Microsoft Agent365) advertise per-resource scopes in
their RFC 9728 Protected Resource Metadata document. If your profile asks for
a generic `<audience>/.default`-style scope, McpLense automatically replaces
it with the server-advertised `.default` scope at request time:

```text
# Your profile says:
"scopes": ["${VSCODE_AUDIENCE}/.default"]
#   -> "ea9ffc3e-8a23-4a7d-836d-234d7c7565c1/.default" after env expansion

# Server's PRM advertises:
"scopes_supported": [
  "https://agent365.svc.cloud.microsoft/agents/tenants/<tenant>/servers/mcp_MailTools/.default",
  ...
]

# McpLense automatically requests the second form, which is what the server
# actually accepts. One profile works across every Agent365 MCP without per-
# server boilerplate.
```

**The rule**: substitution happens only when **every** scope in your profile
ends with `/.default`. Profiles with explicit permission names
(`mcp.read`, `repo`, `User.Read`, etc.) are left untouched — those users
asked for something specific.

When the server doesn't speak RFC 9728 (no PRM document) or the probe is
inconclusive (network failure, etc.), McpLense uses the profile's original
scopes as-is.

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
| `--profiles <path>`    | Load profile entries from a specific file (repeatable; merges across files). |
| `--profile <name>`     | Force a specific loaded profile by name. Env-expandable.               |
| `--try-all`            | Walk every loaded profile sequentially. Currently `mcplense login`-only. |
| `--auth bearer`        | Send a static `Authorization: Bearer <token>` header.                  |
| `--auth-token <value>` | Bearer token paired with `--auth bearer`. Env-expandable.              |
| `--no-auth`            | Suppress all authentication.                                           |

### Logging in and out

`mcplense login` and `mcplense logout` are top-level commands that operate on
profiles directly. They support three mutually-exclusive selectors:

| Invocation                       | What it does                                                              |
| -------------------------------- | ------------------------------------------------------------------------- |
| `mcplense login --all`           | Walk every loaded profile; skip already-cached; prompt for the rest.      |
| `mcplense login --profile <name>`| Force interactive login for one profile.                                  |
| `mcplense login <url>`           | Resolve URL via auto-pick, then log in to the matched profile.            |
| `mcplense logout --all`          | Clear cached state for every loaded profile (per-account, MSAL-safe).     |
| `mcplense logout --profile <n>`  | Clear cache for one profile.                                              |
| `mcplense logout <url>`          | Resolve URL via auto-pick, then clear that profile's cache.               |

Bare `mcplense login` (or `logout`) errors with a hint listing the three forms.

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

mcplense login --profiles samples/agent365.json --profile agent365

# Subsequent commands re-use the cached token automatically:
mcplense tools https://agent365.svc.cloud.microsoft/.../mcp_MailTools `
  --profiles samples/agent365.json
```

### Microsoft 365 / Entra ID (Azure CLI)

If you'd rather **not** see a browser pop, use `auth.type: azure-cli`. McpLense
delegates every token acquisition to the Azure CLI by shelling out to
`az account get-access-token --resource <scope>`. This requires:

- **Azure CLI installed and on PATH.**
- **A prior `az login`** (or device-code / service-principal session). McpLense
  doesn't perform the login itself — it inherits whatever identity `az` already
  has cached.
- The signed-in account having access to the target Entra app.

There is **no `clientId`** field — the Azure CLI uses its own pre-registered
first-party app. There is **no `cacheName`** either — `az` manages its own
credentials cache (see `az account list`). `tenantId` is optional and defaults
to whatever `az account show` reports.

```json
{
  "authProfiles": [
    {
      "name": "agent365-cli",
      "auth": {
        "type": "azure-cli",
        "tenantId": "env:CORP_TENANT_ID",
        "scopes": ["${VSCODE_AUDIENCE}/.default"]
      }
    }
  ]
}
```

Trade-offs vs `interactive-browser`:

| Aspect                       | `interactive-browser`                  | `azure-cli`                              |
| ---------------------------- | -------------------------------------- | ---------------------------------------- |
| First-time setup             | One browser pop per profile            | One-time `az login` (covers all profiles) |
| Per-request overhead         | MSAL cache hit (~ms)                   | Shell out to `az` (~100–500ms)            |
| Browser required             | Yes (at least first time)              | Never                                     |
| Tenant switching             | Per-profile `tenantId`                 | `az account set` or `tenantId`            |
| Works in SSH / CI / headless | Only with `MCPLENSE_NO_BROWSER=1` + remote port forwarding | Yes, trivially                          |
| Identity source              | Whatever you sign into the browser as  | Current `az account show` identity        |

You can have **both profiles loaded simultaneously** (see `samples/agent365.json`)
and pick at command time with `--profile agent365` vs `--profile agent365-cli`.

```bash
# Verify your az session can mint the right token:
mcplense login --profile agent365-cli --profiles samples/agent365.json

# Then use it:
mcplense inspect https://agent365.svc.cloud.microsoft/.../mcp_MailTools `
  --profile agent365-cli --profiles samples/agent365.json
```

`mcplense logout --profile agent365-cli` is a no-op other than printing a
reminder to run `az logout` if you actually want to clear the CLI session.

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
you to run `mcplense login` on a workstation. This is the recommended posture for CI
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
- `--timeout <seconds>` (default 30s) bounds connecting and listing. Invocations
  (`call` / `read` / `prompt`, including in the TUI) get at least a 10-minute window so a
  legitimately slow tool isn't cut off; a larger `--timeout` still wins, and the interactive
  TUI lets you cancel a running call with `Esc`. A call that runs past the deadline (or whose
  connection keeps dropping) fails with a hint to raise `--timeout`; SSE reconnection is
  capped so a flapping server can't overrun the deadline by minutes.
- Every live connection receives the server-initiated half of the protocol
  (`sampling/createMessage`, `elicitation/create`, `roots/list`, notifications). One-shot
  `inspect` / `call` log what the server tried (and answer with safe defaults: refuse
  sampling, decline elicitation, no roots); the TUI shows it in a `server-initiated` table
  after each invocation. Pass `--server-stream` (tui, or interactive `call` / `read` /
  `prompt`) to also keep the standalone GET event-stream open so idle server traffic
  surfaces; it's suppressed by default because some Streamable-HTTP servers drop the POST
  session when a parallel GET stream is opened.
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
