# Next steps

Living roadmap.

## T4: internal refactors + latent bug fixes (this session, pending release)

Pure-internal refactors (no user-visible behavior change) plus three latent bugs
found while adding the characterization tests that made the refactors safe. Each
refactor was preceded by gap-filling characterization tests and verified
behavior-preserving (746 unit + 69 integration + 35 E2E + 6 gated, all green;
Release `-warnaserror` clean).

- **A4 - CommandLine option registry.** Replaced the ~15 scattered "X is only valid
  for Y" guards plus a separately-maintained known-options allowlist with one
  declarative `RestrictedOptions` registry (option -> allowed commands + message);
  the known set is now DERIVED from the union so the two can't drift. Adding a
  verb-restricted option is a single registry entry.
- **A3 - AuthScanner split.** `AuthScanner` (521 lines) split into `AuthClassifier`
  (pure decision ladder + derivations + heuristics, fully unit-testable), `AuthDiscovery`
  (owns the `IAuthProbe`: RFC 9728 probe + scope substitution), and a slim `AuthScanner`
  orchestrator. The `(IAuthProbe, IMcpHandshakeProbe)` ctor + static derivations are
  preserved so all existing tests stay green.
- **A1 - McpExecutor dictionary dispatch.** The ~155-line if-chain + switch in
  `ExecuteAsync` became a registry of per-command `ICommandHandler`s, each declaring a
  3-state `ServerResolution` (None / ResolveOnly / ResolveAndAuthenticate). The executor
  runs exactly that much shared pipeline then dispatches. Handlers are nested in a
  partial `McpExecutor` so they reuse the existing private helpers with no visibility
  changes. Adding a command is register-a-handler.

Latent bugs caught by the new characterization tests and fixed:

- **`mcplense diff <a> <b>` was unreachable** - threw "Specify a target" because
  `ParseTarget` only relaxed the target requirement for `--targets-from`, not `diff`.
- **`--scan-plugin` was rejected as "Unknown option"** - registered as repeatable with a
  scan-only guard but missing from the `ValidateOptions` allowlist (the drift the A4
  registry now prevents structurally).
- **`observe` double-wrapped its payload** - `DispatchObserveAsync` already returns an
  `ExecutionOutcome`, but `ExecuteAsync` wrapped it again, so its payload was an
  `ExecutionOutcome` instead of a `ScanReport` (inconsistent with `scan`).

When released, the bug fixes warrant a version bump (likely 0.12.0).

## Delivered in 0.11.0

- **T1.1 Shared HTTP client factory.** New `src/McpLense/Mcp/McpHttpClientFactory.cs`
  is the single source of truth for the `HttpClient` behind every live MCP
  connection (standalone-stream suppression, auth attachment, timeout). Wired into
  the four creation sites - `McpExecutor.ConnectHttpAsync`, `McpHandshakeProbe`,
  `ScanPipeline.OpenSessionForAsync`, and `ServerInitiatedObservationCheck`. Folding
  scan-session creation onto the factory fixed a session loss (`-32001`) on POST-only
  Streamable-HTTP servers during enumeration (the CVM triage bridge now enumerates
  `triage_icm_incident`). New `McpHttpClientFactoryTests`.
- **T1.2 Connection auth status surfaced.** `tools` / `resources` / `prompts` /
  `inspect` / `call` / `read` / `prompt` now report how the connection authenticated
  (`auth: anonymous (no credentials sent)` or `authenticated (profile ...)`), threaded
  as `ConnectionAuthInfo` through the reports and rendered by `TextFormatter`.
- **T2.3 Bounded reconnection + clearer errors.** SSE reconnection is capped at 2
  attempts so a server that keeps dropping a long call can't overrun the invoke
  deadline by minutes. Cancellations now suggest raising `--timeout`, connection-drop
  failures get a distinct hint, and status-bearing 401/404s are left untouched. New
  `FormatExceptionTests`.
