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

> Environment variable expansion in config values is _not_ done by `mcplense`; substitute before invocation, or pass headers via `--header`.

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
