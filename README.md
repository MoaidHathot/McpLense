# McpLense

A .NET CLI **and** library for exploring, debugging, and security-scanning
[Model Context Protocol (MCP)](https://modelcontextprotocol.io) servers. Inspect what a server
exposes, classify its auth, audit its TLS/OAuth posture, turn the facts into severity-rated security
findings (with a CI gate and SARIF output), generate example calls, triage "why won't it connect?",
trace the JSON-RPC wire, and even run McpLense itself as an MCP server so an agent can audit other
MCPs.

The scan pipeline is deliberately **fact-only** (it extracts data, never labels it); the opt-in
`analyze` layer is a separate consumer that classifies those facts into findings.

## Quick start

McpLense ships as the `McpLense.Cli` .NET tool (command: `mcplense`). On **.NET 10** you can run it
without installing anything using `dnx`:

```bash
dnx McpLense.Cli inspect https://mcp.context7.com/mcp          # list tools / resources / prompts
dnx McpLense.Cli analyze https://your-server/mcp --fail-on high # security findings (CI gate)
dnx McpLense.Cli explain https://your-server/mcp               # plain-language "what is this MCP"
dnx McpLense.Cli doctor  https://your-server/mcp               # "why won't it connect?" triage
```

Prefer a persistent install? Install once and use the `mcplense` command:

```bash
dotnet tool install -g McpLense.Cli
mcplense inspect https://mcp.context7.com/mcp
```

For a stdio MCP server, pass the command after `--`:

```bash
dnx McpLense.Cli inspect -- npx -y @modelcontextprotocol/server-everything
```

## What it does

- **Explore** — `inspect` / `tools` / `resources` / `prompts`; `call` / `read` / `prompt` to invoke;
  `call <tool> --example` generates a ready-to-edit `--args` template; `explain` narrates a server in
  plain language; `mcplense tui` is an interactive explorer.
- **Secure** — `scan` runs the fact-only check pipeline; `analyze` turns it into severity-rated
  **findings** (prompt-injection signals, anonymous destructive tools, weak CORS, TLS posture, rug-pull
  detection, …). `--fail-on <severity>` is a CI gate; `--format sarif` uploads to GitHub code scanning;
  `--approve`/`--since` snapshot and detect tool changes.
- **Debug** — `doctor` walks DNS → TCP → TLS → MCP initialize → auth with fix-it hints; `--trace` logs
  the JSON-RPC wire traffic; `--watch <seconds>` re-runs on an interval and flags changes.
- **Embed** — reference the `McpLense` library to run scans in your own tooling or add custom
  `IScanCheck` / finding rules.
- **Serve** — `mcplense serve` runs McpLense as a stdio MCP server, exposing
  `mcplense_inspect` / `mcplense_scan` / `mcplense_analyze` / `mcplense_explain` as tools.

```csharp
// Library: run the scan pipeline in-process
using McpLense.Scanning;
using McpLense.Analysis;

var report   = await ScanCommandDispatcher.RunAsync(target, timeout, null, null, ct);
var findings = new FindingsAnalyzer().Analyze(report);
```

## Documentation

- **Agent Skill** — `skills/mcplense/` is a portable [Agent Skill](https://agentskills.io/); its
  `references/` cover every command, auth, config, checks, and classification recipes.
- **Findings & CI** — [`docs/analysis-rules.md`](docs/analysis-rules.md) (built-in rules, `analysis`
  config block, SARIF, rug-pull).
- **Scan checks** — [`docs/scan-checks.md`](docs/scan-checks.md) (every `IScanCheck` and its output).
- **Roadmap** — [`next.md`](next.md).

## License

[Unlicense](https://unlicense.org) — public domain.