- **T2.4 Receive the server-initiated half of the protocol.** Every live client now
  advertises sampling / elicitation / roots and wires handlers via a new
  `IServerInteraction`. The default `LoggingServerInteraction` logs each request /
  notification and answers with safe defaults (refuse sampling, decline elicitation,
  no roots) so one-shot `inspect` / `call` SEE what the server tried. The TUI's
  `TuiServerInteraction` captures the traffic and renders a `server-initiated` table
  after each invocation. New `--server-stream` flag (tui + interactive
  call/read/prompt) keeps the standalone GET event-stream open so idle server traffic
  surfaces; suppressed by default for session safety.
- **T3.6 Opt-in cross-style smoke tests.** `MCPLENSE_PUBLIC_SMOKE`-gated E2E smokes
  drive the CLI against a POST-only Streamable-HTTP server (CVM triage bridge) and a
  FastMCP server (compute-insights lens), guarding the shared client factory
  end-to-end. `CliRunner` gained an environment overload so they pin
  `MCPLENSE_NO_PROFILE_AUTO_DISCOVERY=1`.

Build + test state: Debug clean.
 709 unit + 63 integration + 35 E2E + 6 skipped (gated remote smokes) = 807 total, all green.

The T4 refactors (A1 / A3 / A4) that were carried forward here are now done - see "T4:
internal refactors + latent bug fixes" at the top.

## Delivered in 0.6.0

- **D4 ILogger bridge complete**. New `McpLense.Diagnostics.McpLenseLog` static façade
  is the single sink for every diagnostic line. Default writes verbatim to
  `Console.Error` (so existing test assertions on exact stderr lines still pass);
  embedding hosts call `McpLenseLog.UseLoggerFactory(factory)` to redirect the same
  lines into their own `ILogger` pipeline (Serilog, NLog, OpenTelemetry, etc.).
  Pass `NullLoggerFactory.Instance` to silence everything for `--quiet`-style
  host wrappers. Migrated call sites: `McpExecutor` (10), `ScanCommandDispatcher`,
  `App.cs` (6), `SchemaCommand.cs`. 6 new unit tests in
  `tests/McpLense.UnitTests/Diagnostics/McpLenseLogTests.cs`.
