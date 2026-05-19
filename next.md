# Next steps

Living roadmap after 0.6.0-preview.1.

## Delivered in 0.6.0-preview.1

- Per-target headers (`targets[]`) and pattern overlays (`targetPatterns[]`) in
  `McpLense.Config.json`. CLI gains `@<name>` positional syntax for named targets
  ("the dispatcher resolves the URL"). Per-key last-write-wins merge with three layers:
  pattern -> target -> CLI flag.
- Probe coverage gap closed: with `scope: "All"` (the default), per-target headers
  ride along with same-origin probes (TransportProbe, CorsPreflightCheck,
  AuthenticatedHeadersCheck, DcrEndpointCheck, AuthProbe + RFC 9728 metadata fetch).
  Cross-origin fetches NEVER get MCP-server headers. `scope: "Session"` reverts to
  the previous session-only behaviour per target.
- Per-target `disabledChecks`, `profile`, `transport`, `timeoutSeconds` wiring.
- Stderr `matched: patterns=N target=NAME -> K headers, scope=...` line under
  non-quiet so users can see what overlay applied per server.
- New URL-glob matcher (`UrlGlob`): single `*` = one host label OR one path segment,
  `**` = any sequence including `/`, host case-insensitive, path case-sensitive.
- New test MCP mode `headers` that records inbound HTTP requests via a `/capture`
  endpoint; new integration tests assert per-target headers reach the right
  surface (session vs CORS preflight) under the two scope modes.
- Docs + sample: `docs/scan-checks.md#per-target-configuration`, README "Per-target
  headers (config file)" section, `samples/targets.json` worked example.

Build + test state: Release `-warnaserror` clean. 535 unit + 55 integration + 35 E2E
+ 3 skipped (gated remote smokes).

## Delivered in 0.5.0-preview.1

- 1.1 In-repo test MCP servers: new `tests/McpLense.TestMcps/` project hosting
  Bare / Rich / Sampling / Leaky modes selected via `--mode`. Wired into integration
  tests (`TestMcpsScanTests`, 4 tests).
- 1.2 Remote-target fixture file: `tests/McpLense.E2ETests/remote-targets.json` driving
  `ConfigurableRemoteSmokeTests` (theory + MemberData), gated on `MCPLENSE_E2E_REMOTE=1`.
  Added `SkipUnlessEnvTheoryAttribute` for theory-shaped env-gated tests.
- 1.5 Per-check structured text renderers replace the JSON-payload fallback in
  `mcplense scan --format text`. Every built-in check has a focused renderer;
  extension checks fall back to JSON.
- 3.1 Reduced reflection: direct property access on `ProtocolResource`, `ProtocolPrompt`,
  `ProtocolTool`, `ServerCapabilities`, `Implementation` where the SDK shape is stable.
  Reflection retained only for SDK-experimental / SDK-evolving members (e.g. McpException
  ErrorCode, Implementation.Meta).
- 3.3 Cancellation hygiene: linked-token timeouts thrown from inside a check now surface
  as `CheckOutcome.Error = "Timed out."` rather than escape the pipeline. Regression test
  added.
- 3.6 Partial: introduced `ICommandHandler` abstraction. Full migration of every command
  body to handler classes deferred (see below).
- 4.4 `pack.ps1` packs BOTH `McpLense` (library) and `McpLense.Cli` (tool) by default;
  flags for library-only / CLI-only and a single-call dual-push pipeline.
- 4.5 Documentation: README scan + extensibility section; `docs/scan-checks.md` reference
  for every built-in check; `docs/security-classification-recipes.md` with jq recipes for
  downstream policy / risk classification.

## Carried forward

- 3.6 Full McpExecutor migration: the dispatch switch still selects per-command static
  methods. `ICommandHandler` interface is in place; concrete handler classes per command
  (Inspect/Tools/Resources/Call/Read/Prompt) + dictionary-driven dispatch is the remaining
  work. Significant scope; pure refactor (no behavioural change).
- 3.2-extra Other probes still own their own HttpClient. Roll TransportProbe /
  AuthorizationServerProbe / AuthenticatedHeadersCheck onto the shared `mcplense-probe`
  factory. `CorsPreflightCheck` already uses the factory; `DcrEndpointCheck` still owns
  its own client.

## Section 2 (new checks)

2.1 behavior.callMalformed.
2.2 wellKnownProbes (allowlist-only).
2.3 tlsDeep (cipher enumeration).
2.4 DNS / ASN enrichment.
2.5 Token-claims inspection (opt-in --inspect-tokens).
2.6 Stable resource-id resolution.

## Long-shot ideas

Plugin loading via AssemblyLoadContext. Watch mode. Cross-server correlation in diff
engine. HTML rendering. Web/TUI dashboard. Policy-engine integration.
