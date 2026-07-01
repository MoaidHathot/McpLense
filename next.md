# Next steps

Living roadmap.

## Delivered in 0.18.0 - implied URL scheme + richer TUI

Two usability features on top of the 0.17.2 TUI pass.

- **Implied URL scheme with https-&gt;http fallback.** A bare host now works everywhere a target is
  accepted - `mcplense inspect example.com/mcp`, `localhost:8080`, etc. - instead of erroring. The
  CLI recognises schemeless hosts (conservatively: a dotted host, `host:port`, or `localhost`; a
  lone word like `npx` is still treated as a stdio command), defaults to `https://`, and records a
  `TargetOptions.SchemeInferred` flag. `TargetResolver` then probes the inferred `https://`; if it
  doesn't respond but `http://` does, it switches to http and prints a warning
  (`no scheme was given and https://... did not respond; falling back to http://... (unencrypted)`).
  If neither answers it keeps https so the real connect error surfaces. The probe runs **only** when
  the scheme was inferred, so explicit-URL usage pays no latency. Because it lives in the shared
  `TargetResolver` choke-point it covers inspect / tui / call / read / prompt / tools / resources /
  prompts / scan / analyze / explain / doctor; `--targets-from` batch files also accept bare hosts
  (default https, no per-line probe at fleet scale). New `SchemeReachability` HEAD-probe helper
  (overridable for tests via `TargetResolver.ReachabilityProbe`).
- **Always-visible section counts + richer TUI.** The section menu now shows a compact counts bar
  under the items so you see what a server exposes without opening Overview:
  `\u25cf 5 tools   \u25cf 3 prompts   \u25cb 0 resources   \u25cb 1 template` (filled green dot = has items, hollow
  grey = empty, red = errored/unreachable). `TuiMenu.Select` gained an optional `renderStatusBar`
  slot rendered between the items and the keybinding footer. General polish: rounded, colour-bordered
  summary panel with a health dot + field separators, a branded header on the multi-server selection
  screen, per-row health dots in the server list, colour-coded detail panels (aqua tools / green
  resources / magenta prompts) with required args in red, and a titled, right-aligned, colourised
  Overview table. Deliberately no emoji (they render as tofu in many terminals) - only safe geometric
  glyphs and box drawing already used elsewhere.

Build + test state: 850 unit + 73 integration + 38 E2E (6 gated smokes skipped), all green; solution
Release build clean.

## Delivered in 0.17.2 - TUI reachability + single-server UX fixes

Three TUI explorer fixes driven by real friction opening an unreachable server.

- **Unreachable servers are no longer coloured green.** `TuiMenu.Select` gained an optional
  per-row `itemColors`; the server-selection list tints any server with a connection `Error`
  **red** - including the selected-row highlight, so an unreachable row stays red even when active
  (previously every row, reachable or not, used the green highlight).
- **The exact failure reason is surfaced, not a bare "(unreachable)".** New
  `TuiApp.DescribeConnectionFailure` distils the verbose error into a concise reason - HTTP
  statuses (`401 Unauthorized`, `403 Forbidden`, `404 Not Found`, `5xx`, ...) and transport
  failures (`timed out`, `connection refused`, `host not found`, `connection dropped`,
  `TLS error`). It checks transport reasons first and only reads a number as a status code in a
  genuine HTTP context, so an address/port fragment isn't mistaken for one. The reason now shows
  in the list (`(unreachable: 401 Unauthorized)`), the summary panel, and the section "Connection
  failed" notice (with the full raw error retained underneath).
- **A single resolved server auto-selects.** When `inspect` resolves exactly one server the TUI
  skips the "Select an MCP server" pre-form and opens straight into the section menu (Overview /
  Tools / Resources / ...); Back/Esc/q then exits the TUI (there is no server list to return to).
  The selection screen still appears for multi-server configs.

Build + test state: 836 unit (74 TUI), all green; solution Release build clean.

