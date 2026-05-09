# McpLense

`McpLense` is a `.NET tool` for debugging MCP servers.

It can:

- inspect one or many servers
- browse them in a TUI
- list tools, resources, prompts, and resource templates
- call tools
- read resources
- resolve prompts
- connect from a config file, an HTTP URL, or a stdio command
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

## dotnet run examples

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
