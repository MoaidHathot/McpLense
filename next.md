# Next steps

Living roadmap after 0.5.0-preview.1.

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

Build + test state: Release `-warnaserror` clean. 494 unit + 52 integration
(+4 new TestMcps) + 35 E2E + 3 skipped (gated remote smokes).

## Carried forward

- 3.6 Full McpExecutor migration: the dispatch switch still selects per-command static
  methods. `ICommandHandler` interface is in place; concrete handler classes per command
  (Inspect/Tools/Resources/Call/Read/Prompt) + dictionary-driven dispatch is the remaining
  work. Significant scope; pure refactor (no behavioural change).
- 3.2-extra Other probes still own their own HttpClient. Roll TransportProbe /
  AuthorizationServerProbe / DcrEndpointCheck / AuthenticatedHeadersCheck onto the
  shared `mcplense-probe` factory.

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
