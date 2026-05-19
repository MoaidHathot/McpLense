# Next steps

Living roadmap after 0.4.0.

## Delivered in 0.4.0

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
