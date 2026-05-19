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

## Per-target configuration

Two top-level config blocks let you bind headers and other knobs to specific MCP servers
without re-typing them on every CLI invocation. Both live in `McpLense.Config.json`
alongside `authProfiles` and the `scan` block:

```jsonc
{
  "targetPatterns": [
    {
      "match":   "https://*.ec.com/**",
      "headers": { "x-mcp-ec-organization": "default-org" },
      "scope":   "All"
    }
  ],
  "targets": [
    {
      "name":   "ec-foo",
      "url":    "https://example.ec.com/foo/mcp",
      "headers": {
        "x-mcp-ec-organization": "myorg",
        "x-mcp-ec-project":      "myproj",
        "x-mcp-ec-repository":   "${MCPLENSE_EC_REPO:-default}"
      },
      "scope":   "All",
      "profile": "agent365",
      "transport": "streamable-http",
      "timeoutSeconds": 90,
      "disabledChecks": ["corsPreflight"]
    }
  ]
}
```

A worked example sits at [`samples/targets.json`](../samples/targets.json).

### `targets[]`

| Field | Type | Description |
|---|---|---|
| `name` | string | Short identifier the CLI can reference positionally as `@<name>`. Optional. Case-insensitive. Duplicates across config files raise an error at load time. |
| `url` | string | Exact MCP URL this entry binds to. Required. Matched case-insensitively on scheme + host, case-sensitively on path, trailing slash ignored. |
| `headers{}` | string -> string | HTTP headers to merge into outbound requests. Values run through the standard env-expander. |
| `scope` | enum | `"All"` (default) or `"Session"`. See **Scope** below. |
| `profile` | string | Auth profile name to bind. Override per scan with CLI `--profile`. |
| `transport` | string | `auto` / `streamable-http` / `sse`. Overrides the default auto-detect. |
| `timeoutSeconds` | number | Per-server handshake timeout. Overrides CLI `--timeout` for this server. |
| `disabledChecks[]` | string[] | Check ids to skip for this target. Unioned with CLI `--disable`. |

### `targetPatterns[]`

| Field | Type | Description |
|---|---|---|
| `match` | string | URL-level glob. See **Glob syntax** below. |
| `headers{}` / `scope` / `profile` / `transport` / `timeoutSeconds` / `disabledChecks[]` | various | Same shape as `targets[]`. |

Pattern entries are the **least-specific** layer of the resolver: a named `targets[]`
entry overrides every matching pattern, and CLI flags override both.

### Resolution & precedence

For each scanned URL the resolver merges (in order, last-write-wins per header key):

1. **`targetPatterns[]`** in declaration order. Multiple patterns may match the same URL.
2. **`targets[]`** — the entry whose `url` matches the scanned URL, OR the entry whose
   `name` matches a `@name` positional. Auto-resolution by URL fires even when no
   `@name` was supplied.
3. **CLI flags** — `--header`, `--profile`, `--transport`, `--timeout`, `--disable`.

The overlay applies uniformly across every command that opens an MCP connection:
`scan`, `inspect`, `tools`, `resources`, `prompts`, `call`, `read`, `prompt`,
`fetch-resource`, `auth-scan`, `observe`. The same code path that drives `scan` also
drives the other commands, so per-target headers and disabled-checks behave identically
regardless of which command the user invoked.

Under `--quiet` the scan stays silent; otherwise one stderr line per server reports the
matching layer:

```
matched: patterns=2 target=ec-foo -> 3 headers, scope=all
```

Add `--verbose` to also see which header NAMES the overlay produced (values are never
echoed - they may carry secrets) and which patterns fired:

```
matched: patterns=1 target=- -> 3 headers, scope=all
matched headers for https://mcp.bluebird-ai.net/: x-mcp-ec-organization, x-mcp-ec-project, x-mcp-ec-repository
matched pattern(s): https://**bluebird**/**
```

### Scope: `All` vs `Session`