- **C5 TUI polish complete**, all three sub-features:
  - **Search/filter** across every section (Tools / Resources / Resource Templates /
    Prompts) via a `[Search…]` choice that opens a `TextPrompt`. Substring,
    case-insensitive, matches `name + description` for tools/prompts and
    `name + uri + description` for resources. A `[Clear filter]` choice appears
    once a filter is active. Filter state is section-scoped.
  - **Tool detail / inline JSON Schema preview**. Selecting a tool from the list
    opens a detail view that renders `tool.inputSchema` as a Spectre `Tree`.
    Recognises the JSON-Schema `type` + `properties` + `required` shape and emits
    one subtree per top-level property with a required-marker (`*`), type, and
    description.
  - **Bookmarks**. New `TuiBookmarkStore` persists `(serverName, kind, name)`
    triples to `$XDG_DATA_HOME/McpLense/tui-bookmarks.json` (Windows:
    `%LOCALAPPDATA%\McpLense\`). Toggle from any tool/resource/prompt detail
    view; "Bookmarks" section per server lists them. Atomic write via .tmp +
    `File.Move(overwrite: true)`. Malformed file degrades to empty store rather
    than crashing the TUI. 17 new unit tests in `TuiPolishTests.cs`.

Build + test state: Release `-warnaserror` clean.
579 unit + 56 integration + 35 E2E + 3 skipped (gated remote smokes) = 670 total, all green.

## Delivered in 0.5.0

- **`mcplense schema [config] [--output <path>]`** verb. Emits the embedded
  JSON Schema for `McpLense.Config.json` (auth profiles, targets,
  targetPatterns, scan). Schema is shipped both as an embedded resource in
  `McpLense.Cli` and as a stable file at `docs/schema/mcplense-config.schema.json`
  for editor `json.schemas` consumers (VS Code, JetBrains, etc.).
- **Scan plugins via `--scan-plugin <path>`**. Repeatable. Accepts a single
  `.dll` or a directory of `*.dll`. Loads via `ScanPluginLoader` into a
  collectible, per-plugin `AssemblyLoadContext` that shares only the host's
  `McpLense` assembly so `IScanCheck` identity holds across the boundary.
  Discovered types need a public parameterless ctor; others are skipped silently.
  A plugin check whose `Id` matches a built-in replaces the built-in.
  Plugin load failures surface as `ScanPluginException` and never abort the run.
- **A2 (HttpClient factory unification)**:
  - `DcrEndpointCheck` now prefers the `mcplense-probe` named client when
    `AddMcpLense` registered an `IHttpClientFactory`; falls back to a one-off
    `HttpClient` for the no-DI `ScanPipelineBuilder.UseServices(...)` path.
  - `TransportProbe` and `AuthorizationServerProbe` gained
    `(IHttpClientFactory)` constructor overloads. `TransportProbe` keeps owning
    its `SocketsHttpHandler` (TLS cert capture requires it) but inherits the
    factory's timeout config so operator overrides flow through.
    `AuthorizationServerProbe` reuses the factory's client wholesale.
  - `TransportCheck` and `AuthorizationServersCheck` pick the factory-backed
    overload via DI lookup, falling back to the parameterless ctor when DI is
    absent.
  - `AuthenticatedHeadersCheck` deliberately stays on per-request handlers
    because it wraps an auth `DelegatingHandler` per call - shared sockets
    don't apply.

Build + test state: Release `-warnaserror` clean.
556 unit + 56 integration + 35 E2E + 3 skipped (gated remote smokes) = 647 total, all green.

## Audit results (no-op deliveries)

The following items from the user's request list turned out to already be
implemented; we documented + verified rather than re-implementing:

- **D1 (parallelise independent scan checks)**: already shipped.
  `ScanPipeline.BuildTiers` does a topological layering by `DependsOn` and runs
  each tier with `Task.WhenAll`. Multi-server parallelism is also wired via
  `maxDegreeOfParallelism` + a bounded `SemaphoreSlim`. See
  `src/McpLense/Scanning/ScanPipeline.cs:192` (per-server tier dispatch) and
  `:102` (cross-server bounded fan-out).
- **D2 (session reuse across checks)**: already shipped. `ScanContext.GetSessionAsync`
  memoises the open session under `_sessionLock` and disposes once at scan
  teardown (`ScanContext.cs:112`). Every check that reaches for a session
  shares it.
- **D3 (cancellation hygiene)**: already shipped. `ScanPipeline.RunCheckAsync`
  distinguishes the pipeline's own user-cancellation token from internal
  per-request / per-handshake timeouts: real user cancels propagate; per-check
  timeouts surface as `CheckOutcome.Error = "Timed out."` so one slow probe
  can't kill the rest of the report (`ScanPipeline.cs:241-258`).

## Carried forward to a follow-up session

- **A1 / A3 / A4 refactors: DONE.** Delivered in the T4 refactor pass - see "T4:
  internal refactors + latent bug fixes" at the top of this file. (A1 McpExecutor
  dictionary dispatch, A3 AuthScanner split, A4 CommandLine option registry.)

## Delivered in 0.4.0

- **AI Agent Skill**: `skills/mcplense/` is a portable [Agent Skill](https://agentskills.io/)
  folder. Any skills-aware client (Claude Code / Claude / Cursor / OpenCode /
  Goose / Gemini CLI / OpenHands / GitHub Copilot / Roo Code / Kiro / ...) can drop the
  folder under its skills root and the agent will discover + load it. Includes a concise
  main `SKILL.md`, reference docs for commands / config / auth / checks / classification,
  plus runnable bash helpers under `scripts/`.
- **Single shared version**: every NuGet package (`McpLense` library + `McpLense.Cli` tool)
  derives its version from `<Version>` in the root `Directory.Build.props`. Bumping that
  single property ships both packages at the lockstep version. Per-project csproj files
  no longer carry a `<Version>` override.
- Per-target headers (`targets[]`) and pattern overlays (`targetPatterns[]`) in
  `McpLense.Config.json`. CLI gains `@<name>` positional syntax for named targets.
  Per-key last-write-wins merge with three layers: pattern -> target -> CLI flag.
- Overlay applies uniformly across EVERY command that opens an MCP connection
  (`scan`, `inspect`, `tools`, `resources`, `prompts`, `call`, `read`, `prompt`,
  `fetch-resource`, `auth-scan`, `observe`) - not just `scan`. Shared
  `TargetOverlayApplicator` is used by both `ScanCommandDispatcher` and `McpExecutor`.
- Probe coverage gap closed: with `scope: "All"` (the default), per-target headers
  ride along with same-origin probes (TransportProbe, CorsPreflightCheck,
  AuthenticatedHeadersCheck, DcrEndpointCheck, AuthProbe + RFC 9728 metadata fetch).
  Cross-origin fetches NEVER get MCP-server headers. `scope: "Session"` reverts to
  the previous session-only behaviour per target.
- Per-target `disabledChecks`, `profile`, `transport`, `timeoutSeconds` wiring.
- Stderr observability:
  - `matched: patterns=N target=NAME -> K headers, scope=...` line under non-quiet.
  - `--verbose` adds per-header `name: value` lines (sensitive header values redacted
    to length-only).
  - `auth: ...` lines for every non-scan command: profile load, probe classification,
    cache hits, picked profile + reason (cache-hit vs precedence vs single-profile).
- New URL-glob matcher (`UrlGlob`): single `*` = one host label OR one path segment,
  `**` = any sequence including `/`, host case-insensitive, path case-sensitive.
- New test MCP mode `headers` that records inbound HTTP requests via a `/capture`
  endpoint; new integration tests assert per-target headers reach the right
  surface (session vs CORS preflight) under the two scope modes AND on non-scan
  commands via McpExecutor.
- Docs + sample: `docs/scan-checks.md#per-target-configuration`, README "Per-target
  headers (config file)" section, `samples/targets.json` worked example.

Build + test state: Release `-warnaserror` clean. 543 unit + 56 integration + 35 E2E
+ 3 skipped (gated remote smokes).

## Earlier deliveries

- 0.3.x preview: scan pipeline (16 built-in checks), library / CLI split, baseline +
  diff, observe / fetch-resource / diff commands.
- Inbound interception (sampling / elicitation / roots + 6 notifications).
- Cancellation hygiene, direct property access on stable SDK types, per-check
  structured text renderers.
- In-repo test MCPs (`bare`, `rich`, `sampling`, `leaky`).
- Remote-target smoke fixture (`MCPLENSE_E2E_REMOTE=1` gated).
- `pack.ps1` packs both packages; `-LibraryOnly` / `-CliOnly` / `-Push`.
- README scan + extensibility section; `docs/scan-checks.md` per-check reference;
  `docs/security-classification-recipes.md` jq recipes.

## Carried forward

(moved up - see "Carried forward to a follow-up session" above for A1, A3, A4, C5, D4.)

## Section 2 (new checks)

2.1 behavior.callMalformed.
2.2 wellKnownProbes (allowlist-only).
2.3 tlsDeep (cipher enumeration).
2.4 DNS / ASN enrichment.
2.5 Token-claims inspection (opt-in --inspect-tokens).
2.6 Stable resource-id resolution.

## Long-shot ideas

Watch mode. Cross-server correlation in diff engine. HTML rendering.
Web/TUI dashboard. Policy-engine integration.
(AssemblyLoadContext plugin loading: shipped in 0.5.0 - see top.)