## Delivered in 0.17.0 - new check + MCP server mode (Phase 5)

The final phase of the 5-phase push.

- **`behavior.callMalformed`** check (opt-in, HTTP): sends deliberately malformed JSON-RPC (invalid
  JSON, valid-JSON-not-JSON-RPC, JSON-RPC missing `method`) and records how the server responds - a
  robustness signal. The analyze rule `malformed-handling` flags any `5xx` (the server didn't reject
  bad input gracefully). Found a real example live: a server that 500s on every malformed input.
- **`mcplense serve`**: runs McpLense itself as an MCP server over stdio, exposing
  `mcplense_inspect` / `mcplense_scan` / `mcplense_analyze` / `mcplense_explain` as tools so an agent
  can introspect and security-scan OTHER MCP servers on demand. Reuses the exact CLI pipeline.
  Verified by `mcplense inspect -- mcplense serve` (McpLense inspecting itself).

Deferred (lower value / higher effort, noted for a future pass): `tlsDeep` (cipher enumeration) and
`tokenClaims` (JWT claim inspection).

Build + test state: Release `-warnaserror` clean. 821 unit + 73 integration + 38 E2E + 6 gated.

The 5-phase "explore MCPs for learning, debugging, and security" arc (0.13.0 - 0.17.0) is complete:
findings + SARIF + rug-pull (security), explain + examples + markdown (learning), doctor + trace +
watch (debugging), and the callMalformed check + serve mode.

## Delivered in 0.16.0 - debugging aids (Phase 4)

Developer-facing diagnostics for people building their own MCP servers.

- **`mcplense doctor <url>`**: staged connectivity triage - DNS -> TCP -> TLS -> MCP initialize ->
  auth classification - reporting exactly which stage broke with a fix-it hint (auth required,
  transport mismatch, expired cert, ...). Stdio targets get spawn + initialize. Non-zero exit on a
  failed stage. `DoctorRunner` + `McpExecutor.TryStdioHandshakeAsync` for the stdio path.
- **`--trace`**: logs every HTTP MCP request/response (method, URL, JSON-RPC body, status,
  content-type, timing) to stderr via a `TraceLoggingHandler` inserted into the shared HTTP client
  factory. Buffers the request body (never consuming the sent content) and only reads non-streaming
  responses, so it can't break a live call. Works across every command that connects over HTTP.
- **`--watch <seconds>`**: re-runs a read-only command on an interval, clearing + re-rendering each
  cycle and flagging when the rendered output changed; Ctrl+C stops cleanly. A tight dev loop.

Deferred: stdio child-process stderr capture (the doctor stdio failure hint covers the common need;
full capture needs deeper SDK transport work).

Build + test state: Release `-warnaserror` clean. 818 unit + 73 integration + 37 E2E + 6 gated.

Remaining: Phase 5 - Section-2 checks (callMalformed / tlsDeep / tokenClaims) + `mcplense serve`.

## Delivered in 0.15.0 - learning aids (Phase 3)

Make any MCP easy to understand and a first call easy to write.

- **`mcplense explain <url>`**: runs the scan and narrates it in plain language - identity, auth
  posture ("anonymous - anyone who can reach it can use it"), tool/resource/prompt counts,
  server-declared destructive/open-world tools, and a one-line findings summary. `ExplainBuilder`
  (pure) over the scan facts + findings.
- **`call <tool> --example`**: connects, reads the tool's input schema, and prints a ready-to-edit
  `--args` template (generated by `SchemaSampleGenerator`) + the equivalent command, WITHOUT
  invoking. Copy-paste-edit for a first call; also reusable by the TUI.
- **`--format markdown`** (alias `md`): renders `explain` / `inspect` / findings as a shareable
  Markdown document (`MarkdownRenderer`); other payloads fall back to a fenced text block.

Build + test state: Release `-warnaserror` clean. 813 unit + 72 integration + 37 E2E + 6 gated.

