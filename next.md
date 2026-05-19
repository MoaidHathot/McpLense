# Next steps

Roadmap after 0.4.0-preview.1.

## Delivered in 0.4.0-preview.1

- 3.5 Legacy Auditor + McpSessionInspector deleted. Scan now flows through ScanPipeline.
- 1.3 Public extension API promoted. IScanCheck and friends are public.
- 3.4 Profiles + AuthOverrides are first-class on ScanContext.
- 1.4 Real inbound JSON-RPC interception in behavior.serverInitiated.
- 3.2 IHttpClientFactory pooling for probes.
- 4.1 --parallel-servers N flag.
- 4.2 / 4.3 --quiet and --verbose CLI flags + progress callback.

Build state: Release -warnaserror clean. 493 unit + 48 integration + 35 E2E + 2 skipped.

## Carried forward

1.1 Test MCPs project (BareMcp/RichMcp/SamplingMcp/LeakyMcp).
1.2 Remote E2E fixture file with MCPLENSE_E2E_REMOTE gate.
1.5 Text-format polish: per-check structured renderers.
3.1 Reduce reflection in checks where SDK property names are stable.
3.3 Cancellation hygiene audit (linked-token timeouts as CheckOutcome.Error).
3.6 Drop legacy McpExecutor switch.
4.4 NuGet packaging via pack.ps1.
4.5 Documentation: README scan/extensibility section, docs/scan-checks.md.

## Section 2 (new checks) - unchanged

2.1 behavior.callMalformed.
2.2 wellKnownProbes (allowlist-only).
2.3 tlsDeep (cipher enumeration).
2.4 DNS / ASN enrichment.
2.5 Token-claims inspection.
2.6 Stable resource-id resolution.

## Long-shot ideas - unchanged

Plugin loading via AssemblyLoadContext. Watch mode. Cross-server correlation in diff
engine. HTML rendering. Web/TUI dashboard. Policy-engine integration.