| Scope | MCP session (initialize + JSON-RPC) | Probes (transport, CORS, authenticated-headers, DCR) |
|---|---|---|
| `All` (default) | Headers sent | Headers sent (same-origin only) |
| `Session` | Headers sent | Headers stripped |

Use `Session` when the unauthenticated probe must stay unauthenticated — for example to
inspect a server's bare `GET /mcp` challenge response while the MCP session still
authenticates normally. Cross-origin probes (e.g. the authorization-server metadata
fetch, which usually lives on `login.microsoftonline.com` or similar) **never** receive
MCP-server headers, regardless of scope. This is enforced by the same-origin guard in
each probe and is non-configurable.

### Glob syntax (`targetPatterns[].match`)

URL-level globs anchored at both ends; the candidate URL's query string and fragment
are stripped before matching.

| Token | Host part | Path part |
|---|---|---|
| `*` | Single host label (no `/`, no `.`). E.g. `https://*.example.com/x` matches `https://api.example.com/x` but NOT `https://api.staging.example.com/x`. | Single path segment (no `/`). E.g. `/*` matches `/mcp` but not `/a/b`. |
| `**` | Any sequence including `/` and `.`. | Any sequence including `/`. E.g. `/**` matches `/a/b/c`. |
| `?` | Single character (no `/`, no `.`). | Single character (no `/`). |
| literal | Case-insensitive. Default ports (`:443` for `https`, `:80` for `http`) are normalised away. | Case-sensitive (browser convention). |

The scheme separator `://` is required. Patterns without a scheme are rejected at load
time with a stderr warning; the pattern is then skipped (the scan otherwise continues).

### Headers on probes — the "gated server" workflow

Some MCP servers reject **every** request that arrives without a custom header set
(e.g. `x-mcp-ec-organization`). Before per-target headers, the scanner's probes (the
unauthenticated GET, CORS preflight, authenticated-headers re-probe, RFC 9728 metadata
fetch) all went out bare and the scan returned mostly opaque errors against those
servers.

With `scope: "All"` (the default for any `targets[]` / `targetPatterns[]` entry):

- The **transport probe** (`transport` check) sends GET to the MCP URL with the headers.
- The **CORS preflight** (`corsPreflight` check) sends OPTIONS with the headers.
- The **authenticated headers** (`authenticatedHeaders` check) re-probes with the headers.
- The **DCR endpoint** (`dcrEndpoint` check) sends OPTIONS+POST **only when** the DCR
  endpoint is same-origin with the MCP URL.
- The **RFC 9728 metadata** fetch (inside the `auth` check) sends GET **only when** the
  metadata URL is same-origin.

Cross-origin fetches (authorization-server metadata on a different host, DCR endpoint
on the AS host, etc.) always go out bare so per-MCP headers never leak to a different
origin.

If you specifically want to test the server's behaviour **without** custom headers on
the probes (e.g. to validate the bare RFC 9728 challenge), set `scope: "Session"` on
the target.

## CLI cheatsheet

```
mcplense scan <url>                                  # full audit
mcplense scan @ec-foo                                # scan a named target from config
mcplense scan <url> --classify-only                  # skip profile attempts + enumeration
mcplense scan <url> --check-authorization-servers    # opt in to RFC 8414 fetch
mcplense scan <url> --enable behavior.serverInitiated
mcplense scan <url> --baseline ./baselines/          # write report under <host>/<ts>.json
mcplense scan <url> --diff ./baselines/x/old.json    # diff against baseline
mcplense scan <url> --parallel-servers 8 --quiet     # fleet scan, no progress chatter
mcplense scan <url> --header x-mcp-ec-organization=myorg  # ad-hoc per-server header
mcplense observe <url> --timeout 30                  # auth + behavior.serverInitiated only
mcplense fetch-resource <uri> <url>                  # read one resource verbatim
mcplense diff <baseline-before> <baseline-after>     # pure file-to-file diff
```