Remaining phases: 4 debugging (trace + doctor + stdio stderr + watch); 5 Section-2 checks + serve.

## Delivered in 0.14.0 - SARIF + rug-pull (Phase 2)

Builds on the 0.13.0 findings layer for CI-grade security gating.

- **SARIF 2.1.0 output**: `--format sarif` (for `analyze` / `scan --findings`) maps findings to a
  SARIF run (severity -> error/warning/note, target as artifact location, evidence path as logical
  location) so they flow into GitHub code scanning. `SarifRenderer` + `OutputFormat.Sarif`.
- **Rug-pull detection**: `analyze --approve <file>` snapshots the current per-item `hashing` output
  as a trust anchor; `analyze --since <file>` re-scans and emits `rug-pull` findings for any
  tool/prompt/resource that changed (high), was added (medium), or removed (info) since approval.
  `RugPullAnalyzer` (pure) + the `rug-pull` rule severity is config-overridable like any other.
  With `--fail-on high` this fails CI the moment a trusted tool's definition changes.
- Docs: `docs/analysis-rules.md` (rug-pull + SARIF + a GitHub Actions snippet), help, COMMANDS.md.

Build + test state: Release `-warnaserror` clean. 796 unit + 70 integration + 37 E2E + 6 gated.

Remaining phases: 3 learning (explain + markdown/HTML + sample calls); 4 debugging (trace + doctor
+ stdio stderr + watch); 5 Section-2 checks + serve.

## Delivered in 0.13.0 - findings/analysis layer (Phase 1)

The first of a 5-phase push to make exploring MCPs easy for learning, debugging, and security.
A new opt-in analysis layer turns the fact-only scan into severity-rated findings, kept strictly
separate from the facts (the scan checks still never label anything).

- **`mcplense analyze <url>`** and **`scan --findings`**: run the scan, then apply a built-in rule
  pack via `FindingsAnalyzer` (a pure consumer of `ScanReport`) and emit a `FindingsReport`. Facts
  and findings never interleave - `analyze` emits findings only; `scan --findings` emits
  `{ scan, findings }` with separate top-level keys; plain `scan` is unchanged.
- **Built-in rules** (`src/McpLense/Analysis/Rules/`) codify the security-classification recipes:
  prompt-injection (hidden bidi/zero-width/control chars + instruction-hijacking phrases),
  anonymous-destructive, weak-cors, mixed-content, tls-chain-invalid, tls-expiry, open-shape-input,
  error-info-leak, description-url, missing-destructive-hint, unannounced-bearer. Each finding
  carries an evidence path back into the scan facts + a remediation.
- **CI gate**: `--fail-on <severity>` (or `analysis.failOn` in config) exits non-zero when a finding
  meets/exceeds the threshold.
- **Config-driven**: a top-level `analysis` block in `McpLense.Config.json`
  (`analysis.rules.<id>.enabled` / `.severity`, `analysis.failOn`) configures rules + the gate, so
  policy lives in config rather than CLI flags. Wired through `ScanConfig` + the loader + the schema.
- Docs: `docs/analysis-rules.md` (rule + config reference), README "Findings" section, skill
  COMMANDS.md, and a pointer atop `security-classification-recipes.md`.

Build + test state: Release `-warnaserror` clean. 777 unit + 70 integration + 36 E2E + 6 gated.

Remaining phases: 2 SARIF + rug-pull/baseline-approve; 3 learning (explain + markdown/HTML +
sample calls); 4 debugging (trace + doctor + stdio stderr + watch); 5 Section-2 checks + serve.

## Delivered in 0.12.0 - internal refactors + latent bug fixes

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

- **A1 / A3 / A4 refactors: DONE.** Delivered in the T4 refactor pass - see "Delivered
  in 0.12.0 - internal refactors + latent bug fixes" at the top of this file. (A1
  McpExecutor dictionary dispatch, A3 AuthScanner split, A4 CommandLine option registry.)

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
