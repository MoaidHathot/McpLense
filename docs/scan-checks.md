# `mcplense scan` checks reference

Every built-in check that ships with McpLense, the data shape it emits under
`checks.<id>`, every config knob it reads, and the default enable-state.

The full report shape is:

```json
{
  "generatedAt": "...",
  "schemaVersion": "1",
  "servers": [
    {
      "name": "...", "transport": "http", "target": "https://...",
      "checks": { "<id>": <data> },
      "timings": { "<id>": <ms> }
    }
  ]
}
```

Every check produces a `<data>` JSON object documented below. Checks default-enabled run
out of the box; checks default-disabled require either CLI `--enable <id>` or the
`scan.checks.<id>.enabled: true` config entry to fire.

## auth

**Default:** on. **Depends on:** _none_.

RFC 9728 classification + profile attempts.

| Field | Type | Description |
|---|---|---|
| `classification` | string | One of `anonymous`, `oauth-rfc9728`, `oauth-bearer-unannounced`, `auth-required-unspecified`, `unknown`, `stdio`. Stable wire identifiers. |
| `summary` | string | Free-form one-liner. |
| `details.statusCode` | int? | HTTP status code from the unauthenticated probe. |
| `details.wwwAuthenticate` | string? | Verbatim WWW-Authenticate header. |
| `details.resourceMetadataUrl` | string? | RFC 9728 metadata URL when discovered. |
| `details.scopes` | string[]? | scopes_supported from the metadata document. |
| `details.authorizationServers` | string[]? | authorization_servers from metadata. |
| `details.anonymousHandshakeSucceeded` | bool? | True when the unauth MCP `initialize` worked. |
| `profileAttempts[]` | array | Per-profile success/failure with verbatim error strings. |

## transport

**Default:** on. **Depends on:** _none_.

Unauthenticated GET probe: status, leaf cert, security-relevant headers, mixed-content flag.

| Field | Type | Description |
|---|---|---|
| `mixedContent` | bool | True when the target URL is `http://`. |
| `statusCode` | int? | Status returned by the unauth GET. |
| `tls.subject` / `issuer` / `notBefore` / `notAfter` / `daysUntilExpiry` / `protocolVersion` / `signatureAlgorithm` / `subjectAlternativeNames[]` | various | Leaf cert details. Null when target is HTTP. |
| `responseHeaders.server` / `xPoweredBy` / `strictTransportSecurity` / `contentSecurityPolicy` / `xFrameOptions` / `xContentTypeOptions` / `referrerPolicy` / `accessControlAllowOrigin` / `cacheControl` / `other{}` | various | Verbatim headers captured. `other` holds every header not separately named. |

## tlsChain

**Default:** on. **Depends on:** `transport`.

Full TLS chain captured via a dedicated `SslStream` handshake.

| Field | Type | Description |
|---|---|---|
| `captured` | bool | True when the chain was inspectable. |
| `chainValid` | bool? | OS-level chain validation result. |
| `intermediates[]` | array | One entry per intermediate cert with subject / issuer / SANs / validity / signatureAlgorithm. |
| `chainPolicyErrors[]` | string[] | Verbatim policy-error strings when invalid. |

## authenticatedHeaders

**Default:** on. **Depends on:** `auth`.

Same shape as `transport.responseHeaders` but captured AFTER authentication succeeds.
Useful for servers that emit different headers (HSTS, CORS) on authenticated requests.

## corsPreflight

**Default:** on. **Depends on:** _none_.

CORS preflight via one `OPTIONS` request with `Origin: https://mcplense.invalid`.

| Field | Type | Description |
|---|---|---|
| `statusCode` | int | Preflight response status. |
| `accessControlAllow*` / `accessControlMaxAge` / `accessControlExposeHeaders` / `allow` / `vary` | string? | Verbatim header values. |

## authorizationServers

**Default:** off. **Depends on:** `auth`.

Per-AS RFC 8414 / OIDC discovery (only when classification is `oauth-rfc9728`).
Opt-in via `--check-authorization-servers` or `scan.checks.authorizationServers.enabled: true`.

| Field | Type | Description |
|---|---|---|
| `servers[].issuer` | string | Each `authorizationServers` URL from the resource metadata. |
| `servers[].tokenEndpoint` / `authorizationEndpoint` / `registrationEndpoint` / `userinfoEndpoint` / ... | string? | Verbatim endpoint URIs. |
| `servers[].grantTypesSupported` / `scopesSupported` / `tokenEndpointAuthMethodsSupported` / ... | string[] | Verbatim arrays. |
| `dcrFromResourceMetadata.endpoint` | string? | First AS that advertised a registration endpoint. |

## dcrEndpoint

**Default:** off. **Depends on:** `authorizationServers`.

DCR (RFC 7591) endpoint surface check: one OPTIONS + one empty POST. Records the status
+ body excerpts WITHOUT actually registering.

## serverInfo

**Default:** on. **Depends on:** `auth`.

`Implementation` block from `initialize`: name / title / version / description / websiteUrl /
icons / `_meta`.

## protocol

**Default:** on. **Depends on:** `auth`.

Negotiated protocol version, server capabilities block (tools / prompts / resources /
logging / completions / tasks / experimental / extensions), session id, instructions text
(verbatim), `_meta`.

## tools

**Default:** on. **Depends on:** `auth`.

Full tool listing. Each tool gets:

