using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using McpLense.Scanning;

namespace McpLense;

internal static class TextFormatter
{
    public static string Format(object payload, JsonSerializerOptions jsonOptions) => payload switch
    {
        InspectReport report => FormatInspect(report, jsonOptions),
        ToolListReport report => FormatServerItems("tools", report.Servers, FormatTool, jsonOptions),
        ResourceListReport report => FormatResourceList(report, jsonOptions),
        PromptListReport report => FormatServerItems("prompts", report.Servers, FormatPrompt, jsonOptions),
        ToolCallReport report => FormatToolCall(report, jsonOptions),
        ReadReport report => FormatRead(report, jsonOptions),
        PromptCallReport report => FormatPromptCall(report, jsonOptions),
        AuthSessionReport report => FormatAuthSession(report),
        AuthScanReport report => FormatAuthScan(report),
        ScanReport report => FormatScanReport(report, jsonOptions),
        ScanDiff.ScanDiffReport diff => FormatScanDiff(diff, jsonOptions),
        _ => JsonSerializer.Serialize(payload, jsonOptions)
    };

    /// <summary>
    /// Renders the new <see cref="ScanReport"/> from the IScanCheck pipeline. Each server
    /// gets a header followed by one named section per check. Known check ids get a
    /// structured renderer; unknown / extension check ids fall back to pretty-JSON so the
    /// payload is still readable.
    /// </summary>
    private static string FormatScanReport(ScanReport report, JsonSerializerOptions jsonOptions)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"scan: {report.Servers.Count} server(s)");

        for (var index = 0; index < report.Servers.Count; index++)
        {
            var entry = report.Servers[index];
            builder.AppendLine();
            AppendServerHeader(builder, entry.Name, entry.Transport, entry.Target);

            if (!string.IsNullOrEmpty(entry.Error))
            {
                AppendLine(builder, 1, $"error: {entry.Error}");
            }

            foreach (var (checkId, data) in entry.Checks)
            {
                AppendLine(builder, 1, $"{checkId}:");
                if (data is null)
                {
                    AppendLine(builder, 2, "(no data)");
                    continue;
                }

                if (data is JsonObject obj && TryRenderKnownCheck(builder, checkId, obj, jsonOptions))
                {
                    continue;
                }

                // Fallback for extension checks: pretty-print the JSON.
                foreach (var line in Indent(FormatJson(data, jsonOptions), 4))
                {
                    builder.AppendLine(line);
                }
            }

            if (entry.Timings.Count > 0)
            {
                AppendLine(builder, 1, "timings:");
                foreach (var (id, ms) in entry.Timings.OrderByDescending(kv => kv.Value))
                {
                    AppendLine(builder, 2, $"{id}: {ms:F1} ms");
                }
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Per-built-in-check structured rendering. Returns false when no renderer matches the
    /// check id; the caller then falls back to pretty-JSON. New built-in checks should add
    /// a renderer here; extension authors keep the JSON fallback automatically.
    /// </summary>
    private static bool TryRenderKnownCheck(StringBuilder builder, string checkId, JsonObject obj, JsonSerializerOptions jsonOptions)
    {
        switch (checkId)
        {
            case "auth": RenderAuthSection(builder, obj); return true;
            case "transport": RenderTransportSection(builder, obj, jsonOptions); return true;
            case "serverInfo": RenderServerInfoSection(builder, obj, jsonOptions); return true;
            case "protocol": RenderProtocolSection(builder, obj, jsonOptions); return true;
            case "tools": RenderToolsSection(builder, obj, jsonOptions); return true;
            case "prompts": RenderPromptsSection(builder, obj, jsonOptions); return true;
            case "resources": RenderResourcesSection(builder, obj, jsonOptions); return true;
            case "tlsChain": RenderTlsChainSection(builder, obj); return true;
            case "corsPreflight": RenderCorsSection(builder, obj); return true;
            case "authenticatedHeaders": RenderAuthHeadersSection(builder, obj, jsonOptions); return true;
            case "authorizationServers": RenderAuthorizationServersSection(builder, obj, jsonOptions); return true;
            case "dcrEndpoint": RenderDcrSection(builder, obj); return true;
            case "stdio": RenderStdioSection(builder, obj); return true;
            case "behavior.callNonExistentTool": RenderCallNonExistentToolSection(builder, obj, jsonOptions); return true;
            case "behavior.serverInitiated": RenderObservationSection(builder, obj, jsonOptions); return true;
            case "metrics": RenderMetricsSection(builder, obj); return true;
            case "hashing": RenderHashingSection(builder, obj); return true;
            default: return false;
        }
    }

    private static void RenderAuthSection(StringBuilder builder, JsonObject obj)
    {
        var classification = obj["classification"]?.GetValue<string>();
        if (classification is not null)
        {
            AppendLine(builder, 2, $"classification: {classification}");
        }
        if (obj["summary"]?.GetValue<string>() is { } summary)
        {
            AppendLine(builder, 2, $"summary: {summary}");
        }
        if (obj["details"] is JsonObject details)
        {
            if (details["statusCode"]?.GetValue<int>() is { } status)
            {
                AppendLine(builder, 2, $"probeStatus: {status}");
            }
            if (details["resourceMetadataUrl"]?.GetValue<string>() is { } prm)
            {
                AppendLine(builder, 2, $"resourceMetadataUrl: {prm}");
            }
            if (details["scopes"] is JsonArray scopes && scopes.Count > 0)
            {
                AppendLine(builder, 2, $"scopesSupported: {JoinStrings(scopes)}");
            }
            if (details["authorizationServers"] is JsonArray ass && ass.Count > 0)
            {
                AppendLine(builder, 2, $"authorizationServers: {JoinStrings(ass)}");
            }
        }
        if (obj["profileAttempts"] is JsonArray attempts && attempts.Count > 0)
        {
            AppendLine(builder, 2, $"profileAttempts: {attempts.Count}");
            foreach (var attempt in attempts.OfType<JsonObject>())
            {
                var name = attempt["profileName"]?.GetValue<string>() ?? "(unnamed)";
                var ok = attempt["success"]?.GetValue<bool>() == true;
                var kind = attempt["authKind"]?.GetValue<string>() ?? "?";
                AppendLine(builder, 3, $"- {name} [{kind}]: {(ok ? "ok" : "failed")}");
                if (!ok && attempt["error"]?.GetValue<string>() is { } err)
                {
                    AppendLine(builder, 4, $"error: {err}");
                }
            }
        }
    }

    private static void RenderTransportSection(StringBuilder builder, JsonObject obj, JsonSerializerOptions jsonOptions)
    {
        if (obj["mixedContent"]?.GetValue<bool>() is bool mixed)
        {
            AppendLine(builder, 2, $"mixedContent: {(mixed ? "true" : "false")}");
        }
        if (obj["statusCode"]?.GetValue<int>() is { } status)
        {
            AppendLine(builder, 2, $"statusCode: {status}");
        }
        if (obj["tls"] is JsonObject tls)
        {
            AppendLine(builder, 2, "tls:");
            CopyStringField(builder, 3, tls, "subject");
            CopyStringField(builder, 3, tls, "issuer");
            CopyStringField(builder, 3, tls, "notAfter");
            CopyIntField(builder, 3, tls, "daysUntilExpiry");
            CopyStringField(builder, 3, tls, "protocolVersion");
            CopyStringField(builder, 3, tls, "signatureAlgorithm");
            if (tls["subjectAlternativeNames"] is JsonArray sans && sans.Count > 0)
            {
                AppendLine(builder, 3, $"subjectAlternativeNames: {JoinStrings(sans)}");
            }
        }
        if (obj["responseHeaders"] is JsonObject headers)
        {
            AppendLine(builder, 2, "responseHeaders:");
            RenderHeaderMap(builder, 3, headers);
        }
    }

    private static void RenderServerInfoSection(StringBuilder builder, JsonObject obj, JsonSerializerOptions jsonOptions)
    {
        CopyStringField(builder, 2, obj, "name");
        CopyStringField(builder, 2, obj, "title");
        CopyStringField(builder, 2, obj, "version");
        CopyStringField(builder, 2, obj, "description");
        CopyStringField(builder, 2, obj, "websiteUrl");
        if (obj["icons"] is JsonArray icons && icons.Count > 0)
        {
            AppendLine(builder, 2, $"icons: {icons.Count}");
        }
    }

    private static void RenderProtocolSection(StringBuilder builder, JsonObject obj, JsonSerializerOptions jsonOptions)
    {
        CopyStringField(builder, 2, obj, "negotiatedProtocolVersion");
        CopyStringField(builder, 2, obj, "sessionId");
        CopyIntField(builder, 2, obj, "instructionsLength");
        if (obj["instructions"]?.GetValue<string>() is { } instructions && !string.IsNullOrEmpty(instructions))
        {
            AppendLine(builder, 2, "instructions:");
            foreach (var line in instructions.Split('\n'))
            {
                AppendLine(builder, 3, line.TrimEnd('\r'));
            }
        }
        if (obj["capabilities"] is JsonObject caps)
        {
            AppendLine(builder, 2, "capabilities:");
            foreach (var (capId, capValue) in caps)
            {
                AppendLine(builder, 3, capValue is null ? $"{capId}: not declared" : $"{capId}: declared");
            }
        }
    }

    private static void RenderToolsSection(StringBuilder builder, JsonObject obj, JsonSerializerOptions jsonOptions)
    {
        var fetched = obj["fetched"]?.GetValue<bool>() == true;
        AppendLine(builder, 2, $"fetched: {(fetched ? "true" : "false")}");
        CopyStringField(builder, 2, obj, "fetchedVia");
        if (!fetched)
        {
            CopyStringField(builder, 2, obj, "fetchError");
            return;
        }

        if (obj["items"] is JsonArray items)
        {
            AppendLine(builder, 2, $"count: {items.Count}");
            foreach (var item in items.OfType<JsonObject>())
            {
                var name = item["name"]?.GetValue<string>() ?? "(unnamed)";
                AppendLine(builder, 3, $"- name: {name}");
                CopyStringField(builder, 4, item, "title");
                if (item["description"]?.GetValue<string>() is { } desc && !string.IsNullOrEmpty(desc))
                {
                    AppendLine(builder, 4, $"description: {Truncate(desc, 120)}");
                }
                if (item["schemaFingerprint"] is JsonObject fp)
                {
                    var pc = fp["parameterCount"]?.GetValue<int>();
                    var rq = fp["requiredCount"]?.GetValue<int>();
                    var depth = fp["maxNestingDepth"]?.GetValue<int>();
                    AppendLine(builder, 4, $"schemaFingerprint: params={pc} required={rq} depth={depth}");
                }
                if (item["missingAnnotations"] is JsonArray missing && missing.Count > 0)
                {
                    AppendLine(builder, 4, $"missingAnnotations: {JoinStrings(missing)}");
                }
            }
        }
    }

    private static void RenderPromptsSection(StringBuilder builder, JsonObject obj, JsonSerializerOptions jsonOptions)
    {
        var fetched = obj["fetched"]?.GetValue<bool>() == true;
        AppendLine(builder, 2, $"fetched: {(fetched ? "true" : "false")}");
        if (!fetched)
        {
            CopyStringField(builder, 2, obj, "fetchError");
            return;
        }
        if (obj["items"] is JsonArray items)
        {
            AppendLine(builder, 2, $"count: {items.Count}");
            foreach (var item in items.OfType<JsonObject>())
            {
                var name = item["name"]?.GetValue<string>() ?? "(unnamed)";
                AppendLine(builder, 3, $"- name: {name}");
                if (item["description"]?.GetValue<string>() is { } desc && !string.IsNullOrEmpty(desc))
                {
                    AppendLine(builder, 4, $"description: {Truncate(desc, 120)}");
                }
            }
        }
    }

    private static void RenderResourcesSection(StringBuilder builder, JsonObject obj, JsonSerializerOptions jsonOptions)
    {
        var fetched = obj["fetched"]?.GetValue<bool>() == true;
        AppendLine(builder, 2, $"fetched: {(fetched ? "true" : "false")}");
        if (!fetched)
        {
            CopyStringField(builder, 2, obj, "fetchError");
            return;
        }
        if (obj["items"] is JsonArray items)
        {
            AppendLine(builder, 2, $"count: {items.Count}");
        }
        if (obj["uriSchemeHistogram"] is JsonObject hist && hist.Count > 0)
        {
            AppendLine(builder, 2, $"uriSchemeHistogram: {JoinKeyValues(hist)}");
        }
    }

    private static void RenderTlsChainSection(StringBuilder builder, JsonObject obj)
    {
        if (obj["captured"]?.GetValue<bool>() is bool captured)
        {
            AppendLine(builder, 2, $"captured: {(captured ? "true" : "false")}");
        }
        if (obj["chainValid"]?.GetValue<bool>() is bool valid)
        {
            AppendLine(builder, 2, $"chainValid: {(valid ? "true" : "false")}");
        }
        if (obj["intermediates"] is JsonArray ints)
        {
            AppendLine(builder, 2, $"intermediates: {ints.Count}");
            foreach (var inter in ints.OfType<JsonObject>())
            {
                AppendLine(builder, 3, $"- {inter["subject"]?.GetValue<string>() ?? "(no subject)"}");
            }
        }
        if (obj["chainPolicyErrors"] is JsonArray errs && errs.Count > 0)
        {
            AppendLine(builder, 2, $"chainPolicyErrors: {JoinStrings(errs)}");
        }
        CopyStringField(builder, 2, obj, "failureReason");
    }

    private static void RenderCorsSection(StringBuilder builder, JsonObject obj)
    {
        CopyIntField(builder, 2, obj, "statusCode");
        CopyStringField(builder, 2, obj, "accessControlAllowOrigin");
        CopyStringField(builder, 2, obj, "accessControlAllowMethods");
        CopyStringField(builder, 2, obj, "accessControlAllowHeaders");
        CopyStringField(builder, 2, obj, "accessControlAllowCredentials");
        CopyStringField(builder, 2, obj, "accessControlMaxAge");
        CopyStringField(builder, 2, obj, "allow");
        CopyStringField(builder, 2, obj, "vary");
    }

    private static void RenderAuthHeadersSection(StringBuilder builder, JsonObject obj, JsonSerializerOptions jsonOptions)
    {
        var fetched = obj["fetched"]?.GetValue<bool>() == true;
        AppendLine(builder, 2, $"fetched: {(fetched ? "true" : "false")}");
        CopyStringField(builder, 2, obj, "detail");
        if (obj["headers"] is JsonObject headers)
        {
            RenderHeaderMap(builder, 2, headers);
        }
    }

    private static void RenderAuthorizationServersSection(StringBuilder builder, JsonObject obj, JsonSerializerOptions jsonOptions)
    {
        if (obj["servers"] is JsonArray servers && servers.Count > 0)
        {
            AppendLine(builder, 2, $"servers: {servers.Count}");
            foreach (var entry in servers.OfType<JsonObject>())
            {
                AppendLine(builder, 3, $"- issuer: {entry["issuer"]?.GetValue<string>()}");
                AppendLine(builder, 4, $"fetched: {(entry["fetched"]?.GetValue<bool>() == true ? "true" : "false")}");
                CopyStringField(builder, 4, entry, "tokenEndpoint");
                CopyStringField(builder, 4, entry, "registrationEndpoint");
                if (entry["scopesSupported"] is JsonArray scopes && scopes.Count > 0)
                {
                    AppendLine(builder, 4, $"scopesSupported: {JoinStrings(scopes)}");
                }
                if (entry["grantTypesSupported"] is JsonArray grants && grants.Count > 0)
                {
                    AppendLine(builder, 4, $"grantTypesSupported: {JoinStrings(grants)}");
                }
            }
        }
        else
        {
            AppendLine(builder, 2, "servers: (not fetched - pass --check-authorization-servers)");
        }
        if (obj["dcrFromResourceMetadata"] is JsonObject dcr && dcr["endpoint"]?.GetValue<string>() is { } endpoint)
        {
            AppendLine(builder, 2, $"dcrFromResourceMetadata: {endpoint}");
        }
    }

    private static void RenderDcrSection(StringBuilder builder, JsonObject obj)
    {
        CopyStringField(builder, 2, obj, "endpoint");
        if (obj["options"] is JsonObject options && options["statusCode"]?.GetValue<int>() is { } optStatus)
        {
            AppendLine(builder, 2, $"OPTIONS: {optStatus}");
        }
        if (obj["post"] is JsonObject post && post["statusCode"]?.GetValue<int>() is { } postStatus)
        {
            AppendLine(builder, 2, $"POST: {postStatus}");
        }
    }

    private static void RenderStdioSection(StringBuilder builder, JsonObject obj)
    {
        CopyStringField(builder, 2, obj, "command");
        if (obj["arguments"] is JsonArray args && args.Count > 0)
        {
            AppendLine(builder, 2, $"arguments: {JoinStrings(args)}");
        }
        CopyStringField(builder, 2, obj, "workingDirectory");
        if (obj["environment"] is JsonObject env && env.Count > 0)
        {
            AppendLine(builder, 2, $"environment: {env.Count} variables");
        }
    }

    private static void RenderCallNonExistentToolSection(StringBuilder builder, JsonObject obj, JsonSerializerOptions jsonOptions)
    {
        CopyStringField(builder, 2, obj, "outcome");
        CopyStringField(builder, 2, obj, "toolNameUsed");
        CopyIntField(builder, 2, obj, "jsonRpcErrorCode");
        CopyStringField(builder, 2, obj, "jsonRpcErrorMessage");
        if (obj["toolResultIsError"]?.GetValue<bool>() is bool isErr)
        {
            AppendLine(builder, 2, $"toolResultIsError: {(isErr ? "true" : "false")}");
        }
        CopyStringField(builder, 2, obj, "transportError");
    }

    private static void RenderObservationSection(StringBuilder builder, JsonObject obj, JsonSerializerOptions jsonOptions)
    {
        if (obj["observationDurationMs"]?.GetValue<double>() is { } ms)
        {
            AppendLine(builder, 2, $"observationDurationMs: {ms:F1}");
        }
        if (obj["advertisedCapabilities"] is JsonArray adv)
        {
            AppendLine(builder, 2, $"advertisedCapabilities: {JoinStrings(adv)}");
        }
        if (obj["inboundCountsByMethod"] is JsonObject counts && counts.Count > 0)
        {
            AppendLine(builder, 2, $"inboundCountsByMethod: {JoinKeyValues(counts)}");
        }
        if (obj["inboundRequests"] is JsonArray reqs && reqs.Count > 0)
        {
            AppendLine(builder, 2, $"inboundRequests: {reqs.Count}");
        }
        CopyStringField(builder, 2, obj, "error");
    }

    private static void RenderMetricsSection(StringBuilder builder, JsonObject obj)
    {
        if (obj["fields"] is JsonArray fields)
        {
            AppendLine(builder, 2, $"fields: {fields.Count}");
            foreach (var field in fields.OfType<JsonObject>())
            {
                var path = field["path"]?.GetValue<string>() ?? "?";
                var chars = field["charLength"]?.GetValue<int>();
                var urls = field["urlCount"]?.GetValue<int>();
                var ctrl = field["controlCharCount"]?.GetValue<int>();
                AppendLine(builder, 3, $"- {path}: chars={chars} urls={urls} ctrl={ctrl}");
            }
        }
    }

    private static void RenderHashingSection(StringBuilder builder, JsonObject obj)
    {
        CopyStringField(builder, 2, obj, "algorithm");
        CopyStringField(builder, 2, obj, "serverFingerprint");
        if (obj["toolHashes"] is JsonObject th && th.Count > 0)
        {
            AppendLine(builder, 2, $"toolHashes: {th.Count}");
        }
        if (obj["promptHashes"] is JsonObject ph && ph.Count > 0)
        {
            AppendLine(builder, 2, $"promptHashes: {ph.Count}");
        }
        if (obj["resourceHashes"] is JsonObject rh && rh.Count > 0)
        {
            AppendLine(builder, 2, $"resourceHashes: {rh.Count}");
        }
    }

    private static void RenderHeaderMap(StringBuilder builder, int indent, JsonObject headers)
    {
        foreach (var (k, v) in headers)
        {
            if (string.Equals(k, "other", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (v is JsonValue val && val.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
            {
                AppendLine(builder, indent, $"{k}: {Truncate(s, 200)}");
            }
        }
    }

    private static void CopyStringField(StringBuilder builder, int indent, JsonObject obj, string key)
    {
        if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
        {
            AppendLine(builder, indent, $"{key}: {s}");
        }
    }

    private static void CopyIntField(StringBuilder builder, int indent, JsonObject obj, string key)
    {
        if (obj[key] is JsonValue v && v.TryGetValue<int>(out var i))
        {
            AppendLine(builder, indent, $"{key}: {i}");
        }
    }

    private static string JoinStrings(JsonArray array)
        => string.Join(", ", array.OfType<JsonValue>()
            .Select(v => v.TryGetValue<string>(out var s) ? s : null)
            .Where(s => !string.IsNullOrEmpty(s)));

    private static string JoinKeyValues(JsonObject obj)
        => string.Join(", ", obj.Select(kv => $"{kv.Key}={kv.Value}"));

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max) + "...";

    private static string FormatScanDiff(ScanDiff.ScanDiffReport diff, JsonSerializerOptions jsonOptions)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"diff: baseline={diff.BaselineGeneratedAt:O} current={diff.CurrentGeneratedAt:O}");
        builder.AppendLine($"changed servers: {diff.Servers.Count}");

        foreach (var server in diff.Servers)
        {
            builder.AppendLine();
            AppendServerHeader(builder, server.Name, "diff", server.Target);
            AppendLine(builder, 1, $"status: {server.Status}");
            if (!string.IsNullOrEmpty(server.ServerFingerprintBefore))
            {
                AppendLine(builder, 1, $"serverFingerprintBefore: {server.ServerFingerprintBefore}");
            }
            if (!string.IsNullOrEmpty(server.ServerFingerprintAfter))
            {
                AppendLine(builder, 1, $"serverFingerprintAfter: {server.ServerFingerprintAfter}");
            }

            foreach (var (checkId, data) in server.Checks)
            {
                AppendLine(builder, 1, $"{checkId}:");
                if (data is null)
                {
                    AppendLine(builder, 2, "(no data)");
                    continue;
                }

                foreach (var line in Indent(FormatJson(data, jsonOptions), 4))
                {
                    builder.AppendLine(line);
                }
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatNullableBool(bool? value)
        => value is null ? "null" : value.Value ? "true" : "false";

    /// <summary>
    /// Body of an auth-scan entry, shared between <c>auth-scan</c>'s top-level renderer and
    /// the auth section inside <c>audit</c>. The indent is parameterised because audit nests
    /// it under "auth:" while auth-scan emits it at the server header level.
    /// </summary>
    private static void AppendAuthScanBody(StringBuilder builder, ServerAuthScan entry, int indentBase)
    {
        AppendLine(builder, indentBase, $"classification: {entry.Classification}");
        AppendLine(builder, indentBase, $"summary: {entry.Summary}");

        if (!string.IsNullOrEmpty(entry.Error))
        {
            AppendLine(builder, indentBase, $"error: {entry.Error}");
        }

        AppendAuthScanDetails(builder, entry.Details, indentBase);

        if (entry.ProfileAttempts.Count == 0)
        {
            AppendLine(builder, indentBase, "profile attempts: (none)");
        }
        else
        {
            AppendLine(builder, indentBase, $"profile attempts: {entry.ProfileAttempts.Count}");
            foreach (var attempt in entry.ProfileAttempts)
            {
                AppendLine(builder, indentBase + 1, $"- profile: {attempt.ProfileName} [{attempt.AuthKind}]");
                AppendLine(builder, indentBase + 2, $"status: {(attempt.Success ? "ok" : "failed")}");

                if (attempt.Scopes is { Count: > 0 })
                {
                    AppendLine(builder, indentBase + 2, $"scopes: {string.Join(", ", attempt.Scopes)}");
                }

                if (!string.IsNullOrEmpty(attempt.Detail))
                {
                    AppendLine(builder, indentBase + 2, $"detail: {attempt.Detail}");
                }

                if (!string.IsNullOrEmpty(attempt.Error))
                {
                    AppendLine(builder, indentBase + 2, $"error: {attempt.Error}");
                }
            }
        }
    }

    private static string FormatAuthScan(AuthScanReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"scan: {report.Servers.Count} server(s)");

        for (var index = 0; index < report.Servers.Count; index++)
        {
            var entry = report.Servers[index];
            builder.AppendLine();
            AppendServerHeader(builder, entry.Name, entry.Transport, entry.Target);
            AppendAuthScanBody(builder, entry, indentBase: 1);
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendAuthScanDetails(StringBuilder builder, AuthScanDetails details, int indent)
    {
        if (details.StatusCode is { } status)
        {
            AppendLine(builder, indent, $"probe status: {status}");
        }

        if (!string.IsNullOrEmpty(details.WwwAuthenticate))
        {
            AppendLine(builder, indent, $"www-authenticate: {details.WwwAuthenticate}");
        }

        if (!string.IsNullOrEmpty(details.ResourceMetadataUrl))
        {
            AppendLine(builder, indent, $"resource_metadata: {details.ResourceMetadataUrl}");
        }

        if (!string.IsNullOrEmpty(details.Resource))
        {
            AppendLine(builder, indent, $"resource: {details.Resource}");
        }

        if (details.Scopes is { Count: > 0 })
        {
            AppendLine(builder, indent, $"scopes_supported: {string.Join(", ", details.Scopes)}");
        }

        if (details.AuthorizationServers is { Count: > 0 })
        {
            AppendLine(builder, indent, $"authorization_servers: {string.Join(", ", details.AuthorizationServers)}");
        }

        if (details.AnonymousHandshakeSucceeded is { } anonHandshake)
        {
            AppendLine(builder, indent, $"anonymous handshake: {(anonHandshake ? "ok" : "failed")}");
            if (!anonHandshake && !string.IsNullOrEmpty(details.AnonymousHandshakeError))
            {
                AppendLine(builder, indent + 1, $"error: {details.AnonymousHandshakeError}");
            }
        }

        if (!string.IsNullOrEmpty(details.ProbeError))
        {
            AppendLine(builder, indent, $"probe error: {details.ProbeError}");
        }
    }

    private static string FormatAuthSession(AuthSessionReport report)
    {
        var builder = new StringBuilder();
        var success = report.Servers.Count(server => server.Success);
        builder.AppendLine($"{report.Action}: {success}/{report.Servers.Count} succeeded");

        for (var index = 0; index < report.Servers.Count; index++)
        {
            var entry = report.Servers[index];
            AppendLine(builder, 1, $"- {entry.Name} [{entry.Target}]");
            AppendLine(builder, 2, $"status: {(entry.Success ? "ok" : "failed")}");

            if (!string.IsNullOrEmpty(entry.Detail))
            {
                AppendLine(builder, 2, $"detail: {entry.Detail}");
            }

            if (!string.IsNullOrEmpty(entry.Error))
            {
                AppendLine(builder, 2, $"error: {entry.Error}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatInspect(InspectReport report, JsonSerializerOptions jsonOptions)
    {
        var builder = new StringBuilder();

        for (var index = 0; index < report.Servers.Count; index++)
        {
            var server = report.Servers[index];
            AppendServerHeader(builder, server.Name, server.Transport, server.Target);

            if (server.Error is not null)
            {
                AppendLine(builder, 1, $"error: {server.Error}");
            }
            else
            {
                if (DescribeConnectionAuth(server.AuthStatus) is { } authLine)
                {
                    AppendLine(builder, 1, $"auth: {authLine}");
                }

                AppendLine(builder, 1, $"capabilities: {FormatCapabilities(server.Capabilities)}");
                AppendSection(builder, "tools", server.Tools, FormatTool, jsonOptions);
                AppendSection(builder, "resources", server.Resources, FormatResource, jsonOptions);
                AppendSection(builder, "resource templates", server.ResourceTemplates, FormatResourceTemplate, jsonOptions);
                AppendSection(builder, "prompts", server.Prompts, FormatPrompt, jsonOptions);
            }

            if (index < report.Servers.Count - 1)
            {
                builder.AppendLine();
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// One-line summary of how the connection authenticated, or null when it doesn't apply
    /// (stdio, or no status captured) so the caller can omit the line entirely.
    /// </summary>
    internal static string? DescribeConnectionAuth(ConnectionAuthInfo? auth)
    {
        if (auth is null || auth.Mode == ConnectionAuthModes.None)
        {
            return null;
        }

        if (auth.Mode == ConnectionAuthModes.Anonymous)
        {
            return "anonymous (no credentials sent)";
        }

        return auth.Profile is not null
            ? $"authenticated (profile={auth.Profile}, kind={auth.Kind})"
            : $"authenticated (kind={auth.Kind})";
    }

    private static string FormatServerItems<T>(string label, IReadOnlyList<ServerItems<T>> servers, Func<T, JsonSerializerOptions, IEnumerable<string>> formatter, JsonSerializerOptions jsonOptions)
    {
        var builder = new StringBuilder();

        for (var index = 0; index < servers.Count; index++)
        {
            var server = servers[index];
            AppendServerHeader(builder, server.Name, server.Transport, server.Target);

            if (server.Error is not null)
            {
                AppendLine(builder, 1, $"error: {server.Error}");
            }
            else
            {
                AppendLine(builder, 1, $"{label}: {server.Items.Count}");
                foreach (var item in server.Items)
                {
                    foreach (var line in formatter(item, jsonOptions))
                    {
                        AppendLine(builder, 1, line);
                    }
                }
            }

            if (index < servers.Count - 1)
            {
                builder.AppendLine();
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatResourceList(ResourceListReport report, JsonSerializerOptions jsonOptions)
    {
        var builder = new StringBuilder();

        for (var index = 0; index < report.Servers.Count; index++)
        {
            var server = report.Servers[index];
            AppendServerHeader(builder, server.Name, server.Transport, server.Target);

            if (server.Error is not null)
            {
                AppendLine(builder, 1, $"error: {server.Error}");
            }
            else
            {
                AppendLine(builder, 1, $"resources: {server.Items.Count}");
                foreach (var item in server.Items)
                {
                    foreach (var line in FormatResource(item, jsonOptions))
                    {
                        AppendLine(builder, 1, line);
                    }
                }

                AppendLine(builder, 1, $"resource templates: {server.Templates.Count}");
                foreach (var template in server.Templates)
                {
                    foreach (var line in FormatResourceTemplate(template, jsonOptions))
                    {
                        AppendLine(builder, 1, line);
                    }
                }
            }

            if (index < report.Servers.Count - 1)
            {
                builder.AppendLine();
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatToolCall(ToolCallReport report, JsonSerializerOptions jsonOptions)
    {
        var builder = new StringBuilder();
        AppendServerHeader(builder, report.Server.Name, report.Server.Transport, report.Server.Target);
        AppendLine(builder, 1, $"tool: {report.ToolName}");
        AppendArguments(builder, report.Arguments, jsonOptions);

        if (report.Error is not null)
        {
            AppendLine(builder, 1, $"error: {report.Error}");
            return builder.ToString().TrimEnd();
        }

        AppendLine(builder, 1, $"progress events: {report.Progress.Count}");
        foreach (var progress in report.Progress)
        {
            AppendLine(builder, 1, $"- progress: {FormatProgress(progress)}");
        }

        AppendLine(builder, 1, $"is error: {report.Result?.IsError?.ToString().ToLowerInvariant() ?? "unknown"}");
        AppendContent(builder, report.Result?.Content ?? [], jsonOptions);
        AppendJsonBlock(builder, 1, "structured content", report.Result?.StructuredContent, jsonOptions);
        AppendJsonBlock(builder, 1, "meta", report.Result?.Meta, jsonOptions);
        return builder.ToString().TrimEnd();
    }

    private static string FormatRead(ReadReport report, JsonSerializerOptions jsonOptions)
    {
        var builder = new StringBuilder();
        AppendServerHeader(builder, report.Server.Name, report.Server.Transport, report.Server.Target);
        AppendLine(builder, 1, $"resource: {report.Resource}");
        AppendArguments(builder, report.Arguments, jsonOptions);

        if (report.Error is not null)
        {
            AppendLine(builder, 1, $"error: {report.Error}");
            return builder.ToString().TrimEnd();
        }

        AppendLine(builder, 1, $"contents: {report.Result?.Contents.Count ?? 0}");
        foreach (var content in report.Result?.Contents ?? [])
        {
            foreach (var line in FormatResourceContent(content, jsonOptions))
            {
                AppendLine(builder, 1, line);
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatPromptCall(PromptCallReport report, JsonSerializerOptions jsonOptions)
    {
        var builder = new StringBuilder();
        AppendServerHeader(builder, report.Server.Name, report.Server.Transport, report.Server.Target);
        AppendLine(builder, 1, $"prompt: {report.PromptName}");
        AppendArguments(builder, report.Arguments, jsonOptions);

        if (report.Error is not null)
        {
            AppendLine(builder, 1, $"error: {report.Error}");
            return builder.ToString().TrimEnd();
        }

        if (!string.IsNullOrWhiteSpace(report.Result?.Description))
        {
            AppendLine(builder, 1, $"description: {report.Result.Description}");
        }

        AppendLine(builder, 1, $"messages: {report.Result?.Messages.Count ?? 0}");
        foreach (var message in report.Result?.Messages ?? [])
        {
            AppendLine(builder, 1, $"- role: {message.Role ?? "unknown"}");
            foreach (var line in FormatContent(message.Content, jsonOptions))
            {
                AppendLine(builder, 2, line);
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendSection<T>(StringBuilder builder, string label, SectionResult<T> section, Func<T, JsonSerializerOptions, IEnumerable<string>> formatter, JsonSerializerOptions jsonOptions)
    {
        if (!section.Supported)
        {
            AppendLine(builder, 1, $"{label}: not supported");
            return;
        }

        if (section.Error is not null)
        {
            AppendLine(builder, 1, $"{label}: error: {section.Error}");
            return;
        }

        AppendLine(builder, 1, $"{label}: {section.Items.Count}");
        foreach (var item in section.Items)
        {
            foreach (var line in formatter(item, jsonOptions))
            {
                AppendLine(builder, 1, line);
            }
        }
    }

    private static IEnumerable<string> FormatTool(ToolInfo tool, JsonSerializerOptions jsonOptions)
    {
        yield return $"- {tool.Name}{FormatDescription(tool.Description)}";

        if (tool.InputSchema is not null)
        {
            yield return "  schema:";
            foreach (var line in Indent(FormatJson(tool.InputSchema, jsonOptions), 4))
            {
                yield return line;
            }
        }
    }

    private static IEnumerable<string> FormatResource(ResourceInfo resource, JsonSerializerOptions _) 
    {
        yield return $"- {resource.Name ?? "(unnamed)"}: {resource.Uri ?? "(no uri)"}{FormatMimeType(resource.MimeType)}";
        if (!string.IsNullOrWhiteSpace(resource.Description))
        {
            yield return $"  {resource.Description}";
        }
    }

    private static IEnumerable<string> FormatResourceTemplate(ResourceTemplateInfo template, JsonSerializerOptions _)
    {
        yield return $"- {template.Name ?? "(unnamed)"}: {template.UriTemplate ?? "(no template)"}{FormatMimeType(template.MimeType)}";
        if (!string.IsNullOrWhiteSpace(template.Description))
        {
            yield return $"  {template.Description}";
        }
    }

    private static IEnumerable<string> FormatPrompt(PromptInfo prompt, JsonSerializerOptions _)
    {
        yield return $"- {prompt.Name}{FormatDescription(prompt.Description)}";
        foreach (var argument in prompt.Arguments)
        {
            yield return $"  arg: {argument.Name ?? "(unnamed)"}{(argument.Required ? " (required)" : string.Empty)}{FormatDescription(argument.Description)}";
        }
    }

    private static void AppendContent(StringBuilder builder, IReadOnlyList<ContentBlockView> content, JsonSerializerOptions jsonOptions)
    {
        AppendLine(builder, 1, $"content: {content.Count}");
        foreach (var block in content)
        {
            foreach (var line in FormatContent(block, jsonOptions))
            {
                AppendLine(builder, 1, line);
            }
        }
    }

    private static IEnumerable<string> FormatContent(ContentBlockView? block, JsonSerializerOptions jsonOptions)
    {
        if (block is null)
        {
            yield break;
        }

        yield return $"- kind: {block.Kind}";

        if (!string.IsNullOrWhiteSpace(block.MimeType))
        {
            yield return $"  mime: {block.MimeType}";
        }

        if (!string.IsNullOrWhiteSpace(block.Text))
        {
            yield return "  text:";
            foreach (var line in Indent(block.Text, 4))
            {
                yield return line;
            }
        }

        if (block.ByteCount is not null)
        {
            yield return $"  bytes: {block.ByteCount}";
        }

        if (block.Resource is not null)
        {
            yield return "  resource:";
            foreach (var line in FormatResourceContent(block.Resource, jsonOptions))
            {
                yield return $"    {line}";
            }
        }

        if (block.Raw is not null)
        {
            yield return "  raw:";
            foreach (var line in Indent(FormatJson(block.Raw, jsonOptions), 4))
            {
                yield return line;
            }
        }
    }

    private static IEnumerable<string> FormatResourceContent(ResourceContentView content, JsonSerializerOptions jsonOptions)
    {
        yield return $"- kind: {content.Kind}";

        if (!string.IsNullOrWhiteSpace(content.Uri))
        {
            yield return $"  uri: {content.Uri}";
        }

        if (!string.IsNullOrWhiteSpace(content.MimeType))
        {
            yield return $"  mime: {content.MimeType}";
        }

        if (!string.IsNullOrWhiteSpace(content.Text))
        {
            yield return "  text:";
            foreach (var line in Indent(content.Text, 4))
            {
                yield return line;
            }
        }

        if (content.ByteCount is not null)
        {
            yield return $"  bytes: {content.ByteCount}";
        }

        if (content.Raw is not null)
        {
            yield return "  raw:";
            foreach (var line in Indent(FormatJson(content.Raw, jsonOptions), 4))
            {
                yield return line;
            }
        }
    }

    private static void AppendArguments(StringBuilder builder, JsonObject? arguments, JsonSerializerOptions jsonOptions)
    {
        if (arguments is null)
        {
            return;
        }

        AppendJsonBlock(builder, 1, "arguments", arguments, jsonOptions);
    }

    private static void AppendJsonBlock(StringBuilder builder, int indent, string label, JsonNode? value, JsonSerializerOptions jsonOptions)
    {
        if (value is null)
        {
            return;
        }

        AppendLine(builder, indent, $"{label}:");
        foreach (var line in Indent(FormatJson(value, jsonOptions), (indent + 1) * 2))
        {
            builder.AppendLine(line);
        }
    }

    private static void AppendServerHeader(StringBuilder builder, string name, string transport, string target)
        => builder.AppendLine($"{name} [{transport}] {target}");

    private static void AppendLine(StringBuilder builder, int indent, string text)
        => builder.AppendLine($"{new string(' ', indent * 2)}{text}");

    private static string FormatCapabilities(CapabilitySnapshot capabilities)
    {
        var names = new List<string>();
        if (capabilities.Tools)
        {
            names.Add("tools");
        }

        if (capabilities.Resources)
        {
            names.Add("resources");
        }

        if (capabilities.Prompts)
        {
            names.Add("prompts");
        }

        if (capabilities.Logging)
        {
            names.Add("logging");
        }

        if (capabilities.Completions)
        {
            names.Add("completions");
        }

        return names.Count == 0 ? "none" : string.Join(", ", names);
    }

    private static string FormatDescription(string? description)
        => string.IsNullOrWhiteSpace(description) ? string.Empty : $": {description}";

    private static string FormatMimeType(string? mimeType)
        => string.IsNullOrWhiteSpace(mimeType) ? string.Empty : $" [{mimeType}]";

    private static string FormatProgress(ProgressUpdate progress)
    {
        var pieces = new List<string>();
        if (progress.Progress is not null && progress.Total is not null)
        {
            pieces.Add($"{progress.Progress:0.##}/{progress.Total:0.##}");
        }
        else if (progress.Progress is not null)
        {
            pieces.Add(progress.Progress.Value.ToString("0.##"));
        }

        if (!string.IsNullOrWhiteSpace(progress.Message))
        {
            pieces.Add(progress.Message!);
        }

        pieces.Add(progress.Timestamp.ToString("u"));
        return string.Join(" | ", pieces);
    }

    private static string FormatJson(JsonNode node, JsonSerializerOptions jsonOptions)
        => node.ToJsonString(jsonOptions);

    private static IEnumerable<string> Indent(string text, int spaces)
    {
        var prefix = new string(' ', spaces);
        return text.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.None)
            .Select(line => prefix + line);
    }
}