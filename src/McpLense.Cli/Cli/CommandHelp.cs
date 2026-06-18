namespace McpLense;

/// <summary>
/// Per-command help text. <c>mcplense &lt;cmd&gt; --help</c> (or <c>mcplense help &lt;cmd&gt;</c>)
/// shows a focused synopsis for that command instead of the full reference; the global
/// <c>mcplense help</c> still prints <see cref="CommandLineHelp.Text"/>.
/// </summary>
internal static class CommandHelp
{
    /// <summary>
    /// Resolves the help text for a topic. <paramref name="topic"/> is the
    /// <see cref="AppCommand"/> name stored in the parsed help command's subject; null /
    /// unknown / topics without focused help fall back to the full reference.
    /// </summary>
    public static string For(string? topic)
    {
        if (!string.IsNullOrEmpty(topic)
            && Enum.TryParse<AppCommand>(topic, ignoreCase: true, out var command)
            && Topics.TryGetValue(command, out var text))
        {
            return text;
        }

        return CommandLineHelp.Text;
    }

    private static readonly IReadOnlyDictionary<AppCommand, string> Topics = new Dictionary<AppCommand, string>
    {
        [AppCommand.Inspect] = """
mcplense inspect - one-shot snapshot of a server's capabilities, tools, resources,
resource templates, and prompts.

Usage
  mcplense inspect <url|@target> [common-options]
  mcplense inspect --config <path> [--server <name>] [common-options]
  mcplense inspect -- <command> [args...]

Options
  --format <text|json|jsonl|dumpify>   Output format (default: text).
  --timeout <seconds>                  Per-server timeout (default: 30).
  --default-scope <value>              Fallback OAuth scope for AAD-backed MCPs.

Examples
  mcplense inspect https://localhost:3000/mcp --format json
  mcplense inspect -- npx -y @modelcontextprotocol/server-everything

Run 'mcplense help' for targets, auth, config, and the full reference.
""",

        [AppCommand.Tools] = """
mcplense tools - list the tools a server exposes (name, description, input schema).

Usage
  mcplense tools <url|@target> [common-options]
  mcplense tools --config <path> [--server <name>] [common-options]
  mcplense tools -- <command> [args...]

Options
  --format <text|json|jsonl|dumpify>   Output format (default: text).
  --timeout <seconds>                  Per-server timeout (default: 30).

Example
  mcplense tools --config mcp.json --server everything --format json

Run 'mcplense help' for targets, auth, config, and the full reference.
""",

        [AppCommand.Resources] = """
mcplense resources - list a server's resources AND resource templates.

Usage
  mcplense resources <url|@target> [common-options]
  mcplense resources --config <path> [--server <name>] [common-options]

Options
  --format <text|json|jsonl|dumpify>   Output format (default: text).
  --timeout <seconds>                  Per-server timeout (default: 30).

Example
  mcplense resources https://localhost:3000/mcp --format json

Run 'mcplense help' for targets, auth, config, and the full reference.
""",

        [AppCommand.Prompts] = """
mcplense prompts - list a server's prompts and their arguments.

Usage
  mcplense prompts <url|@target> [common-options]
  mcplense prompts --config <path> [--server <name>] [common-options]

Options
  --format <text|json|jsonl|dumpify>   Output format (default: text).
  --timeout <seconds>                  Per-server timeout (default: 30).

Example
  mcplense prompts https://localhost:3000/mcp

Run 'mcplense help' for targets, auth, config, and the full reference.
""",

        [AppCommand.Call] = """
mcplense call - invoke a tool and print the result.

Usage
  mcplense call <tool-name> <url|@target> [--args <json> | --interactive] [common-options]
  mcplense call <tool-name> --config <path> --server <name> [--args <json> | --interactive]
  mcplense call <tool-name> [--interactive] -- <command> [args...]

Options
  --args <json>        Tool arguments as a JSON object, e.g. '{"message":"hi"}'.
  -i, --interactive    Prompt for each argument from the tool's input schema; required
                       args are marked *, declared defaults are pre-filled (Enter accepts).
  --server-stream      Keep the server->client event-stream open so sampling / elicitation /
                       roots / notifications surface during the call (needs --interactive).
  --progress [bool]    Show live tool-call progress (default: true).
  --format <...>       Output format (default: text).
  --timeout <seconds>  Per-server timeout (default: 30).

Examples
  mcplense call echo --url https://localhost:3000/mcp --args '{"message":"hi"}'
  mcplense call Add https://localhost:3000/mcp --interactive

Run 'mcplense help' for targets, auth, config, and the full reference.
""",

        [AppCommand.Read] = """
mcplense read - read a resource, or expand a URI template, and print its contents.

Usage
  mcplense read <uri-or-template> <url|@target> [--args <json> | --interactive] [common-options]

Options
  --args <json>        Template variables as a JSON object, e.g. '{"id":"42"}'.
  -i, --interactive    Prompt for each {variable} in the URI template.
  --server-stream      Keep the server->client event-stream open (needs --interactive).
  --format <...>       Output format (default: text).
  --timeout <seconds>  Per-server timeout (default: 30).

Examples
  mcplense read config://app/settings https://localhost:3000/mcp
  mcplense read 'docs://articles/{id}' https://localhost:3000/mcp --interactive

Run 'mcplense help' for targets, auth, config, and the full reference.
""",

        [AppCommand.Prompt] = """
mcplense prompt - fetch a prompt's rendered messages.

Usage
  mcplense prompt <prompt-name> <url|@target> [--args <json> | --interactive] [common-options]

Options
  --args <json>        Prompt arguments as a JSON object.
  -i, --interactive    Prompt for each declared argument.
  --server-stream      Keep the server->client event-stream open (needs --interactive).
  --format <...>       Output format (default: text).
  --timeout <seconds>  Per-server timeout (default: 30).

Examples
  mcplense prompt Greet https://localhost:3000/mcp --args '{"name":"world"}'
  mcplense prompt CodeReview https://localhost:3000/mcp --interactive

Run 'mcplense help' for targets, auth, config, and the full reference.
""",

        [AppCommand.Tui] = """
mcplense tui - interactive terminal explorer that can also invoke tools/resources/prompts.

Usage
  mcplense tui <url|@target> [common-options]
  mcplense tui --config <path> [common-options]
  mcplense tui -- <command> [args...]

Browse tools / resources / resource templates / prompts (per-section search + persistent
bookmarks) and invoke them: pick a tool and "Call tool" to be prompted for each argument
(required marked *, declared defaults pre-filled), then see the result. Reading resources
/ templates and getting prompts work the same way. Requires an interactive terminal.

Options
  --server-stream      Keep the server->client event-stream open so server-initiated
                       traffic (sampling / elicitation / roots / notifications) is shown.

Examples
  mcplense tui https://localhost:3000/mcp
  mcplense tui -- npx -y @modelcontextprotocol/server-everything

Run 'mcplense help' for targets, auth, config, and the full reference.
""",

        [AppCommand.Scan] = """
mcplense scan - run the full IScanCheck audit pipeline against one or more targets.

Usage
  mcplense scan [<url|@target>] [target-options] [scan-options] [common-options]

Scan options
  --classify-only                 Skip profile attempts and dependent enumeration.
  --check-authorization-servers   Fetch advertised authorization-server metadata.
  --enable <check-id>             Force-enable a check (repeatable).
  --disable <check-id>            Force-disable a check (repeatable).
  --baseline <path>               Write the JSON report to <path> (file or directory).
  --diff <baseline-path>          Diff against a previous baseline instead of the report.
  --scan-plugin <path>            Load external IScanCheck assemblies (repeatable).
  --targets-from <path>           Read targets (one URL/@name per line) (repeatable).
  --parallel-servers <n>          Scan up to n servers concurrently.
  --http-only                     Drop stdio targets after resolution.
  --findings                      Also run the analysis layer and emit facts + findings.
  --fail-on <severity>            With --findings: exit non-zero if a finding >= severity.
  --quiet | --verbose             Progress verbosity.

Examples
  mcplense scan https://api.example.com/mcp --check-authorization-servers
  mcplense scan --targets-from fleet.txt --parallel-servers 8 --format jsonl

Run 'mcplense help' for targets, auth, config, and the full reference.
""",

        [AppCommand.Analyze] = """
mcplense analyze - run the scan pipeline, then classify the facts into severity-rated findings.

Usage
  mcplense analyze [<url|@target>] [target-options] [scan-options] [common-options]

The scan stays fact-only; analyze is a separate opt-in consumer that applies a built-in rule
pack (prompt-injection signals, tool poisoning, open-shape input, weak CORS, TLS posture, ...)
and emits a findings report. Rules and their severities are configurable in McpLense.Config.json
under the top-level "analysis" block; nothing about the underlying scan facts changes.

Options
  --fail-on <severity>            Exit non-zero if any finding >= severity (info/low/medium/
                                  high/critical). Overrides analysis.failOn from config.
  --approve <file>                Snapshot the current tool/prompt/resource hashes as the
                                  approved baseline (the trust anchor for rug-pull detection).
  --since <file>                  Flag any tool/prompt/resource that changed since the approved
                                  baseline as a 'rug-pull' finding.
  --format sarif                  Emit SARIF 2.1.0 (for GitHub code scanning / CI security).
  --enable / --disable <id>       Toggle scan checks (findings depend on the facts they emit).
  --check-authorization-servers   Fetch advertised authorization-server metadata.
  --scan-plugin <path>            Load external IScanCheck assemblies (repeatable).
  --targets-from <path>           Analyze a fleet (one URL/@name per line) (repeatable).
  --parallel-servers <n>          Scan up to n servers concurrently.
  --format <text|json|sarif|...>  Output format (default: text).

Examples
  mcplense analyze https://api.example.com/mcp
  mcplense analyze https://api.example.com/mcp --fail-on high          # CI gate
  mcplense analyze https://api.example.com/mcp --format sarif > out.sarif
  mcplense analyze https://api.example.com/mcp --approve approved.json # trust it now
  mcplense analyze https://api.example.com/mcp --since approved.json --fail-on high  # detect rug-pull

Run 'mcplense help' for targets, auth, config, and the full reference.
""",

        [AppCommand.Explain] = """
mcplense explain - a plain-language summary of what an MCP server is and whether it looks safe.

Usage
  mcplense explain [<url|@target>] [target-options] [common-options]

Runs the scan pipeline, then narrates it: server identity, auth posture (e.g. "anonymous - anyone
who can reach it can use it"), how many tools/resources/prompts it exposes, which tools the server
declares destructive or open-world, and a one-line findings summary. Use `--format markdown` for a
shareable write-up, or `--format json` for the structured form.

Examples
  mcplense explain https://api.example.com/mcp
  mcplense explain https://api.example.com/mcp --format markdown > server.md

Run 'mcplense help' for targets, auth, config, and the full reference.
""",

        [AppCommand.Serve] = """
mcplense serve - run McpLense itself as an MCP server (stdio), so an agent can audit OTHER MCPs.

Usage
  mcplense serve

Exposes tools: mcplense_inspect, mcplense_scan, mcplense_analyze, mcplense_explain - each takes an
MCP server URL and returns the corresponding JSON report. Add it to an MCP host's config like any
other stdio server; the agent can then introspect and security-scan MCP servers on demand.

Examples
  mcplense serve
  mcplense inspect -- mcplense serve     # inspect McpLense-as-an-MCP-server

Run 'mcplense help' for targets, auth, config, and the full reference.
""",

        [AppCommand.Doctor] = """
mcplense doctor - "why won't this MCP connect?" staged connectivity triage.

Usage
  mcplense doctor [<url|@target>] [target-options] [common-options]

Walks DNS -> TCP -> TLS -> MCP initialize -> auth classification and reports exactly which stage
failed, with a hint (e.g. "the transport may be mismatched - try --transport sse", or "the server
requires authentication - pass --profile"). For stdio targets it runs the spawn + initialize. Exit
code is non-zero if any stage failed. Distinct from `scan` (an audit) - this is a first-aid kit.

Examples
  mcplense doctor https://api.example.com/mcp
  mcplense doctor -- npx -y @modelcontextprotocol/server-everything

Run 'mcplense help' for targets, auth, config, and the full reference.
""",

        [AppCommand.AuthScan] = """
mcplense auth-scan - minimal, read-only auth classification + profile probing.

Usage
  mcplense auth-scan [<url|@target>] [target-options] [common-options]

Options
  --classify-only    Emit only the classification block (no profile attempts).
  --no-auth          Same as --classify-only here; also strips inline auth elsewhere.
  --profiles <path>  Load profile entries from a file (repeatable).
  --profile <name>   Try only this single profile.

Examples
  mcplense auth-scan https://server.example/mcp --classify-only
  mcplense auth-scan https://server.example/mcp --profiles ./agent365.json --profile agent365

Run 'mcplense help' for targets, auth, config, and the full reference.
""",

        [AppCommand.Observe] = """
mcplense observe - hold a session open and record server-initiated messages.

Usage
  mcplense observe [<url|@target>] [target-options] [common-options]

Runs only the auth + behavior.serverInitiated checks. Use --timeout to cap the
observation duration (the configured observationDurationSeconds is honoured first).

Example
  mcplense observe https://server.example/mcp --timeout 10

Run 'mcplense help' for targets, auth, config, and the full reference.
""",

        [AppCommand.FetchResource] = """
mcplense fetch-resource - read a named resource (alias of `read`, kept for pipelines).

Usage
  mcplense fetch-resource <uri> [<url|@target>] [target-options] [common-options]

Example
  mcplense fetch-resource config://app/settings https://server.example/mcp

Run 'mcplense help' for targets, auth, config, and the full reference.
""",

        [AppCommand.Diff] = """
mcplense diff - structural diff of two scan baseline files. No network.

Usage
  mcplense diff <baseline-before> <baseline-after>

Example
  mcplense diff ./baselines/old.json ./baselines/new.json
""",

        [AppCommand.Schema] = """
mcplense schema - print the McpLense.Config.json JSON Schema.

Usage
  mcplense schema [config] [--output <path>]

Options
  -o, --output <path>   Write the schema to a file instead of stdout.

Example
  mcplense schema --output mcplense.schema.json
""",

        [AppCommand.Login] = """
mcplense login - acquire and cache credentials for auth profiles.

Usage
  mcplense login {--all | --profile <name> | <url>} [--profiles <path>] [common-options]

Options
  --all              Log in to every loaded profile (skip already-cached).
  --profile <name>   Log in to one named profile.
  <url>              Resolve the URL via auto-pick, then log in to the matched profile.
  --profiles <path>  Load profile entries from a file (repeatable).

Examples
  mcplense login --all
  mcplense login --profile agent365

Run 'mcplense help' for targets, auth, config, and the full reference.
""",

        [AppCommand.Logout] = """
mcplense logout - clear cached credentials for auth profiles.

Usage
  mcplense logout {--all | --profile <name> | <url>} [--profiles <path>] [common-options]

Options
  --all              Log out of every loaded profile.
  --profile <name>   Log out of one named profile.
  <url>              Resolve the URL via auto-pick, then log out of the matched profile.
  --profiles <path>  Load profile entries from a file (repeatable).

Examples
  mcplense logout --all
  mcplense logout --profile agent365

Run 'mcplense help' for targets, auth, config, and the full reference.
""",
    };
}
