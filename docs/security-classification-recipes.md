# Security-classification recipes

`mcplense scan` is deliberately fact-only: every check emits raw observations and never
labels findings. Downstream tools (security policy engines, dashboards, CI gates)
classify on top of the data. This document collects practical recipes for that
downstream step. None of them require new mcplense features - they pattern-match against
the JSON the existing checks produce.

All examples use `jq` against `mcplense scan <url> --format json`.

## "Flag servers whose tools accept open-shape input"

A tool whose JSON Schema doesn't lock down `additionalProperties` accepts arbitrary
fields. That's a wide LLM attack surface.

```bash
mcplense scan https://server/mcp --format json | jq -r '
  .servers[] | .target as $t |
  .checks.tools.items[]
  | select(.schemaFingerprint.hasAdditionalProperties == true)
  | "\($t) tool=\(.name) accepts additionalProperties"
'
```

## "Flag tools with no declared destructive hint"

Tools without `destructiveHint` are unclassified by the server. Policy may want to
require an annotation before allowing the host to auto-invoke them.

```bash
mcplense scan https://server/mcp --format json | jq -r '
  .servers[] | .target as $t |
  .checks.tools.items[]
  | select(.missingAnnotations | index("destructiveHint"))
  | "\($t) tool=\(.name) missing destructiveHint"
'
```

## "Spot information-leakage in error responses"

The `behavior.callNonExistentTool` outcome captures verbatim error responses.
Information-leakage policy might match against stack-trace markers / internal hostnames /
build identifiers in the response body.

```bash
mcplense scan https://server/mcp --format json | jq -r '
  .servers[] | .target as $t |
  .checks."behavior.callNonExistentTool" as $b |
  if ($b.toolResultJson // "") | test("(at [A-Z]:\\\\|build=|internal-)") then
    "\($t) leaked internals in error: \($b.toolResultJson | .[0:120])..."
  else empty end
'
```

## "Find URLs the host would render via tool descriptions"

The `metrics` check surfaces every URL in tool descriptions verbatim. Image-fetch
exfiltration tricks rely on these being rendered by the host UI.

```bash
mcplense scan https://server/mcp --format json | jq -r '
  .servers[] | .target as $t |
  .checks.metrics.fields[]
  | select(.path | startswith("tool:"))
  | select(.urlCount > 0)
  | "\($t) \(.path): \(.urls | join(", "))"
'
```

## "Hidden / RTL characters in instructions"

`metrics.controlCharCount` and `metrics.nonAsciiCharCount` are pure counts; a non-zero
control-char count in server instructions is worth a closer look.

```bash
mcplense scan https://server/mcp --format json | jq -r '
  .servers[] | .target as $t |
  .checks.metrics.fields[]
  | select(.path == "serverInstructions")
  | select(.controlCharCount > 0 or .nonAsciiCharCount > 50)
  | "\($t) instructions: ctrl=\(.controlCharCount) nonAscii=\(.nonAsciiCharCount)"
'
```

## "Servers that gained / lost tools since the baseline"

The diff engine already produces this. Combined with `--baseline` for daily snapshots
plus `--diff` against the previous run, you get rug-pull detection for free.

```bash
mcplense scan https://server/mcp --diff ./baselines/server/yesterday.json --format json |
  jq '.servers[].checks.tools.changed[] | { id, before: .before.contentHash, after: .after.contentHash }'
```

## "Servers reaching back into the host for sampling / elicitation / roots"

Opt-in observation captures inbound calls.

```bash
mcplense observe https://server/mcp --timeout 30 --format json | jq -r '
  .servers[] | .target as $t |
  .checks."behavior.serverInitiated".inboundCountsByMethod
  | to_entries[]
  | "\($t) \(.key): \(.value) call(s)"
'
```

## "TLS cert expiring within 30 days"

Trivial date arithmetic against `daysUntilExpiry`.

```bash
mcplense scan https://server/mcp --format json | jq -r '
  .servers[] | select(.checks.transport.tls.daysUntilExpiry < 30)
  | "\(.target): cert expires in \(.checks.transport.tls.daysUntilExpiry) days"
'
```

## "Weak CORS posture"

Either the basic `transport.responseHeaders` or the preflight check captures CORS-related
headers.

```bash
mcplense scan https://server/mcp --format json | jq -r '
  .servers[] | .target as $t |
  .checks.corsPreflight as $c |
  if ($c.accessControlAllowOrigin // "") == "*"
     and ($c.accessControlAllowCredentials // "") == "true"
  then "\($t) wildcard origin with credentials: high risk"
  else empty end
'
```

## "Fleet-wide drift report"

For nightly runs against many servers, write the baseline once and diff every subsequent
run.

```bash
# nightly
mkdir -p baselines
for url in $(cat targets.txt); do
  mcplense scan "$url" --baseline ./baselines/ --quiet --format json > /dev/null
done

# next day - compare and surface ANY drift
for url in $(cat targets.txt); do
  host=$(echo "$url" | awk -F/ '{print $3}')
  latest=$(ls -1t ./baselines/"$host"/*.json | head -1)
  mcplense scan "$url" --diff "$latest" --format json |
    jq -r --arg url "$url" '.servers[] | select(.status != "unchanged") | "\($url): \(.status)"'
done
```
