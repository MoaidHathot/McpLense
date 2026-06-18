# `mcplense analyze` — built-in findings rules

`mcplense scan` is deliberately **fact-only**: it records raw observations and never labels them.
`mcplense analyze` is the opt-in layer on top — it runs the same scan, then applies a built-in rule
pack to the fact report and emits **findings** with severities. Facts and findings never share a
document: `analyze` emits a `FindingsReport`; `scan --findings` emits `{ "scan": ..., "findings": ... }`
with the two as separate top-level keys; plain `scan` is unchanged.

```bash
mcplense analyze https://server.example/mcp
mcplense analyze https://server.example/mcp --fail-on high   # CI gate: non-zero exit
mcplense scan https://server.example/mcp --findings --format json
```

## Severities

`info` < `low` < `medium` < `high` < `critical`. `--fail-on <severity>` (or `analysis.failOn` in
config) makes the process exit non-zero when any finding meets or exceeds the threshold — turning
`analyze` into a CI security gate.

## Built-in rules

Each finding records the rule id, severity, a `title`, the `evidencePath` (a JSON path into the scan
report it was derived from), the quoted `evidence`, and a `remediation`.

| Rule id | Default | Flags | Derived from |
|---|---|---|---|
| `prompt-injection` | high / medium | Hidden bidi/zero-width/control characters, or instruction-hijacking phrases ("ignore previous instructions", ...) in model-visible text | `tools`/`prompts` descriptions, `serverInfo.description`, `protocol.instructions` |
| `anonymous-destructive` | high | An anonymous (no-auth) server exposes a tool the server marks destructive or open-world | `auth.classification` + `tools` |
| `weak-cors` | high | `Access-Control-Allow-Origin: *` together with `Access-Control-Allow-Credentials: true` | `corsPreflight` |
| `mixed-content` | high | Server reachable over plain HTTP (Authorization header sent in the clear) | `transport.mixedContent` |
| `tls-chain-invalid` | high | OS-level TLS chain validation failed | `tlsChain.chainValid` |
| `tls-expiry` | critical / medium | Leaf certificate expired (critical) or expiring within 30 days (medium) | `transport.tls.daysUntilExpiry` |
| `open-shape-input` | medium | Tool input schema does not restrict `additionalProperties` (wide LLM attack surface) | `tools.items[].schemaFingerprint` |
| `error-info-leak` | medium | Error response to an unknown tool leaks stack traces / file paths / build ids / internal hostnames | `behavior.callNonExistentTool` |
| `malformed-handling` | medium | Server returned a 5xx to deliberately malformed JSON-RPC (doesn't reject bad input gracefully) | `behavior.callMalformed` (opt-in) |
| `description-url` | low | URL(s) embedded in a tool/prompt description (host-rendered links/images are an exfiltration vector) | `metrics.fields[]` |
| `missing-destructive-hint` | low | Tool does not declare a `destructiveHint` annotation | `tools.items[].missingAnnotations` |
| `unannounced-bearer` | low | Server demands Bearer auth but advertises no RFC 9728 metadata | `auth.classification` |
| `rug-pull` | high / medium / info | A tool/prompt/resource **changed** (high), was **added** (medium), or was **removed** (info) since an approved baseline | `hashing` (vs `--since` snapshot) |

Some rules depend on a check that is not default-on (`error-info-leak` needs
`behavior.callNonExistentTool`, which is on by default; `tls-chain-invalid` needs `tlsChain`). If a
required check did not run the rule simply yields nothing.

## Rug-pull detection (`--approve` / `--since`)

A "rug pull" is when a server changes a tool *after* you trusted it. McpLense detects this with the
fact-only `hashing` check + an approved snapshot:

```bash
mcplense analyze https://server/mcp --approve approved.json     # snapshot: "I trust it as it is now"
# ... later, in CI ...
mcplense analyze https://server/mcp --since approved.json --fail-on high
```

`--approve` writes the current per-item hashes to a file. `--since` re-scans and emits a `rug-pull`
finding for every tool/prompt/resource whose hash changed (high), was added (medium), or was removed
(info) since the snapshot. Combined with `--fail-on high` this fails CI the moment a trusted tool's
definition changes.

## SARIF output (CI / code scanning)

`--format sarif` emits SARIF 2.1.0 so findings flow into GitHub code scanning and other SARIF-aware
tools. Severity maps to the SARIF level (critical/high -> `error`, medium -> `warning`, low/info ->
`note`); each result carries the target URL (artifact location) and the evidence path (logical
location).

```yaml
# GitHub Actions
- run: mcplense analyze "$MCP_URL" --format sarif > mcplense.sarif
- uses: github/codeql-action/upload-sarif@v3
  with: { sarif_file: mcplense.sarif }
```

## Configuration (`analysis` block)

All of the above is config-driven from the top-level `analysis` block of `McpLense.Config.json`
(peer of `scan` and `authProfiles`), so a fleet policy lives in one file instead of a wall of flags:

```jsonc
{
  "analysis": {
    "failOn": "high",
    "rules": {
      "description-url":          { "enabled": false },   // turn a rule off
      "missing-destructive-hint": { "severity": "medium" } // re-rate a rule
    }
  }
}
```

Precedence: a rule's built-in default → `analysis.rules.<id>.enabled` / `.severity` → (for the gate)
`--fail-on` overrides `analysis.failOn`. The legacy nested location `scan.analysis` is also accepted.

## Rolling your own

The findings layer is a reference consumer of the fact report — you can still classify the raw
`mcplense scan --format json` output yourself; see
[security-classification-recipes.md](security-classification-recipes.md) for jq recipes, several of
which are the exact logic the built-in rules now codify.