| Field | Type | Description |
|---|---|---|
| `name` / `title` / `description` | string? | Verbatim from `tools/list`. |
| `inputSchema` / `outputSchema` | JSON | Verbatim JSON Schema. |
| `annotations` | object? | `readOnlyHint`, `destructiveHint`, `idempotentHint`, `openWorldHint`, `title`. Each is null when unset. |
| `missingAnnotations[]` | string[] | Names of hints the server did NOT declare. |
| `schemaFingerprint` | object | `parameterCount`, `requiredCount`, `maxNestingDepth`, `hasAdditionalProperties`, `usesOneOf` / `usesAnyOf` / `usesAllOf`, `usesRefOrDefs`, `parameterTypeHistogram{}`, `parameterFormats[]`, `parameterNames[]`, `schemaBytes`, `hasOutputSchema`. |
| `execution` | JSON? | Verbatim `Execution` field (MCP spec extension). |
| `icons` / `meta` | JSON? | Verbatim. |

## prompts

**Default:** on. **Depends on:** `auth`.

Each prompt: name / title / description / arguments / icons / `_meta`.

## resources

**Default:** on. **Depends on:** `auth`.

Each resource: name / title / uri / uriScheme / mimeType / size / description / annotations /
icons / `_meta`. Plus a top-level `uriSchemeHistogram` counting resources per scheme.

## stdio

**Default:** on. **Depends on:** _none_.

For stdio targets only: resolved command line, args, working directory, environment.

## behavior.callNonExistentTool

**Default:** on. **Depends on:** `auth`.

Calls a deliberately non-existent tool name. Three structurally-distinct outcomes:

| `outcome` | Other fields |
|---|---|
| `tool-result-returned` | `toolResultIsError`, `toolResultJson` (verbatim envelope) |
| `jsonrpc-error` | `jsonRpcErrorCode`, `jsonRpcErrorMessage`, `jsonRpcErrorData` |
| `transport-error` | `transportError` |

## behavior.serverInitiated

**Default:** off. **Depends on:** `tools`, `prompts`, `resources`.

Opens a dedicated MCP session with handlers wired for sampling / elicitation / roots and
notification listeners. Holds the session open for the configured duration and captures
every inbound call.

| Config knob | Default |
|---|---|
| `observationDurationSeconds` | `2` |
| `advertiseCapabilities` | `["sampling", "elicitation", "roots", "listChanged"]` |

Output:

| Field | Description |
|---|---|
| `observationDurationMs` | Actual wall-clock duration. |
| `advertisedCapabilities[]` | What we advertised. |
| `inboundRequests[]` | Verbatim inbound messages (method + params + receivedAt). |
| `inboundCountsByMethod{}` | Per-method tally. |
| `refusalPolicy` | Currently always `"silent"`. |

## metrics

**Default:** on. **Depends on:** `tools`, `prompts`, `resources`, `protocol`.

Counts only - no judgements. Applied to a configurable set of fields.

| Config knob | Default |
|---|---|
| `urlExtractionFields[]` | `["serverInstructions", "toolDescription", "promptDescription"]` |

Output per field:

| Field | Type |
|---|---|
| `path` | "serverInstructions" / "tool:<name>:description" / "prompt:<name>:description" |
| `charLength` / `lineCount` / `urlCount` | int |
| `urls[]` | string[] (verbatim URLs found) |
| `markdownLinkCount` / `markdownImageCount` / `codeBlockFenceCount` | int |
| `nonAsciiCharCount` / `controlCharCount` / `tabCount` | int |

## hashing

**Default:** on. **Depends on:** `auth`, `tools`, `prompts`, `resources`, `protocol`, `serverInfo`.

Per-item content hashes + a top-level server fingerprint. Powers the `mcplense diff`
engine.

| Field | Type |
|---|---|
| `algorithm` | `"sha256"` |
| `serverFingerprint` | SHA-256 over the canonical-JSON of all stable check outputs |
| `toolHashes{}` / `promptHashes{}` / `resourceHashes{}` | per-item hashes keyed by name / uri |

---

## Configuration

Per-check config lives under `scan.checks.<id>` in `McpLense.Config.json`:

```jsonc
{
  "authProfiles": [],
  "scan": {
    "checks": {
      "authorizationServers": { "enabled": true },
      "behavior.serverInitiated": {
        "enabled": true,
        "observationDurationSeconds": 5,
        "advertiseCapabilities": ["sampling", "elicitation"]
      },
      "metrics": {
        "urlExtractionFields": ["serverInstructions", "toolDescription"]
      }
    },
    "output": {
      "baselineDir": "./baselines"
    }
  }
}
```

Precedence (low -> high): check default -> `scan.checks.<id>.enabled` -> CLI
`--enable` / `--disable` flags.

## CLI cheatsheet

```
mcplense scan <url>                                  # full audit
mcplense scan <url> --classify-only                  # skip profile attempts + enumeration
mcplense scan <url> --check-authorization-servers    # opt in to RFC 8414 fetch
mcplense scan <url> --enable behavior.serverInitiated
mcplense scan <url> --baseline ./baselines/          # write report under <host>/<ts>.json
mcplense scan <url> --diff ./baselines/x/old.json    # diff against baseline
mcplense scan <url> --parallel-servers 8 --quiet     # fleet scan, no progress chatter
mcplense observe <url> --timeout 30                  # auth + behavior.serverInitiated only
mcplense fetch-resource <uri> <url>                  # read one resource verbatim
mcplense diff <baseline-before> <baseline-after>     # pure file-to-file diff
```
